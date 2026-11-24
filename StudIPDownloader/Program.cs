using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace StudIPDownloader
{
    class Program
    {
        const string CookieHint = "Studip_Session Cookie aus dem Browser (bis Stud.IP 5.x: Seminar_Session): ";

        public const int ExitOk = 0;
        public const int ExitFatal = 1;
        public const int ExitPartial = 2;

        static bool _interactive = true;

        internal static async Task<int> Main(string[] args)
        {
            CommandLine cl = CommandLine.Parse(args);
            if (cl.Help)
            {
                Console.WriteLine(CommandLine.HelpText);
                return ExitOk;
            }
            if (cl.Errors.Count > 0)
            {
                foreach (string error in cl.Errors)
                {
                    Log.Error(error);
                }
                Console.WriteLine("Hilfe: StudIPDownloader --help");
                return ExitFatal;
            }

            _interactive = !cl.NonInteractive;
            string configPath = Config.ResolvePath(cl.ConfigPath);
            Config config = Config.Load(configPath);
            if (!config.Exists)
            {
                Log.Info("Neue Konfiguration: " + configPath);
            }

            // Kommandozeile überschreibt config.ini
            if (cl.StudIP != null) config.StudIP = cl.StudIP;
            if (cl.Path != null) config.Pfad = cl.Path;
            if (cl.Parallel.HasValue) config.Parallel = cl.Parallel.Value;
            if (cl.Express.HasValue) config.Express = cl.Express.Value;
            if (cl.Cleanup.HasValue) config.Cleanup = cl.Cleanup.Value;
            if (cl.SavePass.HasValue) config.SavePass = cl.SavePass.Value;
            Log.Verbose = cl.Verbose ?? config.Verbose;

            int exitCode = await Run(config, cl, configPath);
            TrySave(config, configPath);
            return exitCode;
        }

        static async Task<int> Run(Config config, CommandLine cl, string configPath)
        {
            if (string.IsNullOrWhiteSpace(config.StudIP))
            {
                config.StudIP = ReadLine("StudIP URL (https://elearning.uni-oldenburg.de/): ");
                if (string.IsNullOrWhiteSpace(config.StudIP))
                {
                    Log.Error("Keine StudIP URL angegeben.");
                    return ExitFatal;
                }
            }

            OAuth2Session oauth = null;
            try
            {
                using StudIPClient client = new StudIPClient(config.StudIP);
                config.StudIP = client.BaseUri.ToString();

                if (config.HasOAuthClient())
                {
                    oauth = new OAuth2Session(client.BaseUri, config.OAuthClientId, config.OAuthClientSecret, config.OAuthScope,
                        config.OAuthAuthorizeUrl, config.OAuthTokenUrl, config.OAuthRedirectUri, config.OAuthRefreshToken);
                    oauth.RefreshTokenChanged = token =>
                    {
                        // Sofort speichern: Stud.IP kann Refresh-Tokens bei jeder Erneuerung austauschen
                        config.OAuthRefreshToken = token;
                        TrySave(config, configPath);
                    };
                }

                if (!await Authenticate(client, config, cl, oauth))
                {
                    Log.Error("Login fehlgeschlagen.");
                    return ExitFatal;
                }

                if (string.IsNullOrWhiteSpace(config.Pfad))
                {
                    string pfad = ReadLine("Lokaler Pfad (" + Config.DefaultPath + "): ");
                    config.Pfad = string.IsNullOrWhiteSpace(pfad) ? Config.DefaultPath : pfad.Trim();
                }

                SyncOptions options = new SyncOptions
                {
                    Express = config.Express,
                    DownloadOverwrite = config.DownloadOverwrite,
                    Cleanup = config.Cleanup,
                    WindowsSafeNames = config.WindowsSafeNames,
                    Parallel = config.Parallel,
                    IgnoreFolders = SyncOptions.SplitList(config.Ignore),
                    IgnoreCourses = SyncOptions.SplitList(config.IgnoreCourses),
                    OnlyCourses = SyncOptions.SplitList(config.OnlyCourses),
                };

                SyncResult result;
                try
                {
                    result = await new Synchronizer(client, options).Run(config.Pfad);
                }
                catch (UnauthorizedException) when (!client.IsOAuth)
                {
                    // Session während des Syncs abgelaufen -> neuen Cookie abfragen und erneut versuchen
                    Log.Info("Session abgelaufen.");
                    if (!await LoginWithCookie(client, config, null, true))
                    {
                        return ExitFatal;
                    }
                    result = await new Synchronizer(client, options).Run(config.Pfad);
                }

                PrintSummary(result);
                return result.HasErrors ? ExitPartial : ExitOk;
            }
            catch (HttpRequestException ex)
            {
                Log.Error("Verbindungsfehler: " + FullMessage(ex));
                return ExitFatal;
            }
            catch (StudIPException ex)
            {
                Log.Error(ex.Message);
                return ExitFatal;
            }
            catch (IOException ex)
            {
                Log.Error(ex.Message);
                return ExitFatal;
            }
            finally
            {
                oauth?.Dispose();
            }
        }

        static void PrintSummary(SyncResult r)
        {
            Log.Info("");
            Log.Info(string.Format("Fertig in {0:hh\\:mm\\:ss}: {1} Veranstaltungen synchronisiert{2}, {3} neue und {4} aktualisierte Dateien ({5}), {6} unverändert.",
                r.Duration, r.Courses, r.SkippedCourses > 0 ? " (" + r.SkippedCourses + " übersprungen)" : "",
                r.NewFiles, r.UpdatedFiles, Log.FormatSize(r.Bytes), r.UnchangedFiles));
            if (r.MovedToTrash > 0)
            {
                Log.Info(r.MovedToTrash + " im Stud.IP gelöschte Dateien nach " + Synchronizer.TrashFolderName + " verschoben.");
            }
            if (r.HasErrors)
            {
                Log.Info(string.Format("{0} Dateien und {1} Ordner konnten nicht geladen werden.", r.FailedFiles, r.FolderErrors));
            }
        }

        static async Task<bool> Authenticate(StudIPClient client, Config config, CommandLine cl, OAuth2Session oauth)
        {
            if (cl.User != null)
            {
                config.User = cl.User;
                config.Pass = cl.Password;
            }

            AuthMode mode = config.Auth;
            if (cl.OAuthLogin || (mode == AuthMode.Auto && cl.Cookie == null && cl.User == null && oauth != null && oauth.HasRefreshToken))
            {
                mode = AuthMode.OAuth2;
            }
            else if (mode == AuthMode.Auto)
            {
                mode = cl.Cookie == null && (cl.User != null || config.HasCredentials()) ? AuthMode.Password : AuthMode.Cookie;
            }

            switch (mode)
            {
                case AuthMode.OAuth2:
                    return await LoginWithOAuth2(client, config, oauth, cl.OAuthLogin);

                case AuthMode.Password:
                    if (!config.HasCredentials() && cl.User == null)
                    {
                        Log.Error("auth|password: User und Pass fehlen in der config.ini.");
                        return false;
                    }
                    if (await LoginWithPassword(client, config))
                    {
                        return true;
                    }
                    if (config.Auth == AuthMode.Password)
                    {
                        return false;
                    }
                    Log.Info("Login mit Benutzername/Passwort fehlgeschlagen, verwende Session Cookie.");
                    return await LoginWithCookie(client, config, cl.Cookie, false);

                default:
                    return await LoginWithCookie(client, config, cl.Cookie, false);
            }
        }

        static async Task<bool> LoginWithOAuth2(StudIPClient client, Config config, OAuth2Session oauth, bool forceLogin)
        {
            if (oauth == null)
            {
                Log.Error("OAuth2: oauthClientId fehlt in der config.ini. Der OAuth2-Client muss vom Stud.IP Admin angelegt werden.");
                return false;
            }
            client.UseOAuth2(oauth);

            if (!forceLogin && oauth.HasRefreshToken && await oauth.Refresh(null) && await client.IsAuthenticated())
            {
                return true;
            }

            if (!_interactive)
            {
                Log.Error("OAuth2: Kein gültiger Refresh-Token. Bitte einmal interaktiv mit --oauth-login anmelden.");
                return false;
            }

            if (await oauth.InteractiveLogin(_interactive) && await client.IsAuthenticated())
            {
                Log.Info("OAuth2 Login erfolgreich.");
                return true;
            }
            return false;
        }

        static async Task<bool> LoginWithPassword(StudIPClient client, Config config)
        {
            client.UseBasicAuth(config.User, config.Pass);
            if (await client.IsAuthenticated())
            {
                return true;
            }

            Log.Info("HTTP Basic Auth nicht erfolgreich, versuche Login über das Login-Formular...");
            return await client.TryFormLogin(config.User, config.Pass);
        }

        static async Task<bool> LoginWithCookie(StudIPClient client, Config config, string cookieArgument, bool forceNew)
        {
            if (cookieArgument != null)
            {
                config.Cookie = cookieArgument;
            }

            if (!forceNew && !string.IsNullOrWhiteSpace(config.Cookie))
            {
                client.UseSessionCookie(config.Cookie);
                if (await client.IsAuthenticated())
                {
                    return true;
                }
                Log.Info("Session Cookie ungültig oder abgelaufen.");
            }

            for (int i = 0; i < 3; i++)
            {
                string cookie = ReadLine(CookieHint);
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    return false;
                }
                config.Cookie = cookie.Trim();
                client.UseSessionCookie(config.Cookie);
                if (await client.IsAuthenticated())
                {
                    return true;
                }
                Log.Info("Session Cookie ungültig oder abgelaufen.");
            }
            return false;
        }

        static void TrySave(Config config, string path)
        {
            try
            {
                config.Save(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Error("config.ini konnte nicht gespeichert werden (" + path + "): " + ex.Message);
            }
        }

        static string FullMessage(Exception ex)
        {
            string message = ex.Message;
            for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                message += " -> " + inner.Message;
            }
            return message;
        }

        static string ReadLine(string prompt)
        {
            if (!_interactive)
            {
                return "";
            }
            Console.WriteLine(prompt);
            return Console.ReadLine() ?? "";
        }
    }
}
