using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace StudIPDownloader.Tests
{
    public class AuthTests : IDisposable
    {
        readonly FakeStudIP _server = new FakeStudIP();
        readonly TempDir _dir = new TempDir();

        public AuthTests()
        {
            FakeCourse course = _server.AddCourse("Kurs", _server.CurrentSemester());
            _server.AddFile(course.RootFolderId, "a.txt", "inhalt");
        }

        public void Dispose()
        {
            _server.Dispose();
            _dir.Dispose();
        }

        [Theory]
        [InlineData("Studip_Session")]
        [InlineData("Seminar_Session")]
        public async Task SessionCookieWorksWithOldAndNewName(string cookieName)
        {
            _server.SessionCookieName = cookieName;
            using StudIPClient client = new StudIPClient(_server.BaseUrl);
            client.UseSessionCookie(_server.ValidSession);
            Assert.True(await client.IsAuthenticated());
            client.UseSessionCookie("falsch");
            Assert.False(await client.IsAuthenticated());
        }

        [Fact]
        public async Task BasicAuth()
        {
            _server.BasicAuthEnabled = true;
            _server.Password = "pä|ss";
            using StudIPClient client = new StudIPClient(_server.BaseUrl);
            client.UseBasicAuth("max", "pä|ss");
            Assert.True(await client.IsAuthenticated());
        }

        [Fact]
        public async Task FormLoginFallback()
        {
            _server.FormLoginEnabled = true;
            using StudIPClient client = new StudIPClient(_server.BaseUrl);
            client.UseBasicAuth(_server.User, _server.Password);
            Assert.False(await client.IsAuthenticated());
            Assert.True(await client.TryFormLogin(_server.User, _server.Password));
            Assert.False(await client.TryFormLogin(_server.User, "falsch"));
        }

        [Fact]
        public async Task WrongUrlGivesHelpfulError()
        {
            using StudIPClient client = new StudIPClient("http://localhost:" + _server.Port + "/falsch/");
            client.UseSessionCookie("x");
            StudIPException ex = await Assert.ThrowsAsync<StudIPException>(() => client.IsAuthenticated());
            Assert.Contains("nicht gefunden", ex.Message);
        }

        OAuth2Session OAuth(string refreshToken = null)
        {
            int callbackPort = FakeStudIP.GetFreePort();
            OAuth2Session session = new OAuth2Session(new Uri(_server.BaseUrl), _server.OAuthClientId,
                redirectUri: "http://localhost:" + callbackPort + "/callback", refreshToken: refreshToken);
            return session;
        }

        [Fact]
        public async Task OAuthInteractiveLoginViaLoopbackRedirect()
        {
            using OAuth2Session session = OAuth();
            string savedRefreshToken = null;
            session.RefreshTokenChanged = t => savedRefreshToken = t;
            // "Browser": folgt der Weiterleitung von authorize auf die lokale Redirect-URI
            session.OpenBrowser = uri => Task.Run(async () =>
            {
                using HttpClient browser = new HttpClient();
                await browser.GetAsync(uri);
            });

            Assert.True(await session.InteractiveLogin(false));
            Assert.NotNull(session.AccessToken);
            Assert.NotNull(savedRefreshToken);

            using StudIPClient client = new StudIPClient(_server.BaseUrl);
            client.UseOAuth2(session);
            Assert.True(await client.IsAuthenticated());
        }

        [Fact]
        public async Task OAuthInteractiveLoginByPastingRedirectUrl()
        {
            using OAuth2Session session = OAuth();
            string redirect = null;
            session.OpenBrowser = uri =>
            {
                // Browser auf anderem Rechner: Weiterleitung nicht folgen, sondern URL "kopieren"
                using HttpClient browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                redirect = browser.GetAsync(uri).Result.Headers.Location.ToString();
            };

            Assert.True(await session.InteractiveLogin(true, async () =>
            {
                while (redirect == null) await Task.Delay(10);
                return redirect;
            }));
            Assert.NotNull(session.RefreshToken);
        }

        [Fact]
        public async Task OAuthRefreshesExpiredAccessTokenDuringSync()
        {
            _server.ValidRefreshTokens.Add("start-refresh");
            using OAuth2Session session = OAuth("start-refresh");
            using StudIPClient client = new StudIPClient(_server.BaseUrl);
            client.UseOAuth2(session);
            Assert.True(await client.IsAuthenticated());
            Assert.NotEqual("start-refresh", session.RefreshToken); // Refresh-Token wurde rotiert

            _server.RevokeAccessTokens(); // Access-Token serverseitig abgelaufen
            SyncResult result = await new Synchronizer(client, new SyncOptions()).Run(_dir.Path);
            Assert.Equal(1, result.NewFiles);
            Assert.Equal(2, _server.TokenRequests);
        }

        [Fact]
        public async Task OAuthInvalidRefreshTokenIsCleared()
        {
            using OAuth2Session session = OAuth("ungueltig");
            string saved = "x";
            session.RefreshTokenChanged = t => saved = t;
            Assert.False(await session.Refresh(null));
            Assert.False(session.HasRefreshToken);
            Assert.Equal("", saved);
        }

        // ------------------------------------------------------------------
        // Ende-zu-Ende über Program.Main
        // ------------------------------------------------------------------

        string WriteConfig(params string[] lines)
        {
            string file = _dir.Combine("config.ini");
            File.WriteAllLines(file, new[] { "StudIP|" + _server.BaseUrl, "Pfad|" + _dir.Combine("out"), "windowsSafeNames|True" }.Concat(lines));
            return file;
        }

        [Fact]
        public async Task MainWithCookieWritesFilesAndConfig()
        {
            string config = WriteConfig("Cookie|" + _server.ValidSession);
            int exit = await Program.Main(new[] { "--config", config, "--non-interactive" });
            Assert.Equal(Program.ExitOk, exit);
            Assert.True(File.Exists(_dir.Combine("out", "WiSe 26_27", "Kurs", "a.txt")));
            Assert.Contains("parallel|3", File.ReadAllLines(config));
        }

        [Fact]
        public async Task MainNonInteractiveWithInvalidCookieFails()
        {
            string config = WriteConfig("Cookie|abgelaufen");
            Assert.Equal(Program.ExitFatal, await Program.Main(new[] { "--config", config, "--non-interactive" }));
        }

        [Fact]
        public async Task MainPasswordFallsBackToFormLogin()
        {
            _server.FormLoginEnabled = true;
            string config = WriteConfig();
            int exit = await Program.Main(new[] { "--config", config, "--non-interactive", _server.User, _server.Password });
            Assert.Equal(Program.ExitOk, exit);
            string[] lines = File.ReadAllLines(config);
            Assert.Contains("User|" + _server.User, lines);
            Assert.DoesNotContain(lines, l => l.StartsWith("Pass|"));
        }

        [Fact]
        public async Task MainUsesStoredOAuthRefreshTokenAndSavesRotatedToken()
        {
            _server.ValidRefreshTokens.Add("gespeichert");
            string config = WriteConfig("oauthClientId|" + _server.OAuthClientId, "oauthRefreshToken|gespeichert");
            int exit = await Program.Main(new[] { "--config", config, "--non-interactive" });
            Assert.Equal(Program.ExitOk, exit);
            string token = File.ReadAllLines(config).Single(l => l.StartsWith("oauthRefreshToken|")).Substring("oauthRefreshToken|".Length);
            Assert.NotEqual("gespeichert", token);
            Assert.Contains(token, _server.ValidRefreshTokens);
        }

        [Fact]
        public async Task MainOAuthWithoutTokenFailsNonInteractive()
        {
            string config = WriteConfig("auth|oauth2", "oauthClientId|" + _server.OAuthClientId);
            Assert.Equal(Program.ExitFatal, await Program.Main(new[] { "--config", config, "--non-interactive" }));
        }

        [Fact]
        public async Task MainReturnsPartialExitCodeOnFailedDownloads()
        {
            FakeCourse course = _server.Courses[0];
            FakeFile broken = _server.AddFile(course.RootFolderId, "kaputt.txt", "x");
            _server.FailNext[broken.Id + "/content"] = 1000;
            string config = WriteConfig("Cookie|" + _server.ValidSession);
            Assert.Equal(Program.ExitPartial, await Program.Main(new[] { "--config", config, "--non-interactive" }));
        }
    }
}
