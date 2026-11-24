using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StudIPDownloader
{
    class SyncOptions
    {
        public bool Express;
        public bool DownloadOverwrite;
        public bool Cleanup;
        public bool WindowsSafeNames;
        public int Parallel = 3;
        public string[] IgnoreFolders = Array.Empty<string>();
        public string[] IgnoreCourses = Array.Empty<string>();
        public string[] OnlyCourses = Array.Empty<string>();

        public static string[] SplitList(string list)
        {
            return (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    class SyncResult
    {
        public int Courses;
        public int SkippedCourses;
        public int NewFiles;
        public int UpdatedFiles;
        public int UnchangedFiles;
        public int FailedFiles;
        public int FolderErrors;
        public int MovedToTrash;
        public long Bytes;
        public TimeSpan Duration;

        public bool HasErrors => FailedFiles > 0 || FolderErrors > 0;
    }

    /// <summary>
    /// Synchronisiert die Dateibereiche aller Veranstaltungen in einen lokalen Ordner.
    /// </summary>
    class Synchronizer
    {
        public const string TrashFolderName = "_geloescht";
        const string TempSuffix = ".part";
        const long ProgressThreshold = 20 * 1024 * 1024;

        static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        readonly StudIPClient _client;
        readonly SyncOptions _options;
        readonly FileNames _names;
        readonly NameAllocator _allocator = new NameAllocator();
        readonly Dictionary<string, Semester> _semesterCache = new Dictionary<string, Semester>();
        readonly SemaphoreSlim _downloadSlots;
        readonly List<Task> _downloads = new List<Task>();
        readonly SyncResult _result = new SyncResult();

        string _root;

        /// <summary>Aktuelle Zeit, zum Testen austauschbar.</summary>
        public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

        public Synchronizer(StudIPClient client, SyncOptions options)
        {
            _client = client;
            _options = options;
            _names = new FileNames(options.WindowsSafeNames);
            _downloadSlots = new SemaphoreSlim(Math.Max(1, options.Parallel));
        }

        public async Task<SyncResult> Run(string localPath)
        {
            Stopwatch watch = Stopwatch.StartNew();
            _root = Path.GetFullPath(localPath);
            Directory.CreateDirectory(_root);

            if (string.IsNullOrEmpty(_client.UserId) && !await _client.IsAuthenticated())
            {
                throw new UnauthorizedException("Nicht eingeloggt.");
            }

            try
            {
                List<JsonObject> courses = await _client.GetCollection("users/" + _client.UserId + "/courses");
                Log.Info(courses.Count + " Veranstaltungen gefunden.");

                // Feste Reihenfolge, damit gleichnamige Veranstaltungen immer denselben Ordner bekommen
                foreach (JsonObject course in courses.OrderBy(c => Json.Attr(c, "title"), StringComparer.OrdinalIgnoreCase).ThenBy(c => (string)c["id"], StringComparer.Ordinal))
                {
                    await SyncCourse(course);
                }
            }
            finally
            {
                // Auch bei Fehlern laufende Downloads abschließen
                await Task.WhenAll(_downloads.ToArray());
            }

            _result.Duration = watch.Elapsed;
            return _result;
        }

        async Task SyncCourse(JsonObject course)
        {
            string courseId = (string)course["id"];
            string title = Json.Attr(course, "title") ?? courseId;

            if (!IsCourseSelected(course))
            {
                Log.Debug(title + " -> Skip (Kursfilter)");
                _result.SkippedCourses++;
                return;
            }

            string semesterName = "Allgemein";
            bool active = false;
            try
            {
                Semester start = await GetSemester(Json.RelationshipId(course, "start-semester"));
                if (start != null)
                {
                    if (!string.IsNullOrWhiteSpace(start.Name))
                    {
                        semesterName = _names.Replace(start.Name);
                    }

                    // Kein End-Semester bei vorhandener Beziehung = unbegrenzte Laufzeit
                    Semester end = Json.HasRelationship(course, "end-semester") ? await GetSemester(Json.RelationshipId(course, "end-semester")) : start;
                    DateTimeOffset now = Now();
                    active = (start.Start == null || start.Start <= now) && (end == null || end.End == null || end.End >= now);
                }
            }
            catch (UnauthorizedException)
            {
                throw;
            }
            catch (StudIPException ex)
            {
                Log.Warn(title + ": Semester konnte nicht geladen werden: " + ex.Message);
            }

            if (_options.Express && !active)
            {
                Log.Debug(title + " -> Skip (nicht im aktuellen Semester)");
                _result.SkippedCourses++;
                return;
            }

            Log.Info(title);
            _result.Courses++;

            string semesterPath = Path.Combine(_root, semesterName);
            string coursePath = Path.Combine(semesterPath, _allocator.Allocate(semesterPath, _names.Clean(title), false));
            CourseState state = new CourseState(coursePath);

            try
            {
                string rootFolderId = await GetRootFolderId(courseId);
                if (rootFolderId == null)
                {
                    Log.Debug("  Kein Dateibereich gefunden.");
                    return;
                }
                await SyncFolder(state, coursePath, rootFolderId, 1);
            }
            catch (UnauthorizedException)
            {
                throw;
            }
            catch (StudIPException ex)
            {
                Log.Error(title + ": Dateibereich konnte nicht geladen werden: " + ex.Message);
                _result.FolderErrors++;
                state.HadErrors = true;
            }

            if (_options.Cleanup)
            {
                CleanupCourse(state);
            }
        }

        bool IsCourseSelected(JsonObject course)
        {
            if (_options.OnlyCourses.Length > 0 && !_options.OnlyCourses.Any(f => CourseMatches(course, f)))
            {
                return false;
            }
            return !_options.IgnoreCourses.Any(f => CourseMatches(course, f));
        }

        public static bool CourseMatches(JsonObject course, string filter)
        {
            string title = Json.Attr(course, "title") ?? "";
            string number = Json.Attr(course, "course-number") ?? "";
            return string.Equals((string)course["id"], filter, StringComparison.OrdinalIgnoreCase)
                || (number.Length > 0 && string.Equals(number, filter, StringComparison.OrdinalIgnoreCase))
                || title.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        async Task<Semester> GetSemester(string semesterId)
        {
            if (string.IsNullOrEmpty(semesterId))
            {
                return null;
            }
            if (!_semesterCache.TryGetValue(semesterId, out Semester semester))
            {
                JsonObject data = (await _client.GetApi("semesters/" + semesterId))?["data"] as JsonObject;
                string token = Json.Attr(data, "token");
                string title = Json.Attr(data, "title");
                semester = new Semester(semesterId, string.IsNullOrWhiteSpace(token) ? title : token,
                    Json.Date(data?["attributes"]?["start"]), Json.Date(data?["attributes"]?["end"]));
                _semesterCache[semesterId] = semester;
            }
            return semester;
        }

        async Task<string> GetRootFolderId(string courseId)
        {
            List<JsonObject> folders = await _client.GetCollection("courses/" + courseId + "/folders");
            JsonObject root = folders.FirstOrDefault(f => Json.Attr(f, "folder-type") == "RootFolder")
                ?? folders.FirstOrDefault(f => Json.HasRelationship(f, "parent") && Json.RelationshipId(f, "parent") == null);
            return (string)root?["id"];
        }

        /// <summary>Feste Reihenfolge für die Namensvergabe bei Duplikaten: älteste zuerst.</summary>
        static IEnumerable<JsonObject> Ordered(IEnumerable<JsonObject> items)
        {
            return items
                .OrderBy(i => Json.Date(i["attributes"]?["mkdate"]) ?? DateTimeOffset.MaxValue)
                .ThenBy(i => (string)i["id"], StringComparer.Ordinal);
        }

        async Task SyncFolder(CourseState state, string localPath, string folderId, int depth)
        {
            Directory.CreateDirectory(localPath);
            state.Seen.Add(localPath);
            string indent = new string(' ', depth);

            try
            {
                foreach (JsonObject fileRef in Ordered(await _client.GetCollection("folders/" + folderId + "/file-refs")))
                {
                    string id = (string)fileRef["id"];
                    string name = Json.Attr(fileRef, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    string localName = _allocator.Allocate(localPath, _names.Clean(name), true);
                    string target = Path.Combine(localPath, localName);
                    state.Seen.Add(target);

                    bool? downloadable = Json.Bool(fileRef["attributes"]?["is-downloadable"]);
                    if (downloadable == false && !_options.DownloadOverwrite)
                    {
                        Log.Debug(indent + "Nicht downloadbar: " + name + " (" + id + "), ggf. downloadOverwrite|True verwenden");
                        continue;
                    }

                    Datei datei = new Datei(id, localName, Json.Long(fileRef["attributes"]?["filesize"]) ?? -1,
                        Json.Date(fileRef["attributes"]?["chdate"]));
                    QueueDownload(target, datei, indent);
                }
            }
            catch (UnauthorizedException)
            {
                throw;
            }
            catch (StudIPException ex)
            {
                Log.Error(Relative(localPath) + ": Dateien konnten nicht geladen werden: " + ex.Message);
                _result.FolderErrors++;
                state.HadErrors = true;
            }

            List<JsonObject> subfolders;
            try
            {
                subfolders = await _client.GetCollection("folders/" + folderId + "/folders");
            }
            catch (UnauthorizedException)
            {
                throw;
            }
            catch (StudIPException ex)
            {
                Log.Error(Relative(localPath) + ": Unterordner konnten nicht geladen werden: " + ex.Message);
                _result.FolderErrors++;
                state.HadErrors = true;
                return;
            }

            foreach (JsonObject folder in Ordered(subfolders))
            {
                string name = Json.Attr(folder, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                string localName = _allocator.Allocate(localPath, _names.Clean(name), false);
                string subPath = Path.Combine(localPath, localName);

                if (_options.IgnoreFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    Log.Debug(indent + "->" + name + " -> Skip");
                    state.Keep.Add(subPath); // lokal vorhandene Dateien nicht aufräumen
                    continue;
                }
                if (Json.Bool(folder["attributes"]?["is-readable"]) == false)
                {
                    Log.Debug(indent + "->" + name + " -> Skip (keine Leseberechtigung)");
                    state.Keep.Add(subPath);
                    continue;
                }
                Log.Debug(indent + "->" + localName);
                await SyncFolder(state, subPath, (string)folder["id"], depth + 1);
            }
        }

        void QueueDownload(string target, Datei datei, string indent)
        {
            bool exists = File.Exists(target);
            if (exists && IsUpToDate(target, datei))
            {
                Interlocked.Increment(ref _result.UnchangedFiles);
                return;
            }

            _downloads.Add(DownloadAsync(target, datei, exists));
        }

        async Task DownloadAsync(string target, Datei datei, bool exists)
        {
            await _downloadSlots.WaitAsync();
            string temp = target + TempSuffix;
            string display = Relative(target);
            try
            {
                Log.Debug("Lade " + display + (datei.Size >= 0 ? " (" + Log.FormatSize(datei.Size) + ")" : "") + "...");

                int lastQuarter = 0;
                Action<long, long> progress = null;
                if (datei.Size >= ProgressThreshold)
                {
                    progress = (done, total) =>
                    {
                        long size = total > 0 ? total : datei.Size;
                        int quarter = size > 0 ? (int)(done * 4 / size) : 0;
                        if (quarter > lastQuarter && quarter < 4)
                        {
                            lastQuarter = quarter;
                            Log.Info(string.Format("  {0}: {1} % ({2} / {3})", datei.Filename, quarter * 25, Log.FormatSize(done), Log.FormatSize(size)));
                        }
                    };
                }

                if (File.Exists(temp) && datei.ChangeDate.HasValue && File.GetLastWriteTimeUtc(temp) < datei.ChangeDate.Value.UtcDateTime)
                {
                    // Angefangener Download einer älteren Version
                    File.Delete(temp);
                }

                if (!await _client.DownloadFileRef(datei.Id, temp, progress))
                {
                    Log.Error("Download fehlgeschlagen: " + display + " (" + datei.Id + ")");
                    Interlocked.Increment(ref _result.FailedFiles);
                    TryDelete(temp);
                    return;
                }

                long size = new FileInfo(temp).Length;
                File.Move(temp, target, true);
                if (datei.ChangeDate.HasValue)
                {
                    File.SetLastWriteTimeUtc(target, datei.ChangeDate.Value.UtcDateTime);
                }

                Interlocked.Add(ref _result.Bytes, size);
                Interlocked.Increment(ref exists ? ref _result.UpdatedFiles : ref _result.NewFiles);
                Log.Info((exists ? "aktualisiert: " : "neu: ") + display + " (" + Log.FormatSize(size) + ")");
            }
            catch (Exception ex) when (ex is IOException || ex is System.Net.Http.HttpRequestException || ex is UnauthorizedAccessException
                || ex is OperationCanceledException || ex is StudIPException)
            {
                // .part Datei bleibt bei Netzwerkfehlern erhalten, beim nächsten Lauf wird fortgesetzt
                Log.Error("Download fehlgeschlagen: " + display + " (" + datei.Id + "): " + ex.Message);
                Interlocked.Increment(ref _result.FailedFiles);
            }
            finally
            {
                _downloadSlots.Release();
            }
        }

        static bool IsUpToDate(string path, Datei datei)
        {
            if (!datei.ChangeDate.HasValue)
            {
                return true;
            }
            long local = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
            if (local < datei.ChangeDate.Value.ToUnixTimeSeconds())
            {
                Log.Debug(datei.Filename + " wurde bearbeitet!");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Verschiebt lokale Dateien einer Veranstaltung, die es im Stud.IP nicht mehr gibt, nach _geloescht.
        /// Wird übersprungen, wenn beim Laden der Veranstaltung Fehler aufgetreten sind.
        /// </summary>
        void CleanupCourse(CourseState state)
        {
            if (state.HadErrors)
            {
                Log.Warn("Aufräumen von " + Relative(state.CoursePath) + " übersprungen, da Fehler aufgetreten sind.");
                return;
            }
            if (!Directory.Exists(state.CoursePath))
            {
                return;
            }

            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            foreach (string file in Directory.EnumerateFiles(state.CoursePath, "*", SearchOption.AllDirectories).ToList())
            {
                if (file.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) || state.Seen.Contains(file) || state.IsKept(file))
                {
                    continue;
                }
                try
                {
                    string trashTarget = Path.Combine(_root, TrashFolderName, Path.GetRelativePath(_root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(trashTarget));
                    if (File.Exists(trashTarget))
                    {
                        string ext = Path.GetExtension(trashTarget);
                        trashTarget = trashTarget.Substring(0, trashTarget.Length - ext.Length) + " (" + stamp + ")" + ext;
                    }
                    File.Move(file, trashTarget);
                    _result.MovedToTrash++;
                    Log.Info("gelöscht im Stud.IP, verschoben nach " + TrashFolderName + ": " + Relative(file));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Warn(Relative(file) + " konnte nicht verschoben werden: " + ex.Message);
                }
            }

            // Leere Ordner entfernen, die es im Stud.IP nicht mehr gibt (tiefste zuerst)
            foreach (string dir in Directory.EnumerateDirectories(state.CoursePath, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length).ToList())
            {
                if (state.Seen.Contains(dir) || state.IsKept(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    continue;
                }
                try
                {
                    Directory.Delete(dir);
                }
                catch (IOException)
                {
                }
            }
        }

        string Relative(string path)
        {
            return Path.GetRelativePath(_root, path);
        }

        static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
            }
        }

        class CourseState
        {
            public readonly string CoursePath;
            public readonly HashSet<string> Seen = new HashSet<string>(PathComparer);
            public readonly List<string> Keep = new List<string>();
            public bool HadErrors;

            public CourseState(string coursePath)
            {
                CoursePath = coursePath;
            }

            public bool IsKept(string path)
            {
                return Keep.Any(k => PathComparer.Equals(path, k)
                    || path.StartsWith(k + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
