using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StudIPDownloader
{
    class StudIPException : Exception
    {
        public HttpStatusCode? StatusCode { get; }

        public StudIPException(string message, HttpStatusCode? statusCode = null, Exception inner = null) : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }

    class UnauthorizedException : StudIPException
    {
        public UnauthorizedException(string message) : base(message, HttpStatusCode.Unauthorized) { }
    }

    class SyncOptions
    {
        public bool Express;
        public bool DownloadOverwrite;
        public string[] IgnoreFolders = Array.Empty<string>();
        public string[] IgnoreCourses = Array.Empty<string>();
        public string[] OnlyCourses = Array.Empty<string>();

        public static string[] SplitList(string list)
        {
            return (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>
    /// Client für die Stud.IP JSON:API (jsonapi.php/v1/).
    /// Die alte REST-API (api.php) wurde mit Stud.IP 6.0 entfernt.
    /// </summary>
    class StudIPClient : IDisposable
    {
        /// <summary>
        /// Name des Session Cookies: Seit Stud.IP 6.0 "Studip_Session", davor "Seminar_Session".
        /// Es werden immer beide gesetzt, der Server wertet nur den passenden aus.
        /// </summary>
        public static readonly string[] SessionCookieNames = { "Studip_Session", "Seminar_Session" };

        const string JsonApiMediaType = "application/vnd.api+json";
        const int PageLimit = 100;
        const string UserAgent = "StudIP-Downloader";

        readonly Uri _base;
        readonly Uri _apiBase;
        readonly SyncOptions _options;

        HttpClient _http;
        CookieContainer _cookies;
        string _userId;

        readonly Dictionary<string, Semester> _semesterCache = new Dictionary<string, Semester>();

        public StudIPClient(string baseUrl, SyncOptions options)
        {
            _base = NormalizeBaseUrl(baseUrl);
            _apiBase = new Uri(_base, "jsonapi.php/v1/");
            _options = options;
            ResetHttpClient();
        }

        public static Uri NormalizeBaseUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? "").Trim();
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = "https://" + baseUrl;
            }
            // Häufig wird die URL einer Unterseite eingetragen, z.B. .../index.php oder .../dispatch.php/start
            foreach (string suffix in new[] { "/dispatch.php", "/index.php", "/api.php", "/jsonapi.php" })
            {
                int idx = baseUrl.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    baseUrl = baseUrl.Substring(0, idx);
                }
            }
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }
            return new Uri(baseUrl);
        }

        void ResetHttpClient()
        {
            _http?.Dispose();
            _cookies = new CookieContainer();
            HttpClientHandler handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromHours(2) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _userId = null;
        }

        // ---------------------------------------------------------------------
        // Authentifizierung
        // ---------------------------------------------------------------------

        public void UseSessionCookie(string value)
        {
            ResetHttpClient();
            value = (value ?? "").Trim();
            foreach (string name in SessionCookieNames)
            {
                _cookies.Add(_base, new Cookie(name, value, "/"));
            }
        }

        public void UseBasicAuth(string user, string password)
        {
            ResetHttpClient();
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        /// <summary>
        /// Prüft, ob die aktuelle Authentifizierung gültig ist, und merkt sich die User-ID.
        /// </summary>
        public async Task<bool> IsAuthenticated()
        {
            try
            {
                JsonNode me = await GetApi("users/me");
                _userId = (string)me?["data"]?["id"];
                return !string.IsNullOrEmpty(_userId);
            }
            catch (UnauthorizedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Login über das normale Stud.IP Login-Formular. Wird verwendet, wenn HTTP Basic Auth
        /// auf dem Server nicht erlaubt ist. Funktioniert nur mit der Standard-Anmeldung von Stud.IP
        /// (nicht mit Single Sign-On wie Shibboleth).
        /// </summary>
        public async Task<bool> TryFormLogin(string user, string password)
        {
            ResetHttpClient();
            try
            {
                Uri loginPage = new Uri(_base, "index.php?again=yes");
                using HttpResponseMessage pageResponse = await _http.GetAsync(loginPage);
                if (!pageResponse.IsSuccessStatusCode)
                {
                    return false;
                }
                string html = await pageResponse.Content.ReadAsStringAsync();
                Uri pageUri = pageResponse.RequestMessage?.RequestUri ?? loginPage;

                Match form = Regex.Matches(html, @"<form\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                    .FirstOrDefault(m => Regex.IsMatch(m.Value, @"name\s*=\s*[""']loginname[""']", RegexOptions.IgnoreCase));
                if (form == null)
                {
                    return false;
                }

                Match formTag = Regex.Match(form.Value, @"<form\b[^>]*>", RegexOptions.IgnoreCase);
                string action = GetAttribute(formTag.Value, "action");
                Uri actionUri = string.IsNullOrEmpty(action) ? pageUri : new Uri(pageUri, WebUtility.HtmlDecode(action));

                Dictionary<string, string> fields = new Dictionary<string, string>();
                foreach (Match input in Regex.Matches(form.Value, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    string name = GetAttribute(input.Value, "name");
                    string type = (GetAttribute(input.Value, "type") ?? "text").ToLowerInvariant();
                    if (string.IsNullOrEmpty(name) || type == "submit" || type == "button" || type == "checkbox")
                    {
                        continue;
                    }
                    fields[WebUtility.HtmlDecode(name)] = WebUtility.HtmlDecode(GetAttribute(input.Value, "value") ?? "");
                }
                fields["loginname"] = user;
                fields["password"] = password;
                fields.TryAdd("resolution", "1920x1080");
                fields.TryAdd("device_pixel_ratio", "1");

                using HttpResponseMessage loginResponse = await _http.PostAsync(actionUri, new FormUrlEncodedContent(fields));
            }
            catch (HttpRequestException)
            {
                return false;
            }
            return await IsAuthenticated();
        }

        static string GetAttribute(string tag, string attribute)
        {
            Match m = Regex.Match(tag, @"\b" + attribute + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return null;
            }
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }

        // ---------------------------------------------------------------------
        // JSON:API
        // ---------------------------------------------------------------------

        public async Task<JsonNode> GetApi(string path)
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonApiMediaType));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await _http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedException("Nicht eingeloggt (401 Unauthorized): Cookie abgelaufen oder Zugangsdaten falsch.");
            }
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound && path == "users/me")
                {
                    throw new StudIPException("Die JSON:API wurde unter " + _apiBase + " nicht gefunden. Ist die StudIP URL korrekt?", response.StatusCode);
                }
                throw new StudIPException(string.Format("{0} ({1} {2}){3}", path, (int)response.StatusCode, response.ReasonPhrase,
                    ExtractErrorDetail(body)), response.StatusCode);
            }

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("html") || body.TrimStart().StartsWith("<"))
            {
                // Typischerweise eine Weiterleitung auf die Login-Seite
                throw new UnauthorizedException("Server hat HTML statt JSON geliefert (vermutlich Weiterleitung auf die Login-Seite).");
            }

            try
            {
                return JsonNode.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new StudIPException("Ungültige Antwort der JSON:API für " + path + ": " + ex.Message, response.StatusCode, ex);
            }
        }

        static string ExtractErrorDetail(string body)
        {
            try
            {
                JsonArray errors = JsonNode.Parse(body)?["errors"] as JsonArray;
                string detail = string.Join("; ", errors?.Select(e => (string)e?["detail"] ?? (string)e?["title"]).Where(s => !string.IsNullOrEmpty(s)) ?? Enumerable.Empty<string>());
                return string.IsNullOrEmpty(detail) ? "" : ": " + detail;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Lädt alle Einträge einer (paginierten) JSON:API Collection.
        /// </summary>
        public async Task<List<JsonObject>> GetCollection(string path)
        {
            List<JsonObject> result = new List<JsonObject>();
            HashSet<string> seen = new HashSet<string>();
            string separator = path.Contains('?') ? "&" : "?";
            int offset = 0;

            while (true)
            {
                JsonNode page = await GetApi(path + separator + "page[offset]=" + offset + "&page[limit]=" + PageLimit);
                JsonArray data = page?["data"] as JsonArray;
                if (data == null || data.Count == 0)
                {
                    break;
                }

                int added = 0;
                foreach (JsonObject item in data.OfType<JsonObject>())
                {
                    string key = (string)item["type"] + "/" + (string)item["id"];
                    if (seen.Add(key))
                    {
                        result.Add(item);
                        added++;
                    }
                }
                offset += data.Count;

                long? total = TryGetLong(page?["meta"]?["page"]?["total"]);
                if (added == 0 || (total.HasValue && offset >= total.Value) || (!total.HasValue && page?["links"]?["next"] == null && data.Count < PageLimit))
                {
                    break;
                }
            }
            return result;
        }

        static string Attr(JsonObject resource, string name)
        {
            JsonNode node = resource?["attributes"]?[name];
            return node is JsonValue value && value.TryGetValue(out string s) ? s : node?.ToString();
        }

        static string RelationshipId(JsonObject resource, string relationship)
        {
            JsonNode data = resource?["relationships"]?[relationship]?["data"];
            return data is JsonObject obj ? (string)obj["id"] : null;
        }

        static long? TryGetLong(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out long l)) return l;
                if (value.TryGetValue(out int i)) return i;
                if (value.TryGetValue(out double d)) return (long)d;
                if (value.TryGetValue(out string s) && long.TryParse(s, out long parsed)) return parsed;
            }
            return null;
        }

        static bool? TryGetBool(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out bool b)) return b;
                if (value.TryGetValue(out string s) && bool.TryParse(s, out bool parsed)) return parsed;
            }
            return null;
        }

        /// <summary>
        /// Datumswerte liefert die JSON:API im ISO 8601 Format, zur Sicherheit werden auch Unix-Timestamps akzeptiert.
        /// </summary>
        static DateTimeOffset? ParseDate(JsonNode node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }
            if (value.TryGetValue(out string s))
            {
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
                {
                    return date;
                }
                if (long.TryParse(s, out long unixFromString))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixFromString);
                }
                return null;
            }
            long? unix = TryGetLong(node);
            return unix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(unix.Value) : null;
        }

        async Task<Semester> GetSemester(string semesterId)
        {
            if (string.IsNullOrEmpty(semesterId))
            {
                return null;
            }
            if (!_semesterCache.TryGetValue(semesterId, out Semester semester))
            {
                JsonObject data = (await GetApi("semesters/" + semesterId))?["data"] as JsonObject;
                string token = Attr(data, "token");
                string title = Attr(data, "title");
                semester = new Semester(semesterId, string.IsNullOrWhiteSpace(token) ? title : token,
                    ParseDate(data?["attributes"]?["start"]), ParseDate(data?["attributes"]?["end"]));
                _semesterCache[semesterId] = semester;
            }
            return semester;
        }

        // ---------------------------------------------------------------------
        // Synchronisation
        // ---------------------------------------------------------------------

        public async Task SyncFiles(string localPath)
        {
            if (string.IsNullOrEmpty(_userId) && !await IsAuthenticated())
            {
                throw new UnauthorizedException("Nicht eingeloggt.");
            }

            List<JsonObject> courses = await GetCollection("users/" + _userId + "/courses");
            Console.WriteLine(courses.Count + " Veranstaltungen gefunden.");
            DateTimeOffset now = DateTimeOffset.UtcNow;

            foreach (JsonObject course in courses)
            {
                string courseId = (string)course["id"];
                string title = Attr(course, "title") ?? courseId;
                Console.Write(title);

                if (!IsCourseSelected(course))
                {
                    Console.WriteLine(" -> Skip (Kursfilter)");
                    continue;
                }

                string semesterName = "Allgemein";
                bool active = false;
                try
                {
                    Semester start = await GetSemester(RelationshipId(course, "start-semester"));
                    if (start != null)
                    {
                        if (!string.IsNullOrWhiteSpace(start.Name))
                        {
                            semesterName = ReplaceInvalidChars(start.Name);
                        }

                        // Kein End-Semester bei vorhandener Beziehung = unbegrenzte Laufzeit
                        bool hasEndRelation = course["relationships"]?["end-semester"] != null;
                        Semester end = hasEndRelation ? await GetSemester(RelationshipId(course, "end-semester")) : start;
                        active = (start.Start == null || start.Start <= now) && (end == null || end.End == null || end.End >= now);
                    }
                }
                catch (StudIPException ex)
                {
                    Console.Write(" (Semester konnte nicht geladen werden: " + ex.Message + ")");
                }

                if (_options.Express && !active)
                {
                    Console.WriteLine(" -> Skip");
                    continue;
                }
                Console.WriteLine();

                try
                {
                    string rootFolderId = await GetRootFolderId(courseId);
                    if (rootFolderId == null)
                    {
                        Console.WriteLine("  Kein Dateibereich gefunden.");
                        continue;
                    }
                    string coursePath = Path.Combine(localPath, semesterName, RemoveInvalidChars(title));
                    await SyncFolder(coursePath, rootFolderId, 1);
                }
                catch (UnauthorizedException)
                {
                    throw;
                }
                catch (StudIPException ex)
                {
                    Console.WriteLine("  Fehler beim Laden des Dateibereichs: " + ex.Message);
                }
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

        static bool CourseMatches(JsonObject course, string filter)
        {
            string title = Attr(course, "title") ?? "";
            string number = Attr(course, "course-number") ?? "";
            return string.Equals((string)course["id"], filter, StringComparison.OrdinalIgnoreCase)
                || (number.Length > 0 && string.Equals(number, filter, StringComparison.OrdinalIgnoreCase))
                || title.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        async Task<string> GetRootFolderId(string courseId)
        {
            List<JsonObject> folders = await GetCollection("courses/" + courseId + "/folders");
            JsonObject root = folders.FirstOrDefault(f => Attr(f, "folder-type") == "RootFolder")
                ?? folders.FirstOrDefault(f => f["relationships"]?["parent"] != null && RelationshipId(f, "parent") == null);
            return (string)root?["id"];
        }

        async Task SyncFolder(string localPath, string folderId, int ebene)
        {
            Directory.CreateDirectory(localPath);
            string indent = new string(' ', ebene);

            try
            {
                foreach (JsonObject fileRef in await GetCollection("folders/" + folderId + "/file-refs"))
                {
                    string id = (string)fileRef["id"];
                    string name = Attr(fileRef, "name");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    bool? downloadable = TryGetBool(fileRef["attributes"]?["is-downloadable"]);
                    if (downloadable == false && !_options.DownloadOverwrite)
                    {
                        Console.WriteLine(indent + "Warnung: " + name + " (" + id + ") nicht downloadbar (ggf. downloadOverwrite|True in config Datei verwenden)");
                        continue;
                    }

                    Datei datei = new Datei(id, RemoveInvalidChars(name), TryGetLong(fileRef["attributes"]?["filesize"]) ?? -1,
                        ParseDate(fileRef["attributes"]?["chdate"]));
                    await DownloadFile(localPath, datei, ebene);
                }
            }
            catch (UnauthorizedException)
            {
                throw;
            }
            catch (StudIPException ex)
            {
                Console.WriteLine(indent + "Fehler beim Laden der Dateien von Ordner " + folderId + ": " + ex.Message);
            }

            List<JsonObject> subfolders;
            try
            {
                subfolders = await GetCollection("folders/" + folderId + "/folders");
            }
            catch (UnauthorizedException)
            {
                throw;
            }
            catch (StudIPException ex)
            {
                Console.WriteLine(indent + "Fehler beim Laden der Unterordner von " + folderId + ": " + ex.Message);
                return;
            }

            foreach (JsonObject folder in subfolders)
            {
                string name = Attr(folder, "name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                if (_options.IgnoreFolders.Contains(name))
                {
                    Console.WriteLine(indent + "->" + name + " -> Skip");
                    continue;
                }
                if (TryGetBool(folder["attributes"]?["is-readable"]) == false)
                {
                    Console.WriteLine(indent + "->" + name + " -> Skip (keine Leseberechtigung)");
                    continue;
                }
                name = RemoveInvalidChars(name);
                Console.WriteLine(indent + "->" + name);
                await SyncFolder(Path.Combine(localPath, name), (string)folder["id"], ebene + 1);
            }
        }

        async Task DownloadFile(string localPath, Datei datei, int ebene)
        {
            string target = Path.Combine(localPath, datei.Filename);
            if (IsUpToDate(target, datei))
            {
                return;
            }

            Console.WriteLine(new string(' ', ebene) + datei.Filename);
            string temp = target + ".part";
            try
            {
                bool ok = await DownloadTo(new Uri(_apiBase, "file-refs/" + datei.Id + "/content"), temp, false);
                if (!ok)
                {
                    // Fallback über die Weboberfläche (z.B. bei sehr großen Dateien)
                    ok = await DownloadTo(new Uri(_base, "sendfile.php?force_download=1&type=0&file_id=" + Uri.EscapeDataString(datei.Id)), temp, true);
                }
                if (!ok)
                {
                    Console.WriteLine(new string(' ', ebene) + "Fehler beim Downloaden der Datei " + datei.Filename + " (" + datei.Id + ")");
                    return;
                }

                File.Move(temp, target, true);
                if (datei.ChangeDate.HasValue)
                {
                    File.SetLastWriteTimeUtc(target, datei.ChangeDate.Value.UtcDateTime);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is UnauthorizedAccessException || ex is TaskCanceledException)
            {
                Console.WriteLine(new string(' ', ebene) + "Fehler beim Downloaden der Datei " + datei.Filename + " (" + datei.Id + "): " + ex.Message);
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }

        async Task<bool> DownloadTo(Uri uri, string file, bool rejectHtml)
        {
            using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            if (rejectHtml && (response.Content.Headers.ContentType?.MediaType ?? "").Contains("html")
                && !file.EndsWith(".html.part", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".htm.part", StringComparison.OrdinalIgnoreCase))
            {
                return false; // vermutlich Login- oder Fehlerseite
            }
            using Stream source = await response.Content.ReadAsStreamAsync();
            using FileStream target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target);
            return true;
        }

        static bool IsUpToDate(string path, Datei datei)
        {
            if (!File.Exists(path))
            {
                return false;
            }
            if (datei.ChangeDate.HasValue)
            {
                long local = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds();
                if (local < datei.ChangeDate.Value.ToUnixTimeSeconds())
                {
                    Console.WriteLine(datei.Filename + " wurde bearbeitet!");
                    return false;
                }
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // Helper
        // ---------------------------------------------------------------------

        static string RemoveInvalidChars(string filename)
        {
            string name = string.Concat(filename.Trim().Split(Path.GetInvalidFileNameChars()));
            return name == "." || name == ".." ? name.Replace('.', '_') : name;
        }

        static string ReplaceInvalidChars(string filename, string replace = "_")
        {
            return string.Join(replace, filename.Split(Path.GetInvalidFileNameChars()));
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }
}
