using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace StudIPDownloader.Tests
{
    class FakeFile
    {
        public string Id;
        public string Name;
        public byte[] Content;
        public DateTimeOffset Mkdate;
        public DateTimeOffset Chdate;
        public bool Downloadable = true;
    }

    class FakeFolder
    {
        public string Id;
        public string Name;
        public string Type = "StandardFolder";
        public string ParentId;
        public string CourseId;
        public bool Readable = true;
        public DateTimeOffset Mkdate = DateTimeOffset.UtcNow.AddDays(-30);
        public List<string> Children = new List<string>();
        public List<string> Files = new List<string>();
    }

    class FakeCourse
    {
        public string Id;
        public string Title;
        public string Number = "";
        public string StartSemester;
        public string EndSemester;
        public bool HasEndRelation = true;
        public string RootFolderId;
    }

    /// <summary>
    /// Minimaler Nachbau der Stud.IP JSON:API, des Login-Formulars und der OAuth2-Endpunkte.
    /// </summary>
    sealed class FakeStudIP : IDisposable
    {
        static FakeStudIP()
        {
            // Keine Proxy-Einstellungen aus der Umgebung für localhost verwenden
            System.Net.Http.HttpClient.DefaultProxy = new WebProxy();
        }

        public const string BasePath = "/studip/";
        readonly HttpListener _listener = new HttpListener();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public string BaseUrl { get; }
        public int Port { get; }

        // Daten
        public readonly Dictionary<string, JsonObject> Semesters = new Dictionary<string, JsonObject>();
        public readonly List<FakeCourse> Courses = new List<FakeCourse>();
        public readonly Dictionary<string, FakeFolder> Folders = new Dictionary<string, FakeFolder>();
        public readonly Dictionary<string, FakeFile> Files = new Dictionary<string, FakeFile>();

        // Authentifizierung
        public string SessionCookieName = "Studip_Session";
        public string ValidSession = "session-ok";
        public bool BasicAuthEnabled = false;
        public bool FormLoginEnabled = false;
        public string User = "max";
        public string Password = "geheim";
        public string OAuthClientId = "client-1";
        public readonly HashSet<string> ValidAccessTokens = new HashSet<string>();
        public readonly HashSet<string> ValidRefreshTokens = new HashSet<string>();
        public int TokenRequests;
        string _pendingCode;
        string _pendingChallenge;
        string _pendingRedirect;

        // Verhalten
        public int MaxPageLimit = 50;
        public readonly ConcurrentDictionary<string, int> FailNext = new ConcurrentDictionary<string, int>();
        public readonly ConcurrentDictionary<string, int> TruncateNext = new ConcurrentDictionary<string, int>();
        public int DownloadDelayMs = 0;
        public int MaxConcurrentDownloads;
        int _currentDownloads;
        public readonly ConcurrentQueue<string> RequestLog = new ConcurrentQueue<string>();
        public readonly ConcurrentQueue<string> RangeRequests = new ConcurrentQueue<string>();

        int _idCounter;

        public FakeStudIP()
        {
            Port = GetFreePort();
            BaseUrl = "http://localhost:" + Port + BasePath;
            _listener.Prefixes.Add("http://localhost:" + Port + "/");
            _listener.Start();
            Task.Run(Loop);
        }

        public static int GetFreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        // ------------------------------------------------------------------
        // Aufbau der Testdaten
        // ------------------------------------------------------------------

        public string NewId(string prefix) => prefix + Interlocked.Increment(ref _idCounter);

        public string AddSemester(string token, string title, DateTimeOffset start, DateTimeOffset end)
        {
            string id = NewId("sem");
            Semesters[id] = new JsonObject
            {
                ["title"] = title,
                ["token"] = token,
                ["start"] = start.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                ["end"] = end.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            };
            return id;
        }

        public string CurrentSemester() => AddSemester("WiSe 26/27", "Wintersemester 2026/27", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(100));
        public string OldSemester() => AddSemester("SoSe 20", "Sommersemester 2020", DateTimeOffset.UtcNow.AddDays(-2000), DateTimeOffset.UtcNow.AddDays(-1800));

        public FakeCourse AddCourse(string title, string semester, string endSemester = null, string number = "")
        {
            FakeCourse course = new FakeCourse { Id = NewId("course"), Title = title, StartSemester = semester, EndSemester = endSemester ?? semester, Number = number };
            FakeFolder root = new FakeFolder { Id = NewId("folder"), Name = "", Type = "RootFolder", CourseId = course.Id };
            Folders[root.Id] = root;
            course.RootFolderId = root.Id;
            Courses.Add(course);
            return course;
        }

        public FakeFolder AddFolder(string parentId, string name)
        {
            FakeFolder parent = Folders[parentId];
            FakeFolder folder = new FakeFolder { Id = NewId("folder"), Name = name, ParentId = parentId, CourseId = parent.CourseId };
            Folders[folder.Id] = folder;
            parent.Children.Add(folder.Id);
            return folder;
        }

        public FakeFile AddFile(string folderId, string name, string content, DateTimeOffset? chdate = null)
        {
            return AddFile(folderId, name, Encoding.UTF8.GetBytes(content), chdate);
        }

        public FakeFile AddFile(string folderId, string name, byte[] content, DateTimeOffset? chdate = null)
        {
            FakeFile file = new FakeFile
            {
                Id = NewId("file"),
                Name = name,
                Content = content,
                Mkdate = DateTimeOffset.UtcNow.AddDays(-10).AddSeconds(_idCounter),
                Chdate = chdate ?? DateTimeOffset.UtcNow.AddDays(-5),
            };
            Files[file.Id] = file;
            Folders[folderId].Files.Add(file.Id);
            return file;
        }

        public void RemoveFile(string fileId)
        {
            foreach (FakeFolder f in Folders.Values)
            {
                f.Files.Remove(fileId);
            }
            Files.Remove(fileId);
        }

        // ------------------------------------------------------------------
        // HTTP
        // ------------------------------------------------------------------

        async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(() => HandleSafe(ctx));
            }
        }

        async Task HandleSafe(HttpListenerContext ctx)
        {
            try
            {
                await Handle(ctx);
            }
            catch (Exception ex)
            {
                try
                {
                    Send(ctx, 500, "text/plain", Encoding.UTF8.GetBytes(ex.ToString()));
                }
                catch (Exception) { }
            }
        }

        static void Send(HttpListenerContext ctx, int status, string contentType, byte[] body, Dictionary<string, string> headers = null)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            if (headers != null)
            {
                foreach (var h in headers)
                {
                    ctx.Response.AddHeader(h.Key, h.Value);
                }
            }
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
        }

        static void SendJson(HttpListenerContext ctx, int status, JsonNode json)
        {
            Send(ctx, status, "application/vnd.api+json", Encoding.UTF8.GetBytes(json.ToJsonString()));
        }

        static void SendError(HttpListenerContext ctx, int status, string title)
        {
            SendJson(ctx, status, new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["status"] = status.ToString(), ["title"] = title }) });
        }

        bool IsAuthenticated(HttpListenerRequest req)
        {
            Cookie cookie = req.Cookies[SessionCookieName];
            if (cookie != null && cookie.Value == ValidSession)
            {
                return true;
            }
            string auth = req.Headers["Authorization"] ?? "";
            if (BasicAuthEnabled && auth == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(User + ":" + Password)))
            {
                return true;
            }
            if (auth.StartsWith("Bearer "))
            {
                lock (ValidAccessTokens)
                {
                    return ValidAccessTokens.Contains(auth.Substring(7));
                }
            }
            return false;
        }

        async Task Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string path = req.Url.AbsolutePath;
            RequestLog.Enqueue(req.HttpMethod + " " + req.Url.PathAndQuery);

            string failKey = FailNext.Keys.FirstOrDefault(k => path.EndsWith(k));
            if (failKey != null && FailNext.TryGetValue(failKey, out int remaining) && remaining > 0)
            {
                FailNext[failKey] = remaining - 1;
                Send(ctx, 503, "text/plain", Encoding.UTF8.GetBytes("busy"), new Dictionary<string, string> { ["Retry-After"] = "0" });
                return;
            }

            if (!path.StartsWith(BasePath))
            {
                SendError(ctx, 404, "Not Found");
                return;
            }
            string route = path.Substring(BasePath.Length);

            switch (route)
            {
                case "index.php":
                    await HandleLoginPage(ctx);
                    return;
                case "dispatch.php/start":
                    Send(ctx, 200, "text/html", Encoding.UTF8.GetBytes("<html>Start</html>"));
                    return;
                case "dispatch.php/api/oauth2/authorize":
                    HandleAuthorize(ctx);
                    return;
                case "dispatch.php/api/oauth2/token":
                    await HandleToken(ctx);
                    return;
            }

            if (!route.StartsWith("jsonapi.php/v1/"))
            {
                SendError(ctx, 404, "Not Found");
                return;
            }
            route = route.Substring("jsonapi.php/v1/".Length);

            if (!IsAuthenticated(req))
            {
                SendError(ctx, 401, "Unauthorized");
                return;
            }

            var q = HttpUtility.ParseQueryString(req.Url.Query);
            Match m;

            if (route == "users/me")
            {
                SendJson(ctx, 200, new JsonObject { ["data"] = new JsonObject { ["type"] = "users", ["id"] = "user1", ["attributes"] = new JsonObject { ["username"] = User } } });
            }
            else if (route == "users/user1/courses")
            {
                SendJson(ctx, 200, Page(Courses.Select(CourseJson), q));
            }
            else if ((m = Regex.Match(route, "^semesters/(\\w+)$")).Success && Semesters.ContainsKey(m.Groups[1].Value))
            {
                SendJson(ctx, 200, new JsonObject { ["data"] = new JsonObject { ["type"] = "semesters", ["id"] = m.Groups[1].Value, ["attributes"] = Semesters[m.Groups[1].Value].DeepClone() } });
            }
            else if ((m = Regex.Match(route, "^courses/(\\w+)/folders$")).Success)
            {
                SendJson(ctx, 200, Page(Folders.Values.Where(f => f.CourseId == m.Groups[1].Value).Select(FolderJson), q));
            }
            else if ((m = Regex.Match(route, "^folders/(\\w+)/folders$")).Success && Folders.ContainsKey(m.Groups[1].Value))
            {
                SendJson(ctx, 200, Page(Folders[m.Groups[1].Value].Children.Select(id => FolderJson(Folders[id])), q));
            }
            else if ((m = Regex.Match(route, "^folders/(\\w+)/file-refs$")).Success && Folders.ContainsKey(m.Groups[1].Value))
            {
                FakeFolder folder = Folders[m.Groups[1].Value];
                if (!folder.Readable)
                {
                    SendError(ctx, 403, "Forbidden");
                    return;
                }
                SendJson(ctx, 200, Page(folder.Files.Select(id => FileJson(Files[id])), q));
            }
            else if ((m = Regex.Match(route, "^file-refs/(\\w+)/content$")).Success && Files.ContainsKey(m.Groups[1].Value))
            {
                await HandleDownload(ctx, Files[m.Groups[1].Value]);
            }
            else
            {
                SendError(ctx, 404, "Not Found");
            }
        }

        JsonObject Page(IEnumerable<JsonObject> items, System.Collections.Specialized.NameValueCollection q)
        {
            List<JsonObject> all = items.ToList();
            int offset = int.TryParse(q["page[offset]"], out int o) ? o : 0;
            int limit = int.TryParse(q["page[limit]"], out int l) ? Math.Min(l, MaxPageLimit) : 30;
            return new JsonObject
            {
                ["meta"] = new JsonObject { ["page"] = new JsonObject { ["offset"] = offset, ["limit"] = limit, ["total"] = all.Count } },
                ["data"] = new JsonArray(all.Skip(offset).Take(limit).Select(i => (JsonNode)i).ToArray()),
            };
        }

        static string Iso(DateTimeOffset d) => d.ToString("yyyy-MM-ddTHH:mm:sszzz");

        JsonObject CourseJson(FakeCourse c)
        {
            JsonObject rel = new JsonObject
            {
                ["start-semester"] = new JsonObject { ["data"] = c.StartSemester == null ? null : new JsonObject { ["type"] = "semesters", ["id"] = c.StartSemester } },
            };
            if (c.HasEndRelation)
            {
                rel["end-semester"] = new JsonObject { ["data"] = c.EndSemester == null ? null : new JsonObject { ["type"] = "semesters", ["id"] = c.EndSemester } };
            }
            return new JsonObject
            {
                ["type"] = "courses",
                ["id"] = c.Id,
                ["attributes"] = new JsonObject { ["title"] = c.Title, ["course-number"] = c.Number },
                ["relationships"] = rel,
            };
        }

        JsonObject FolderJson(FakeFolder f)
        {
            return new JsonObject
            {
                ["type"] = "folders",
                ["id"] = f.Id,
                ["attributes"] = new JsonObject { ["name"] = f.Name, ["folder-type"] = f.Type, ["is-readable"] = f.Readable, ["mkdate"] = Iso(f.Mkdate) },
                ["relationships"] = new JsonObject { ["parent"] = new JsonObject { ["data"] = f.ParentId == null ? null : new JsonObject { ["type"] = "folders", ["id"] = f.ParentId } } },
            };
        }

        JsonObject FileJson(FakeFile f)
        {
            return new JsonObject
            {
                ["type"] = "file-refs",
                ["id"] = f.Id,
                ["attributes"] = new JsonObject
                {
                    ["name"] = f.Name,
                    ["filesize"] = f.Content.Length,
                    ["mkdate"] = Iso(f.Mkdate),
                    ["chdate"] = Iso(f.Chdate),
                    ["is-downloadable"] = f.Downloadable,
                },
            };
        }

        async Task HandleDownload(HttpListenerContext ctx, FakeFile file)
        {
            int now = Interlocked.Increment(ref _currentDownloads);
            int max;
            while ((max = MaxConcurrentDownloads) < now && Interlocked.CompareExchange(ref MaxConcurrentDownloads, now, max) != max) { }
            try
            {
                if (DownloadDelayMs > 0)
                {
                    await Task.Delay(DownloadDelayMs);
                }

                long start = 0;
                string range = ctx.Request.Headers["Range"];
                if (range != null)
                {
                    RangeRequests.Enqueue(file.Id + ":" + range);
                    Match m = Regex.Match(range, @"bytes=(\d+)-");
                    if (m.Success)
                    {
                        start = long.Parse(m.Groups[1].Value);
                    }
                }

                byte[] body = file.Content.Skip((int)start).ToArray();
                ctx.Response.StatusCode = start > 0 ? 206 : 200;
                ctx.Response.ContentType = "application/octet-stream";
                if (start > 0)
                {
                    ctx.Response.AddHeader("Content-Range", "bytes " + start + "-" + (file.Content.Length - 1) + "/" + file.Content.Length);
                }
                ctx.Response.ContentLength64 = body.Length;

                if (TruncateNext.TryGetValue(file.Id, out int truncate) && truncate > 0)
                {
                    TruncateNext[file.Id] = truncate - 1;
                    int half = body.Length / 2;
                    ctx.Response.OutputStream.Write(body, 0, half);
                    ctx.Response.OutputStream.Flush();
                    ctx.Response.Abort();
                    return;
                }

                ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.Close();
            }
            finally
            {
                Interlocked.Decrement(ref _currentDownloads);
            }
        }

        async Task HandleLoginPage(HttpListenerContext ctx)
        {
            if (!FormLoginEnabled)
            {
                Send(ctx, 200, "text/html", Encoding.UTF8.GetBytes("<html>SSO</html>"));
                return;
            }
            if (ctx.Request.HttpMethod == "GET")
            {
                string html = "<html><form class=\"default\" name=\"login\" method=\"post\" action=\"" + BasePath + "index.php?again=yes&amp;cancel_login=1\">"
                    + "<input type=\"hidden\" name=\"login_ticket\" value=\"T1\"><input type=\"hidden\" name=\"security_token\" value=\"S&amp;1\">"
                    + "<input type=\"text\" name=\"loginname\"><input type=\"password\" name=\"password\"><button type=\"submit\" name=\"Login\">Login</button></form></html>";
                Send(ctx, 200, "text/html", Encoding.UTF8.GetBytes(html));
                return;
            }

            string body = await new System.IO.StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var form = HttpUtility.ParseQueryString(body);
            if (form["loginname"] == User && form["password"] == Password && form["login_ticket"] == "T1" && form["security_token"] == "S&1")
            {
                ctx.Response.AppendHeader("Set-Cookie", SessionCookieName + "=" + ValidSession + "; path=" + BasePath);
                ctx.Response.Redirect(BasePath + "dispatch.php/start");
                ctx.Response.Close();
                return;
            }
            Send(ctx, 200, "text/html", Encoding.UTF8.GetBytes("<html>Login fehlgeschlagen</html>"));
        }

        void HandleAuthorize(HttpListenerContext ctx)
        {
            var q = HttpUtility.ParseQueryString(ctx.Request.Url.Query);
            if (q["client_id"] != OAuthClientId || q["response_type"] != "code" || q["code_challenge_method"] != "S256")
            {
                Send(ctx, 400, "text/plain", Encoding.UTF8.GetBytes("bad request"));
                return;
            }
            _pendingCode = NewId("code");
            _pendingChallenge = q["code_challenge"];
            _pendingRedirect = q["redirect_uri"];
            ctx.Response.Redirect(_pendingRedirect + "?code=" + _pendingCode + "&state=" + Uri.EscapeDataString(q["state"]));
            ctx.Response.Close();
        }

        async Task HandleToken(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref TokenRequests);
            string body = await new System.IO.StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            var form = HttpUtility.ParseQueryString(body);
            if (form["client_id"] != OAuthClientId)
            {
                SendJson(ctx, 401, new JsonObject { ["error"] = "invalid_client" });
                return;
            }

            if (form["grant_type"] == "authorization_code")
            {
                string challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"] ?? "")))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (form["code"] != _pendingCode || challenge != _pendingChallenge || form["redirect_uri"] != _pendingRedirect)
                {
                    SendJson(ctx, 400, new JsonObject { ["error"] = "invalid_grant" });
                    return;
                }
                _pendingCode = null;
            }
            else if (form["grant_type"] == "refresh_token")
            {
                lock (ValidRefreshTokens)
                {
                    if (!ValidRefreshTokens.Remove(form["refresh_token"] ?? ""))
                    {
                        SendJson(ctx, 400, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "refresh token revoked" });
                        return;
                    }
                }
            }
            else
            {
                SendJson(ctx, 400, new JsonObject { ["error"] = "unsupported_grant_type" });
                return;
            }

            string access = NewId("access");
            string refresh = NewId("refresh");
            lock (ValidAccessTokens) ValidAccessTokens.Add(access);
            lock (ValidRefreshTokens) ValidRefreshTokens.Add(refresh);
            SendJson(ctx, 200, new JsonObject { ["token_type"] = "Bearer", ["expires_in"] = 3600, ["access_token"] = access, ["refresh_token"] = refresh });
        }

        public void RevokeAccessTokens()
        {
            lock (ValidAccessTokens) ValidAccessTokens.Clear();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
        }
    }
}
