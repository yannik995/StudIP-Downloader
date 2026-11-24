using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StudIPDownloader
{
    /// <summary>
    /// Bereinigt Datei- und Ordnernamen aus dem Stud.IP.
    /// </summary>
    class FileNames
    {
        /// <summary>Ungültige Zeichen unter Windows (gilt auch für SMB-Freigaben, viele NAS und exFAT).</summary>
        static readonly char[] WindowsInvalidChars = "<>:\"/\\|?*".ToCharArray()
            .Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToArray();

        static readonly HashSet<string> WindowsReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>Maximale Länge eines Datei-/Ordnernamens (Zeichen). Hält Pfade unter Windows kurz.</summary>
        public const int MaxNameLength = 120;

        readonly bool _windowsSafe;
        readonly char[] _invalidChars;

        public FileNames(bool windowsSafe)
        {
            // Unter Windows sind die Windows-Zeichen ohnehin ungültig. Die zusätzlichen Regeln (Punkte am Ende,
            // reservierte Namen, Längenbegrenzung) nur mit windowsSafeNames, damit sich bestehende Namen nicht ändern.
            _windowsSafe = windowsSafe;
            _invalidChars = _windowsSafe ? WindowsInvalidChars.Union(Path.GetInvalidFileNameChars()).ToArray() : Path.GetInvalidFileNameChars();
        }

        /// <summary>Entfernt ungültige Zeichen (bisheriges Verhalten für Kurs-, Ordner- und Dateinamen).</summary>
        public string Clean(string name)
        {
            return Finish(string.Concat((name ?? "").Trim().Split(_invalidChars)));
        }

        /// <summary>Ersetzt ungültige Zeichen durch "_" (bisheriges Verhalten für Semesternamen, z.B. "WiSe 23/24").</summary>
        public string Replace(string name, string replacement = "_")
        {
            return Finish(string.Join(replacement, (name ?? "").Trim().Split(_invalidChars)));
        }

        string Finish(string name)
        {
            if (_windowsSafe)
            {
                // Windows erlaubt keine Punkte/Leerzeichen am Ende
                name = name.TrimEnd('.', ' ');
                string stem = name.Split('.')[0];
                if (WindowsReservedNames.Contains(stem))
                {
                    name = "_" + name;
                }
            }
            if (name.Length == 0 || name == "." || name == "..")
            {
                name = "_";
            }
            return _windowsSafe ? Truncate(name, MaxNameLength) : name;
        }

        public static string Truncate(string name, int maxLength)
        {
            if (name.Length <= maxLength)
            {
                return name;
            }
            string ext = Path.GetExtension(name);
            if (ext.Length > 15)
            {
                ext = "";
            }
            string stem = name.Substring(0, maxLength - ext.Length).TrimEnd('.', ' ');
            return stem + ext;
        }

        /// <summary>Fügt " (n)" vor der Dateiendung ein.</summary>
        public static string WithCounter(string name, int counter, bool isFile)
        {
            string ext = isFile ? Path.GetExtension(name) : "";
            string stem = name.Substring(0, name.Length - ext.Length);
            return stem + " (" + counter + ")" + ext;
        }
    }

    /// <summary>
    /// Vergibt pro Ordner eindeutige Namen. Stud.IP erlaubt gleichnamige Dateien/Ordner,
    /// lokal würden sie sich gegenseitig überschreiben. Vergleich ohne Groß-/Kleinschreibung,
    /// da Windows und macOS das nicht unterscheiden.
    /// </summary>
    class NameAllocator
    {
        readonly Dictionary<string, HashSet<string>> _used = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        public string Allocate(string directory, string name, bool isFile)
        {
            string key = Path.GetFullPath(directory);
            if (!_used.TryGetValue(key, out HashSet<string> used))
            {
                used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _used[key] = used;
            }

            string candidate = name;
            for (int counter = 2; !used.Add(candidate); counter++)
            {
                candidate = FileNames.WithCounter(name, counter, isFile);
            }
            return candidate;
        }
    }
}
