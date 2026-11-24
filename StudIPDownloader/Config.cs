using System;
using System.IO;

namespace StudIPDownloader
{
    /// <summary>
    /// Liest und schreibt die config.ini (Format: Schlüssel|Wert pro Zeile).
    /// </summary>
    class Config
    {
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
        /// <summary>Kommagetrennte Liste von Kursen (Titel-Teilstring, Veranstaltungsnummer oder Kurs-ID). Wenn gesetzt, werden nur diese Kurse synchronisiert.</summary>
        public string OnlyCourses = "";
        public bool DownloadOverwrite = false;
        public string User = "";
        public string Pass = "";
        public bool SavePass = false;

        public static Config Load(string file)
        {
            Config config = new Config();
            if (!File.Exists(file))
            {
                return config;
            }

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
                }
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
        }

        public bool HasCredentials()
        {
            return !string.IsNullOrEmpty(User) && User != "Beispieluser" && !string.IsNullOrEmpty(Pass);
        }

        static bool ParseBool(string value)
        {
            return bool.TryParse(value, out bool result) && result;
        }
    }
}
