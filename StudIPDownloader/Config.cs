using System;
using System.Collections.Generic;
using System.IO;

namespace StudIPDownloader
{
    enum AuthMode
    {
        /// <summary>OAuth2 (wenn Refresh-Token vorhanden) → Benutzername/Passwort → Cookie</summary>
        Auto,
        Cookie,
        Password,
        OAuth2,
    }

    /// <summary>
    /// Liest und schreibt die config.ini (Format: Schlüssel|Wert pro Zeile).
    /// </summary>
    class Config
    {
        public const string FileName = "config.ini";

        public static readonly string DefaultPath = OperatingSystem.IsWindows()
            ? @"C:\StudIP-Sync\"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "StudIP-Sync");

        public string Cookie = "";
        public string Pfad = "";
        public string StudIP = "";
        public bool Express = false;
        /// <summary>Kommagetrennte Liste von Ordnernamen, die nicht synchronisiert werden.</summary>
        public string Ignore = "";
        /// <summary>Kommagetrennte Liste von Kursen (Titel-Teilstring, Veranstaltungsnummer oder Kurs-ID), die übersprungen werden.</summary>
        public string IgnoreCourses = "";
        /// <summary>Kommagetrennte Liste von Kursen. Wenn gesetzt, werden nur diese Kurse synchronisiert.</summary>
        public string OnlyCourses = "";
        public bool DownloadOverwrite = false;
        public string User = "";
        public string Pass = "";
        public bool SavePass = false;

        /// <summary>Anzahl gleichzeitiger Downloads.</summary>
        public int Parallel = 3;
        public bool Verbose = false;
        /// <summary>Lokale Dateien, die es im Stud.IP nicht mehr gibt, nach "_geloescht" verschieben.</summary>
        public bool Cleanup = false;
        /// <summary>
        /// Dateinamen auf allen Betriebssystemen so bereinigen, dass sie auch unter Windows gültig sind
        /// (wichtig für Netzlaufwerke, NAS und exFAT). Neue Konfigurationen: True, bestehende ohne Eintrag: False
        /// (damit sich die Ordnerstruktur nicht ändert).
        /// </summary>
        public bool WindowsSafeNames = true;

        public AuthMode Auth = AuthMode.Auto;
        public string OAuthClientId = "";
        public string OAuthClientSecret = "";
        public string OAuthScope = "";
        public string OAuthAuthorizeUrl = "";
        public string OAuthTokenUrl = "";
        public string OAuthRedirectUri = "";
        public string OAuthRefreshToken = "";

        /// <summary>Unbekannte Einträge werden beim Speichern unverändert übernommen.</summary>
        readonly List<string> _unknownLines = new List<string>();

        public bool Exists { get; private set; }

        public static Config Load(string file)
        {
            Config config = new Config();
            if (!File.Exists(file))
            {
                return config;
            }

            config.Exists = true;
            bool windowsSafeNamesSet = false;
            foreach (string rawLine in File.ReadAllLines(file))
            {
                string[] parts = rawLine.Split('|', 2);
                if (parts.Length != 2)
                {
                    continue;
                }
                string value = parts[1].Trim();
                switch (parts[0].Trim())
                {
                    case "Cookie": config.Cookie = value; break;
                    case "Pfad": config.Pfad = value; break;
                    case "StudIP": config.StudIP = value; break;
                    case "express": config.Express = ParseBool(value); break;
                    case "ignore": config.Ignore = value; break;
                    case "ignoreCourses": config.IgnoreCourses = value; break;
                    case "onlyCourses": config.OnlyCourses = value; break;
                    case "downloadOverwrite": config.DownloadOverwrite = ParseBool(value); break;
                    case "User": config.User = value; break;
                    // Passwort nicht trimmen, Leerzeichen könnten Teil des Passworts sein
                    case "Pass": config.Pass = parts[1]; break;
                    case "savePass": config.SavePass = ParseBool(value); break;
                    case "parallel": config.Parallel = int.TryParse(value, out int p) ? Math.Clamp(p, 1, 16) : 3; break;
                    case "verbose": config.Verbose = ParseBool(value); break;
                    case "cleanup": config.Cleanup = ParseBool(value); break;
                    case "windowsSafeNames": config.WindowsSafeNames = ParseBool(value); windowsSafeNamesSet = true; break;
                    case "auth": config.Auth = Enum.TryParse(value, true, out AuthMode mode) ? mode : AuthMode.Auto; break;
                    case "oauthClientId": config.OAuthClientId = value; break;
                    case "oauthClientSecret": config.OAuthClientSecret = value; break;
                    case "oauthScope": config.OAuthScope = value; break;
                    case "oauthAuthorizeUrl": config.OAuthAuthorizeUrl = value; break;
                    case "oauthTokenUrl": config.OAuthTokenUrl = value; break;
                    case "oauthRedirectUri": config.OAuthRedirectUri = value; break;
                    case "oauthRefreshToken": config.OAuthRefreshToken = value; break;
                    default: config._unknownLines.Add(rawLine); break;
                }
            }

            if (!windowsSafeNamesSet)
            {
                // Bestehende Konfiguration aus Version 1.x: Verhalten beibehalten
                config.WindowsSafeNames = false;
            }
            return config;
        }

        public void Save(string file)
        {
            using StreamWriter sw = new StreamWriter(file, false);
            sw.WriteLine("Cookie|" + Cookie);
            sw.WriteLine("Pfad|" + Pfad);
            sw.WriteLine("StudIP|" + StudIP);
            sw.WriteLine("express|" + Express);
            sw.WriteLine("ignore|" + Ignore);
            sw.WriteLine("ignoreCourses|" + IgnoreCourses);
            sw.WriteLine("onlyCourses|" + OnlyCourses);
            sw.WriteLine("downloadOverwrite|" + DownloadOverwrite);
            sw.WriteLine("User|" + User);
            if (SavePass)
            {
                sw.WriteLine("Pass|" + Pass);
            }
            sw.WriteLine("savePass|" + SavePass);
            sw.WriteLine("parallel|" + Parallel);
            sw.WriteLine("verbose|" + Verbose);
            sw.WriteLine("cleanup|" + Cleanup);
            sw.WriteLine("windowsSafeNames|" + WindowsSafeNames);
            sw.WriteLine("auth|" + Auth.ToString().ToLowerInvariant());
            sw.WriteLine("oauthClientId|" + OAuthClientId);
            sw.WriteLine("oauthClientSecret|" + OAuthClientSecret);
            sw.WriteLine("oauthScope|" + OAuthScope);
            sw.WriteLine("oauthAuthorizeUrl|" + OAuthAuthorizeUrl);
            sw.WriteLine("oauthTokenUrl|" + OAuthTokenUrl);
            sw.WriteLine("oauthRedirectUri|" + OAuthRedirectUri);
            sw.WriteLine("oauthRefreshToken|" + OAuthRefreshToken);
            foreach (string line in _unknownLines)
            {
                sw.WriteLine(line);
            }
        }

        public bool HasCredentials()
        {
            return !string.IsNullOrEmpty(User) && User != "Beispieluser" && !string.IsNullOrEmpty(Pass);
        }

        public bool HasOAuthClient()
        {
            return !string.IsNullOrWhiteSpace(OAuthClientId);
        }

        /// <summary>
        /// Ermittelt die zu verwendende config.ini:
        /// 1. --config Parameter, 2. config.ini im aktuellen Ordner (bisheriges Verhalten),
        /// 3. config.ini neben der Anwendung (funktioniert auch bei Aufgabenplanung/cron).
        /// </summary>
        public static string ResolvePath(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }
            string inWorkingDir = Path.GetFullPath(FileName);
            if (File.Exists(inWorkingDir))
            {
                return inWorkingDir;
            }
            string nextToApp = Path.Combine(AppContext.BaseDirectory, FileName);
            if (File.Exists(nextToApp) || IsWritableDirectory(AppContext.BaseDirectory))
            {
                return nextToApp;
            }
            return inWorkingDir;
        }

        static bool IsWritableDirectory(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, ".studip-downloader-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool ParseBool(string value)
        {
            return bool.TryParse(value, out bool result) && result;
        }
    }
}
