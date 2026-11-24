using System;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StudIPDownloader.Tests
{
    sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "studip-test-" + Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public string Combine(params string[] parts) => System.IO.Path.Combine(Path, System.IO.Path.Combine(parts));

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch (Exception) { }
        }
    }

    public class ConfigTests
    {
        [Fact]
        public void RoundTripKeepsValuesPipesInPasswordAndUnknownLines()
        {
            using TempDir dir = new TempDir();
            string file = dir.Combine("config.ini");
            File.WriteAllLines(file, new[]
            {
                "Cookie|abc", "Pfad|/data", "StudIP|https://example.org/", "express|True", "ignore|720p",
                "User|max", "Pass|p|ä ss ", "savePass|True", "parallel|5", "auth|oauth2", "oauthClientId|cid",
                "eigenerEintrag|wert",
            });

            Config config = Config.Load(file);
            Assert.True(config.Exists);
            Assert.Equal("p|ä ss ", config.Pass);
            Assert.Equal(5, config.Parallel);
            Assert.Equal(AuthMode.OAuth2, config.Auth);
            Assert.True(config.HasOAuthClient());
            Assert.False(config.WindowsSafeNames); // bestehende Konfiguration ohne Eintrag

            config.Save(file);
            string[] lines = File.ReadAllLines(file);
            Assert.Contains("Pass|p|ä ss ", lines);
            Assert.Contains("eigenerEintrag|wert", lines);
            Assert.Contains("windowsSafeNames|False", lines);

            Config again = Config.Load(file);
            Assert.Equal("p|ä ss ", again.Pass);
            Assert.False(again.WindowsSafeNames);
        }

        [Fact]
        public void NewConfigUsesWindowsSafeNames()
        {
            using TempDir dir = new TempDir();
            Config config = Config.Load(dir.Combine("fehlt.ini"));
            Assert.False(config.Exists);
            Assert.True(config.WindowsSafeNames);
        }

        [Fact]
        public void PasswordIsOnlySavedWhenRequested()
        {
            using TempDir dir = new TempDir();
            string file = dir.Combine("config.ini");
            new Config { User = "max", Pass = "geheim", SavePass = false }.Save(file);
            Assert.DoesNotContain(File.ReadAllLines(file), l => l.StartsWith("Pass|"));
        }

        [Fact]
        public void ResolvePathPrefersExplicitPath()
        {
            using TempDir dir = new TempDir();
            Assert.Equal(dir.Combine("x.ini"), Config.ResolvePath(dir.Combine("x.ini")));
        }
    }

    public class CommandLineTests
    {
        [Fact]
        public void LegacyPositionalArguments()
        {
            CommandLine cookie = CommandLine.Parse(new[] { "cookie123" });
            Assert.Equal("cookie123", cookie.Cookie);

            CommandLine user = CommandLine.Parse(new[] { "max", "geheim", "True" });
            Assert.Equal("max", user.User);
            Assert.Equal("geheim", user.Password);
            Assert.True(user.SavePass);
        }

        [Fact]
        public void Options()
        {
            CommandLine cl = CommandLine.Parse(new[] { "--config", "a.ini", "--path=/data", "--parallel", "4", "-v", "--cleanup", "--all", "--non-interactive", "--oauth-login" });
            Assert.Empty(cl.Errors);
            Assert.Equal("a.ini", cl.ConfigPath);
            Assert.Equal("/data", cl.Path);
            Assert.Equal(4, cl.Parallel);
            Assert.True(cl.Verbose);
            Assert.True(cl.Cleanup);
            Assert.False(cl.Express);
            Assert.True(cl.NonInteractive);
            Assert.True(cl.OAuthLogin);
            Assert.Null(cl.Cookie);
        }

        [Fact]
        public void Errors()
        {
            Assert.NotEmpty(CommandLine.Parse(new[] { "--gibtsnicht" }).Errors);
            Assert.NotEmpty(CommandLine.Parse(new[] { "--parallel", "x" }).Errors);
            Assert.NotEmpty(CommandLine.Parse(new[] { "--config" }).Errors);
        }
    }

    public class FileNameTests
    {
        [Theory]
        [InlineData("Mathe: Analysis/1", "Mathe Analysis1")]
        [InlineData("Frage?*<>|\"", "Frage")]
        [InlineData("Ende. . ", "Ende")]
        [InlineData("CON", "_CON")]
        [InlineData("nul.txt", "_nul.txt")]
        [InlineData("..", "_")]
        [InlineData("   ", "_")]
        public void WindowsSafe(string input, string expected)
        {
            Assert.Equal(expected, new FileNames(true).Clean(input));
        }

        [Fact]
        public void ReplaceForSemesterNames()
        {
            Assert.Equal("WiSe 23_24", new FileNames(true).Replace("WiSe 23/24"));
        }

        [Fact]
        public void LegacyBehaviourKeepsNames()
        {
            FileNames legacy = new FileNames(false);
            Assert.Equal("Analysis1", legacy.Clean("Analysis/1"));
            string longName = new string('a', 200) + ".pdf";
            Assert.Equal(longName, legacy.Clean(longName));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal("Mathe: Analysis", legacy.Clean("Mathe: Analysis"));
            }
        }

        [Fact]
        public void LongNamesAreTruncatedKeepingExtension()
        {
            string name = new FileNames(true).Clean(new string('a', 300) + ".pdf");
            Assert.Equal(FileNames.MaxNameLength, name.Length);
            Assert.EndsWith(".pdf", name);
        }

        [Fact]
        public void AllocatorMakesNamesUniqueIgnoringCase()
        {
            NameAllocator allocator = new NameAllocator();
            string dir = Path.Combine(Path.GetTempPath(), "x");
            Assert.Equal("VL.pdf", allocator.Allocate(dir, "VL.pdf", true));
            Assert.Equal("vl (2).pdf", allocator.Allocate(dir, "vl.pdf", true));
            Assert.Equal("VL (3).pdf", allocator.Allocate(dir, "VL.pdf", true));
            Assert.Equal("Ordner.v2", allocator.Allocate(dir, "Ordner.v2", false));
            Assert.Equal("Ordner.v2 (2)", allocator.Allocate(dir, "Ordner.v2", false));
            Assert.Equal("VL.pdf", allocator.Allocate(Path.Combine(dir, "sub"), "VL.pdf", true));
        }
    }

    public class ClientHelperTests
    {
        [Theory]
        [InlineData("elearning.uni-oldenburg.de", "https://elearning.uni-oldenburg.de/")]
        [InlineData("https://e-learning.tuhh.de/studip", "https://e-learning.tuhh.de/studip/")]
        [InlineData("https://studip.example.org/dispatch.php/start?cid=1", "https://studip.example.org/")]
        [InlineData("https://studip.example.org/sub/index.php", "https://studip.example.org/sub/")]
        [InlineData("http://localhost:8080/studip/jsonapi.php/v1/users/me", "http://localhost:8080/studip/")]
        public void NormalizeBaseUrl(string input, string expected)
        {
            Assert.Equal(expected, StudIPClient.NormalizeBaseUrl(input).ToString());
        }

        [Fact]
        public void JsonDates()
        {
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), Json.Date(JsonValue.Create(1700000000)));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), Json.Date(JsonValue.Create("1700000000")));
            Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(1)), Json.Date(JsonValue.Create("2024-01-02T03:04:05+01:00")));
            Assert.Null(Json.Date(null));
        }

        [Theory]
        [InlineData("http://localhost:8642/callback?code=abc&state=xyz", "abc", "xyz")]
        [InlineData("code=abc&state=xyz", "abc", "xyz")]
        [InlineData("  abc  ", "abc", null)]
        [InlineData("http://localhost:8642/callback?code=a%2Bb&state=s#frag", "a+b", "s")]
        public void OAuthCallbackParsing(string input, string code, string state)
        {
            var result = OAuth2Session.ParseCallback(input);
            Assert.Equal(code, result["code"]);
            Assert.Equal(state, result.TryGetValue("state", out string s) ? s : null);
        }

        [Fact]
        public void OAuthDefaultsAndAuthorizeUrl()
        {
            using OAuth2Session session = new OAuth2Session(new Uri("https://studip.example.org/"), "cid");
            Assert.Equal("https://studip.example.org/dispatch.php/api/oauth2/token", session.TokenUrl.ToString());
            Uri uri = session.BuildAuthorizeUri("challenge", "state1");
            Assert.StartsWith("https://studip.example.org/dispatch.php/api/oauth2/authorize?response_type=code&client_id=cid", uri.ToString());
            Assert.Contains("code_challenge_method=S256", uri.Query);
            Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A8642%2Fcallback", uri.Query);
        }
    }
}
