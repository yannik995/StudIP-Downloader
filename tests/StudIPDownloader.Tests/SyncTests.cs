using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace StudIPDownloader.Tests
{
    public class SyncTests : IDisposable
    {
        readonly FakeStudIP _server = new FakeStudIP();
        readonly TempDir _dir = new TempDir();

        public void Dispose()
        {
            _server.Dispose();
            _dir.Dispose();
        }

        StudIPClient Client()
        {
            StudIPClient client = new StudIPClient(_server.BaseUrl) { RetryDelay = _ => TimeSpan.Zero };
            client.UseSessionCookie(_server.ValidSession);
            return client;
        }

        async Task<SyncResult> Sync(SyncOptions options = null)
        {
            using StudIPClient client = Client();
            return await new Synchronizer(client, options ?? new SyncOptions { WindowsSafeNames = true }).Run(_dir.Path);
        }

        string Local(params string[] parts) => _dir.Combine(parts);

        [Fact]
        public async Task DownloadsStructureAndSkipsUnchangedFilesOnSecondRun()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Mathe: Analysis/1", sem);
            _server.AddFile(course.RootFolderId, "info.txt", "Hallo");
            FakeFolder folien = _server.AddFolder(course.RootFolderId, "Folien");
            FakeFile vl = _server.AddFile(folien.Id, "VL1.pdf", "vl1", DateTimeOffset.UtcNow.AddDays(-3));
            FakeFolder alt = _server.AddFolder(folien.Id, "Alt");
            _server.AddFile(alt.Id, "alt.pdf", "alt");

            SyncResult first = await Sync();
            Assert.Equal(3, first.NewFiles);
            Assert.False(first.HasErrors);

            string coursePath = Local("WiSe 26_27", "Mathe Analysis1");
            Assert.Equal("Hallo", File.ReadAllText(Path.Combine(coursePath, "info.txt")));
            Assert.Equal("vl1", File.ReadAllText(Path.Combine(coursePath, "Folien", "VL1.pdf")));
            Assert.True(File.Exists(Path.Combine(coursePath, "Folien", "Alt", "alt.pdf")));
            // Änderungsdatum = chdate aus Stud.IP
            Assert.Equal(vl.Chdate.ToUnixTimeSeconds(), new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(coursePath, "Folien", "VL1.pdf"))).ToUnixTimeSeconds());
            Assert.Empty(Directory.GetFiles(_dir.Path, "*.part", SearchOption.AllDirectories));

            SyncResult second = await Sync();
            Assert.Equal(0, second.NewFiles + second.UpdatedFiles);
            Assert.Equal(3, second.UnchangedFiles);

            // Datei im Stud.IP geändert -> wird aktualisiert
            vl.Content = Encoding.UTF8.GetBytes("vl1 neu");
            vl.Chdate = DateTimeOffset.UtcNow.AddMinutes(-1);
            SyncResult third = await Sync();
            Assert.Equal(1, third.UpdatedFiles);
            Assert.Equal("vl1 neu", File.ReadAllText(Path.Combine(coursePath, "Folien", "VL1.pdf")));
        }

        [Fact]
        public async Task PaginationBeyondServerLimit()
        {
            _server.MaxPageLimit = 7;
            string sem = _server.OldSemester();
            for (int i = 0; i < 23; i++)
            {
                FakeCourse c = _server.AddCourse("Kurs " + i, sem);
                _server.AddFile(c.RootFolderId, "f.txt", "x" + i);
            }
            SyncResult result = await Sync();
            Assert.Equal(23, result.Courses);
            Assert.Equal(23, result.NewFiles);
        }

        [Fact]
        public async Task DuplicateNamesGetCounterAndStayStable()
        {
            string sem = _server.CurrentSemester();
            FakeCourse a = _server.AddCourse("Tutorium", sem);
            FakeCourse b = _server.AddCourse("Tutorium", sem);
            _server.AddFile(a.RootFolderId, "Blatt.pdf", "erstes");
            _server.AddFile(a.RootFolderId, "blatt.pdf", "zweites");
            _server.AddFile(b.RootFolderId, "Blatt.pdf", "kurs b");
            _server.AddFolder(a.RootFolderId, "Blatt.pdf"); // Ordner mit gleichem Namen wie Datei

            SyncResult first = await Sync();
            Assert.Equal(3, first.NewFiles);

            string semPath = Local("WiSe 26_27");
            string[] courseDirs = Directory.GetDirectories(semPath).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "Tutorium", "Tutorium (2)" }, courseDirs);

            string[] contents = Directory.GetFiles(Path.Combine(semPath, "Tutorium")).Select(File.ReadAllText)
                .Concat(Directory.GetFiles(Path.Combine(semPath, "Tutorium (2)")).Select(File.ReadAllText)).OrderBy(c => c).ToArray();
            Assert.Equal(new[] { "erstes", "kurs b", "zweites" }, contents);

            SyncResult second = await Sync();
            Assert.Equal(0, second.NewFiles + second.UpdatedFiles);
        }

        [Fact]
        public async Task FiltersExpressIgnoreAndNotDownloadable()
        {
            string current = _server.CurrentSemester();
            string old = _server.OldSemester();
            FakeCourse mathe = _server.AddCourse("Mathe", current, number: "1.01");
            FakeCourse unbegrenzt = _server.AddCourse("Unbegrenzt", old);
            unbegrenzt.EndSemester = null; // unbegrenzte Laufzeit
            FakeCourse alt = _server.AddCourse("Altes Seminar", old);
            FakeCourse ignoriert = _server.AddCourse("Tutorium Mathe", current);
            foreach (FakeCourse c in new[] { mathe, unbegrenzt, alt, ignoriert })
            {
                _server.AddFile(c.RootFolderId, "datei.txt", c.Title);
            }
            FakeFolder video = _server.AddFolder(mathe.RootFolderId, "720p");
            _server.AddFile(video.Id, "video.mp4", "v");
            FakeFolder abgaben = _server.AddFolder(mathe.RootFolderId, "Abgaben");
            abgaben.Readable = false;
            _server.AddFile(mathe.RootFolderId, "geschuetzt.pdf", "x").Downloadable = false;

            SyncResult result = await Sync(new SyncOptions
            {
                WindowsSafeNames = true,
                Express = true,
                IgnoreFolders = new[] { "720P" },
                IgnoreCourses = new[] { "tutorium" },
            });

            Assert.Equal(2, result.Courses); // Mathe + Unbegrenzt
            Assert.Equal(2, result.SkippedCourses);
            Assert.True(File.Exists(Local("WiSe 26_27", "Mathe", "datei.txt")));
            Assert.True(File.Exists(Local("SoSe 20", "Unbegrenzt", "datei.txt")));
            Assert.False(Directory.Exists(Local("SoSe 20", "Altes Seminar")));
            Assert.False(Directory.Exists(Local("WiSe 26_27", "Mathe", "720p")));
            Assert.False(File.Exists(Local("WiSe 26_27", "Mathe", "geschuetzt.pdf")));

            // Whitelist über Veranstaltungsnummer
            SyncResult only = await Sync(new SyncOptions { WindowsSafeNames = true, OnlyCourses = new[] { "1.01" }, DownloadOverwrite = true });
            Assert.Equal(1, only.Courses);
            Assert.True(File.Exists(Local("WiSe 26_27", "Mathe", "geschuetzt.pdf")));
        }

        [Fact]
        public async Task CleanupMovesDeletedFilesButKeepsIgnoredFolders()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Kurs", sem);
            FakeFile keep = _server.AddFile(course.RootFolderId, "bleibt.txt", "1");
            FakeFolder sub = _server.AddFolder(course.RootFolderId, "Alt");
            FakeFile gone = _server.AddFile(sub.Id, "weg.txt", "2");
            FakeFolder videos = _server.AddFolder(course.RootFolderId, "Videos");
            _server.AddFile(videos.Id, "v.mp4", "3");

            await Sync(new SyncOptions { WindowsSafeNames = true, Cleanup = true });
            string coursePath = Local("WiSe 26_27", "Kurs");
            File.WriteAllText(Path.Combine(coursePath, "Videos", "lokal.txt"), "eigene Notiz");

            // Datei und Ordner im Stud.IP gelöscht, Videos jetzt ignoriert
            _server.RemoveFile(gone.Id);
            _server.Folders[course.RootFolderId].Children.Remove(sub.Id);

            SyncResult result = await Sync(new SyncOptions { WindowsSafeNames = true, Cleanup = true, IgnoreFolders = new[] { "Videos" } });
            Assert.Equal(1, result.MovedToTrash);
            Assert.True(File.Exists(Path.Combine(coursePath, "bleibt.txt")));
            Assert.False(Directory.Exists(Path.Combine(coursePath, "Alt")));
            Assert.Equal("2", File.ReadAllText(Local(Synchronizer.TrashFolderName, "WiSe 26_27", "Kurs", "Alt", "weg.txt")));
            Assert.True(File.Exists(Path.Combine(coursePath, "Videos", "v.mp4")));
            Assert.True(File.Exists(Path.Combine(coursePath, "Videos", "lokal.txt")));
        }

        [Fact]
        public async Task CleanupIsSkippedWhenFolderCouldNotBeLoaded()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Kurs", sem);
            _server.AddFile(course.RootFolderId, "a.txt", "1");
            await Sync(new SyncOptions { WindowsSafeNames = true, Cleanup = true });

            _server.Folders[course.RootFolderId].Readable = false; // file-refs liefert 403
            SyncResult result = await Sync(new SyncOptions { WindowsSafeNames = true, Cleanup = true });
            Assert.True(result.HasErrors);
            Assert.Equal(0, result.MovedToTrash);
            Assert.True(File.Exists(Local("WiSe 26_27", "Kurs", "a.txt")));
        }

        [Fact]
        public async Task RetriesTransientErrorsAndResumesDownloads()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Kurs", sem);
            byte[] big = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
            FakeFile file = _server.AddFile(course.RootFolderId, "gross.bin", big);

            _server.FailNext["users/user1/courses"] = 2;
            _server.FailNext[file.Id + "/content"] = 1;
            _server.TruncateNext[file.Id] = 1;

            SyncResult result = await Sync();
            Assert.False(result.HasErrors);
            Assert.Equal(big, File.ReadAllBytes(Local("WiSe 26_27", "Kurs", "gross.bin")));
            Assert.Single(_server.RangeRequests);
            Assert.StartsWith(file.Id + ":bytes=", _server.RangeRequests.First());
        }

        [Fact]
        public async Task GivesUpAfterMaxAttempts()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Kurs", sem);
            FakeFile file = _server.AddFile(course.RootFolderId, "a.txt", "1");
            _server.FailNext[file.Id + "/content"] = 100;

            SyncResult result = await Sync();
            Assert.Equal(1, result.FailedFiles);
            Assert.False(File.Exists(Local("WiSe 26_27", "Kurs", "a.txt")));
        }

        [Fact]
        public async Task DownloadsInParallel()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Kurs", sem);
            for (int i = 0; i < 8; i++)
            {
                _server.AddFile(course.RootFolderId, "f" + i + ".txt", "x");
            }
            _server.DownloadDelayMs = 300;

            await Sync(new SyncOptions { WindowsSafeNames = true, Parallel = 4 });
            Assert.InRange(_server.MaxConcurrentDownloads, 2, 4);
        }

        [Fact]
        public async Task LegacyNamesStayUnchangedWithoutWindowsSafeNames()
        {
            string sem = _server.CurrentSemester();
            FakeCourse course = _server.AddCourse("Mathe: Analysis", sem);
            _server.AddFile(course.RootFolderId, "a.txt", "1");
            await Sync(new SyncOptions { WindowsSafeNames = false });
            string expected = OperatingSystem.IsWindows() ? "Mathe Analysis" : "Mathe: Analysis";
            Assert.True(File.Exists(Local("WiSe 26_27", expected, "a.txt")));
        }
    }
}
