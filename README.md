# StudIP-Downloader
Ein kleines Tool, um die Dateien aller Kurse aus dem StudIP herunterzuladen. Aktuell nur für die Uni Oldenburg getestet. Andere Universitäten und Hochschulen, die StudIP verwenden, sollten aber auch funktionieren.

Seit Version 2.0 wird die Stud.IP **JSON:API** (`jsonapi.php/v1/`) verwendet. Die alte REST-API (`api.php`) wurde mit Stud.IP 6.0 entfernt.
Die JSON:API ist ab Stud.IP 4.x verfügbar.

![.NET Core](https://github.com/yannik995/StudIP-Downloader/workflows/.NET%20Core/badge.svg)

## Download
https://github.com/yannik995/StudIP-Downloader/releases

Die Releases sind eigenständig lauffähig (self-contained, .NET 8) für Windows, Linux (x64/ARM64) und macOS (Intel/Apple Silicon). Es muss keine .NET Runtime installiert werden.
Alternativ gibt es ein [Docker-Image](#docker).

## Wie wird das Tool verwendet?

1. Zip Archiv herunterladen, entpacken.
2. StudIPDownloader.exe starten, StudIP URL eintragen z.B.: https://elearning.uni-oldenburg.de/
3. Anmelden, z.B. mit dem Wert des Session Cookies aus dem Browser (siehe [Anmeldung](#anmeldung)).
4. Lokalen Pfad eintragen
5. Nun werden alle Dateien heruntergeladen

Die Einstellungen werden in der `config.ini` gespeichert und beim nächsten Start muss nur ggf. der Wert des Cookies neu eingetragen werden.
Die `config.ini` wird im aktuellen Ordner gesucht, sonst neben der Anwendung. Mit `--config <Datei>` kann sie explizit angegeben werden.

Es werden immer nur die neuen oder geänderten Dateien heruntergeladen. Das Änderungsdatum der heruntergeladenen Dateien wird auf das Änderungsdatum im Stud.IP gesetzt.
Mehrere Dateien werden parallel geladen, unterbrochene Downloads werden fortgesetzt und bei Netzwerkfehlern automatisch wiederholt.
Gleichnamige Dateien/Ordner/Veranstaltungen bekommen lokal einen Zusatz wie `Blatt (2).pdf`.

## Anmeldung

Über `auth` in der `config.ini` wird festgelegt, wie angemeldet wird. Standard ist `auto`: OAuth2 (wenn eingerichtet) → Benutzername/Passwort (wenn eingetragen) → Session Cookie.

### Session Cookie (`auth|cookie`)
Im Browser bei Stud.IP einloggen und den Wert des Cookies kopieren (Entwicklertools mit F12 → Speicher/Application → Cookies).
Der Cookie heißt seit Stud.IP 6.0 `Studip_Session`, in älteren Versionen `Seminar_Session`.

![Cookie Bild](./cookie.png)

Der Cookie kann auch als Parameter übergeben werden: `StudIPDownloader.exe CookieWert`

### Benutzername und Passwort (`auth|password`)
`StudIPDownloader.exe Username Passwort True` (das optionale `True` speichert das Passwort in der config.ini), oder `User`/`Pass` in der config.ini.

Es wird zuerst HTTP Basic Auth versucht. Ist das auf dem Server deaktiviert, wird über das normale Stud.IP Login-Formular eingeloggt.
Bei Single Sign-On (z.B. Shibboleth) funktioniert das nicht, dann bitte Cookie oder OAuth2 verwenden.

### OAuth2 (`auth|oauth2`, optional)
Stud.IP ab 5.x/6.0 unterstützt OAuth2. Der Vorteil: Nach einmaliger Anmeldung im Browser wird ein Refresh-Token gespeichert, Cookies müssen nicht mehr kopiert werden.
Voraussetzung ist ein OAuth2-Client, den der **Stud.IP Admin** anlegen muss (Admin → System → OAuth2, Typ „öffentlich“/public mit PKCE, Redirect-URI `http://localhost:8642/callback`).

```
auth|oauth2
oauthClientId|<Client-ID vom Admin>
```

Beim ersten Start (oder mit `--oauth-login`) öffnet sich der Browser. Nach der Anmeldung leitet Stud.IP auf `http://localhost:8642/callback` zurück und die Anwendung übernimmt den Login automatisch.
Läuft die Anwendung auf einem anderen Rechner (NAS, Docker), die Anmelde-URL auf einem beliebigen Rechner öffnen und die URL, auf die der Browser anschließend weitergeleitet wird (Seite lädt nicht, das ist ok), in die Konsole kopieren.

Weitere optionale Einstellungen, falls die Installation abweicht: `oauthClientSecret` (bei vertraulichen Clients), `oauthScope` (Standard `api`), `oauthAuthorizeUrl` (Standard `<StudIP>/dispatch.php/api/oauth2/authorize`), `oauthTokenUrl` (Standard `<StudIP>/dispatch.php/api/oauth2/token`), `oauthRedirectUri`.

## Einstellungen (config.ini)

| Schlüssel | Beschreibung |
|---|---|
| `StudIP` | URL der Stud.IP Installation, z.B. `https://elearning.uni-oldenburg.de/` oder `https://e-learning.tuhh.de/studip/` |
| `auth` | `auto` (Standard), `cookie`, `password` oder `oauth2` |
| `Cookie` | Wert des `Studip_Session` (bzw. `Seminar_Session`) Cookies |
| `User` / `Pass` | Benutzername und Passwort (optional) |
| `savePass` | `True`, um das Passwort in der config.ini zu speichern |
| `oauth...` | OAuth2 Einstellungen, siehe oben. `oauthRefreshToken` wird automatisch gespeichert. |
| `Pfad` | Lokaler Zielordner |
| `express` | `True`: Nur Veranstaltungen des aktuellen Semesters synchronisieren |
| `ignore` | Kommagetrennte Liste von Ordnernamen, die übersprungen werden, z.B. `720p,Videos` |
| `ignoreCourses` | Kommagetrennte Liste von Veranstaltungen, die übersprungen werden (Blacklist) |
| `onlyCourses` | Kommagetrennte Liste von Veranstaltungen. Wenn gesetzt, werden nur diese synchronisiert (Whitelist) |
| `downloadOverwrite` | `True`: Auch Dateien herunterladen, die nicht als downloadbar markiert sind |
| `parallel` | Anzahl gleichzeitiger Downloads (Standard `3`, maximal `16`) |
| `cleanup` | `True`: Dateien, die im Stud.IP gelöscht wurden, lokal nach `_geloescht/` verschieben |
| `windowsSafeNames` | `True`: Dateinamen auf allen Systemen Windows-kompatibel bereinigen (für Netzlaufwerke, NAS, exFAT; kürzt sehr lange Namen). Standard für neue Konfigurationen, bestehende Konfigurationen behalten ihre bisherigen Namen. |
| `verbose` | `True`: Ausführliche Ausgabe |

Bei `ignoreCourses` und `onlyCourses` kann jeweils ein Teil des Veranstaltungstitels, die Veranstaltungsnummer oder die Veranstaltungs-ID angegeben werden (Groß-/Kleinschreibung egal).

**Aufräumen (`cleanup`)**: Es wird nur innerhalb von Veranstaltungen aufgeräumt, die vollständig geladen werden konnten. Inhalte ignorierter Ordner und eigene Dateien in ignorierten Ordnern bleiben erhalten.
Verschobene Dateien liegen unter `_geloescht/` mit derselben Ordnerstruktur und können von dort wiederhergestellt oder gelöscht werden.

Siehe auch [config.ini.sample](./config.ini.sample).

## Kommandozeile

```
StudIPDownloader [Optionen] [<Cookie> | <Benutzer> <Passwort> [True]]

  -c, --config <Datei>   config.ini verwenden
  -p, --path <Ordner>    Lokaler Zielordner
      --url <URL>        StudIP URL
      --parallel <n>     Anzahl gleichzeitiger Downloads
      --express / --all  Nur aktuelles Semester / alle Semester
      --cleanup / --no-cleanup
      --oauth-login      OAuth2 Login (neu) durchführen
      --non-interactive  Keine Eingaben abfragen (Aufgabenplanung, cron, Docker)
  -v, --verbose          Ausführliche Ausgabe
  -h, --help             Hilfe
```

Exit-Codes: `0` = OK, `1` = Fehler/Login fehlgeschlagen, `2` = einzelne Dateien oder Ordner konnten nicht geladen werden.

Beispiel für cron (täglich um 6:15 Uhr):
```
15 6 * * * /opt/studip/StudIPDownloader --config /opt/studip/config.ini --non-interactive >> /var/log/studip.log 2>&1
```

## Docker

Das Image synchronisiert im Abstand von `SYNC_INTERVAL` (Standard `6h`, `0` = einmalig).
Die `config.ini` liegt in `/config`, die Dateien landen in `/data`.

```yaml
services:
  studip-downloader:
    image: ghcr.io/yannik995/studip-downloader:latest
    restart: unless-stopped
    user: "1000:1000"   # Besitzer der gemounteten Ordner, Unraid: "99:100"
    environment:
      SYNC_INTERVAL: 6h
      TZ: Europe/Berlin
    volumes:
      - ./config:/config
      - ./studium:/data
```

Vorher eine `config/config.ini` anlegen (mindestens `StudIP|...` und eine Anmeldung, siehe oben).
Mit OAuth2 einmalig interaktiv anmelden:

```
docker compose run --rm -it studip-downloader --oauth-login
```

## Entwicklung

```
dotnet build
dotnet test
```

Die Tests laufen gegen einen nachgebauten Stud.IP Server (`tests/StudIPDownloader.Tests/FakeStudIP.cs`).
Releases entstehen automatisch beim Pushen eines Tags `v*` (z.B. `v2.0.0`), inkl. Docker-Image auf GHCR.
