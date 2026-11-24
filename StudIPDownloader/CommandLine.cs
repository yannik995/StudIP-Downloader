using System;
using System.Collections.Generic;

namespace StudIPDownloader
{
    /// <summary>
    /// Kommandozeilenparameter. Positionsparameter wie bisher:
    /// <c>StudIPDownloader [Cookie]</c> oder <c>StudIPDownloader Benutzer Passwort [True]</c>.
    /// </summary>
    class CommandLine
    {
        public string ConfigPath;
        public string Path;
        public string StudIP;
        public int? Parallel;
        public bool? Verbose;
        public bool? Cleanup;
        public bool? Express;
        public bool OAuthLogin;
        public bool NonInteractive;
        public bool Help;

        public string Cookie;
        public string User;
        public string Password;
        public bool? SavePass;

        public List<string> Errors = new List<string>();

        public static CommandLine Parse(string[] args)
        {
            CommandLine cl = new CommandLine();
            List<string> positional = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string inlineValue = null;
                if (arg.StartsWith("--") && arg.Contains('='))
                {
                    inlineValue = arg.Substring(arg.IndexOf('=') + 1);
                    arg = arg.Substring(0, arg.IndexOf('='));
                }

                string Value()
                {
                    if (inlineValue != null)
                    {
                        return inlineValue;
                    }
                    if (i + 1 < args.Length)
                    {
                        return args[++i];
                    }
                    cl.Errors.Add("Fehlender Wert für " + arg);
                    return null;
                }

                switch (arg)
                {
                    case "-h":
                    case "--help":
                    case "/?":
                        cl.Help = true;
                        break;
                    case "-c":
                    case "--config":
                        cl.ConfigPath = Value();
                        break;
                    case "-p":
                    case "--path":
                        cl.Path = Value();
                        break;
                    case "--url":
                        cl.StudIP = Value();
                        break;
                    case "--parallel":
                        string n = Value();
                        if (int.TryParse(n, out int parallel) && parallel >= 1)
                        {
                            cl.Parallel = Math.Min(parallel, 16);
                        }
                        else if (n != null)
                        {
                            cl.Errors.Add("Ungültiger Wert für --parallel: " + n);
                        }
                        break;
                    case "-v":
                    case "--verbose":
                        cl.Verbose = true;
                        break;
                    case "--cleanup":
                        cl.Cleanup = true;
                        break;
                    case "--no-cleanup":
                        cl.Cleanup = false;
                        break;
                    case "--express":
                        cl.Express = true;
                        break;
                    case "--all":
                        cl.Express = false;
                        break;
                    case "--oauth-login":
                        cl.OAuthLogin = true;
                        break;
                    case "--non-interactive":
                        cl.NonInteractive = true;
                        break;
                    default:
                        if (arg.StartsWith("--"))
                        {
                            cl.Errors.Add("Unbekannter Parameter: " + arg);
                        }
                        else
                        {
                            positional.Add(args[i]);
                        }
                        break;
                }
            }

            if (positional.Count == 1)
            {
                cl.Cookie = positional[0];
            }
            else if (positional.Count >= 2)
            {
                cl.User = positional[0];
                cl.Password = positional[1];
                if (positional.Count > 2 && bool.TryParse(positional[2], out bool savePass))
                {
                    cl.SavePass = savePass;
                }
            }
            return cl;
        }

        public const string HelpText =
@"StudIP-Downloader - lädt die Dateien aller Stud.IP Veranstaltungen herunter.

Aufruf:
  StudIPDownloader [Optionen]                       Einstellungen aus der config.ini
  StudIPDownloader [Optionen] <Cookie>              Login mit Session Cookie
  StudIPDownloader [Optionen] <Benutzer> <Passwort> [True]
                                                    Login mit Benutzername/Passwort (True = Passwort speichern)

Optionen:
  -c, --config <Datei>   config.ini verwenden (Standard: aktueller Ordner, sonst neben der Anwendung)
  -p, --path <Ordner>    Lokaler Zielordner
      --url <URL>        StudIP URL
      --parallel <n>     Anzahl gleichzeitiger Downloads (Standard 3)
      --express          Nur Veranstaltungen des aktuellen Semesters
      --all              Alle Semester (überschreibt express aus der config.ini)
      --cleanup          Im Stud.IP gelöschte Dateien lokal nach _geloescht verschieben
      --no-cleanup       Aufräumen deaktivieren
      --oauth-login      OAuth2 Login (neu) durchführen
      --non-interactive  Keine Eingaben abfragen (für Aufgabenplanung, cron, Docker)
  -v, --verbose          Ausführliche Ausgabe
  -h, --help             Diese Hilfe

Exit-Codes: 0 = OK, 1 = Fehler/Login fehlgeschlagen, 2 = einzelne Dateien fehlgeschlagen";
    }
}
