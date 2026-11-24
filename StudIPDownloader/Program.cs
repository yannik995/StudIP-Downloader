using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace StudIPDownloader
{
    class Program
    {
        const string ConfigFile = "config.ini";
        const string CookieHint = "Studip_Session Cookie aus dem Browser (bis Stud.IP 5.x: Seminar_Session): ";

        static async Task<int> Main(string[] args)
        {
            Config config = Config.Load(ConfigFile);

            string cookieArgument = null;
            string userArgument = null;
            string passArgument = null;
            if (args.Length == 1) // Cookie Login mit Parameter
            {
                cookieArgument = args[0];
            }
            else if (args.Length >= 2) // User/Pass Login mit Parameter
            {
                userArgument = args[0];
                passArgument = args[1];
                if (args.Length > 2 && bool.TryParse(args[2], out bool savePass))
                {
                    config.SavePass = savePass;
                }
            }

            if (string.IsNullOrWhiteSpace(config.StudIP))
            {
                config.StudIP = ReadLine("StudIP URL (https://elearning.uni-oldenburg.de/): ");
            }

            int exitCode = 0;
            try
            {
                SyncOptions options = new SyncOptions
                {
                    Express = config.Express,
                    DownloadOverwrite = config.DownloadOverwrite,
                    IgnoreFolders = SyncOptions.SplitList(config.Ignore),
                    IgnoreCourses = SyncOptions.SplitList(config.IgnoreCourses),
                    OnlyCourses = SyncOptions.SplitList(config.OnlyCourses),
                };

                using StudIPClient client = new StudIPClient(config.StudIP, options);
                config.StudIP = StudIPClient.NormalizeBaseUrl(config.StudIP).ToString();

                if (!await Authenticate(client, config, cookieArgument, userArgument, passArgument))
                {
                    Console.WriteLine("Login fehlgeschlagen.");
                    exitCode = 1;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(config.Pfad))
                    {
                        string pfad = ReadLine("Lokaler Pfad (" + Config.DefaultPath + "): ");
                        config.Pfad = string.IsNullOrWhiteSpace(pfad) ? Config.DefaultPath : pfad.Trim();
                    }

                    try
                    {
                        await client.SyncFiles(config.Pfad);
                    }
                    catch (UnauthorizedException)
                    {
                        // Session während des Syncs abgelaufen -> neuen Cookie abfragen und erneut versuchen
                        Console.WriteLine("Session abgelaufen.");
                        if (await LoginWithCookie(client, config, null, true))
                        {
                            await client.SyncFiles(config.Pfad);
                        }
                        else
                        {
                            exitCode = 1;
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine("Verbindungsfehler: " + FullMessage(ex));
                exitCode = 1;
            }
            catch (StudIPException ex)
            {
                Console.WriteLine("Fehler: " + ex.Message);
                exitCode = 1;
            }

            config.Save(ConfigFile);
            return exitCode;
        }

        static async Task<bool> Authenticate(StudIPClient client, Config config, string cookieArgument, string userArgument, string passArgument)
        {
            if (userArgument != null)
            {
                config.User = userArgument;
                config.Pass = passArgument;
            }

            // Benutzername/Passwort, wenn vorhanden und kein Cookie als Parameter übergeben wurde
            if (cookieArgument == null && (userArgument != null || config.HasCredentials()))
            {
                client.UseBasicAuth(config.User, config.Pass);
                if (await client.IsAuthenticated())
                {
                    return true;
                }

                Console.WriteLine("HTTP Basic Auth nicht erfolgreich, versuche Login über das Login-Formular...");
                if (await client.TryFormLogin(config.User, config.Pass))
                {
                    return true;
                }
                Console.WriteLine("Login mit Benutzername/Passwort fehlgeschlagen, verwende Session Cookie.");
            }

            return await LoginWithCookie(client, config, cookieArgument, false);
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
                Console.WriteLine("Session Cookie ungültig oder abgelaufen.");
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
                Console.WriteLine("Session Cookie ungültig oder abgelaufen.");
            }
            return false;
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
            Console.WriteLine(prompt);
            return Console.ReadLine() ?? "";
        }
    }
}
