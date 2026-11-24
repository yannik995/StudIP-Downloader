using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StudIPDownloader
{
    class StudIPException : Exception
    {
        public HttpStatusCode? StatusCode { get; }

        public StudIPException(string message, HttpStatusCode? statusCode = null, Exception inner = null) : base(message, inner)
        {
            StatusCode = statusCode;
        }
    }

    class UnauthorizedException : StudIPException
    {
        public UnauthorizedException(string message) : base(message, HttpStatusCode.Unauthorized) { }
    }

    /// <summary>
    /// Client für die Stud.IP JSON:API (jsonapi.php/v1/).
    /// Die alte REST-API (api.php) wurde mit Stud.IP 6.0 entfernt.
    /// </summary>
    class StudIPClient : IDisposable
    {
        /// <summary>
        /// Name des Session Cookies: Seit Stud.IP 6.0 "Studip_Session", davor "Seminar_Session".
        /// Es werden immer beide gesetzt, der Server wertet nur den passenden aus.
        /// </summary>
        public static readonly string[] SessionCookieNames = { "Studip_Session", "Seminar_Session" };

        const string JsonApiMediaType = "application/vnd.api+json";
        const string UserAgent = "StudIP-Downloader";

        /// <summary>Seitengröße für Collections.</summary>
        public int PageLimit { get; set; } = 100;

        /// <summary>Anzahl Versuche bei Netzwerkfehlern, 429 und 5xx.</summary>
        public int MaxAttempts { get; set; } = 4;

        /// <summary>Wartezeit vor Versuch n (1-basiert, ab dem zweiten Versuch).</summary>
        public Func<int, TimeSpan> RetryDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Pow(3, attempt - 2));

        public Uri BaseUri => _base;
        public Uri ApiBase => _apiBase;
        public string UserId => _userId;
        public bool IsOAuth => _oauth != null;

        readonly Uri _base;
        readonly Uri _apiBase;

        HttpClient _http;
        CookieContainer _cookies;
        OAuth2Session _oauth;
        string _userId;

        public StudIPClient(string baseUrl)
        {
            _base = NormalizeBaseUrl(baseUrl);
            _apiBase = new Uri(_base, "jsonapi.php/v1/");
            ResetHttpClient();
        }

        public static Uri NormalizeBaseUrl(string baseUrl)
        {
            baseUrl = (baseUrl ?? "").Trim();
            if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = "https://" + baseUrl;
            }
            // Häufig wird die URL einer Unterseite eingetragen, z.B. .../index.php oder .../dispatch.php/start
            foreach (string suffix in new[] { "/dispatch.php", "/index.php", "/api.php", "/jsonapi.php" })
            {
                int idx = baseUrl.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    baseUrl = baseUrl.Substring(0, idx);
                }
            }
            int queryStart = baseUrl.IndexOfAny(new[] { '?', '#' });
            if (queryStart > 0)
            {
                baseUrl = baseUrl.Substring(0, queryStart);
            }
            if (!baseUrl.EndsWith("/"))
            {
                baseUrl += "/";
            }
            return new Uri(baseUrl);
        }

        void ResetHttpClient()
        {
            _http?.Dispose();
            _cookies = new CookieContainer();
            HttpClientHandler handler = new HttpClientHandler
            {
                CookieContainer = _cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _oauth = null;
            _userId = null;
        }

        // ---------------------------------------------------------------------
        // Authentifizierung
        // ---------------------------------------------------------------------

        public void UseSessionCookie(string value)
        {
            ResetHttpClient();
            value = (value ?? "").Trim();
            foreach (string name in SessionCookieNames)
            {
                _cookies.Add(_base, new Cookie(name, value, "/"));
            }
        }

        public void UseBasicAuth(string user, string password)
        {
            ResetHttpClient();
            string auth = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        }

        public void UseOAuth2(OAuth2Session session)
        {
            ResetHttpClient();
            _oauth = session;
        }

        /// <summary>
        /// Prüft, ob die aktuelle Authentifizierung gültig ist, und merkt sich die User-ID.
        /// </summary>
        public async Task<bool> IsAuthenticated()
        {
            try
            {
                JsonNode me = await GetApi("users/me");
                _userId = (string)me?["data"]?["id"];
                return !string.IsNullOrEmpty(_userId);
            }
            catch (UnauthorizedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Login über das normale Stud.IP Login-Formular. Wird verwendet, wenn HTTP Basic Auth
        /// auf dem Server nicht erlaubt ist. Funktioniert nur mit der Standard-Anmeldung von Stud.IP
        /// (nicht mit Single Sign-On wie Shibboleth).
        /// </summary>
        public async Task<bool> TryFormLogin(string user, string password)
        {
            ResetHttpClient();
            try
            {
                Uri loginPage = new Uri(_base, "index.php?again=yes");
                using HttpResponseMessage pageResponse = await Send(() => new HttpRequestMessage(HttpMethod.Get, loginPage));
                if (!pageResponse.IsSuccessStatusCode)
                {
                    return false;
                }
                string html = await pageResponse.Content.ReadAsStringAsync();
                Uri pageUri = pageResponse.RequestMessage?.RequestUri ?? loginPage;

                Match form = Regex.Matches(html, @"<form\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                    .FirstOrDefault(m => Regex.IsMatch(m.Value, @"name\s*=\s*[""']loginname[""']", RegexOptions.IgnoreCase));
                if (form == null)
                {
                    return false;
                }

                Match formTag = Regex.Match(form.Value, @"<form\b[^>]*>", RegexOptions.IgnoreCase);
                string action = GetAttribute(formTag.Value, "action");
                Uri actionUri = string.IsNullOrEmpty(action) ? pageUri : new Uri(pageUri, WebUtility.HtmlDecode(action));

                Dictionary<string, string> fields = new Dictionary<string, string>();
                foreach (Match input in Regex.Matches(form.Value, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    string name = GetAttribute(input.Value, "name");
                    string type = (GetAttribute(input.Value, "type") ?? "text").ToLowerInvariant();
                    if (string.IsNullOrEmpty(name) || type == "submit" || type == "button" || type == "checkbox")
                    {
                        continue;
                    }
                    fields[WebUtility.HtmlDecode(name)] = WebUtility.HtmlDecode(GetAttribute(input.Value, "value") ?? "");
                }
                fields["loginname"] = user;
                fields["password"] = password;
                fields.TryAdd("resolution", "1920x1080");
                fields.TryAdd("device_pixel_ratio", "1");

                using HttpResponseMessage loginResponse = await _http.PostAsync(actionUri, new FormUrlEncodedContent(fields));
            }
            catch (HttpRequestException)
            {
                return false;
            }
            return await IsAuthenticated();
        }

        static string GetAttribute(string tag, string attribute)
        {
            Match m = Regex.Match(tag, @"\b" + attribute + @"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                return null;
            }
            return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }

        // ---------------------------------------------------------------------
        // HTTP mit Wiederholungen
        // ---------------------------------------------------------------------

        static bool IsTransient(HttpStatusCode status)
        {
            int code = (int)status;
            return code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        /// <summary>
        /// Sendet eine Anfrage. Bei Netzwerkfehlern, 429 und 5xx wird mit Pause erneut versucht,
        /// bei 401 mit OAuth2 einmal der Token erneuert.
        /// </summary>
        async Task<HttpResponseMessage> Send(Func<HttpRequestMessage> createRequest,
            HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
        {
            bool refreshed = false;
            for (int attempt = 1; ; attempt++)
            {
                using HttpRequestMessage request = createRequest();
                string token = null;
                if (_oauth != null)
                {
                    token = await _oauth.GetAccessToken();
                    if (token != null)
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                }

                HttpResponseMessage response;
                try
                {
                    using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                    response = await _http.SendAsync(request, completion, timeout.Token);
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is OperationCanceledException) && attempt < MaxAttempts)
                {
                    TimeSpan delay = RetryDelay(attempt + 1);
                    Log.Debug(string.Format("Netzwerkfehler ({0}), neuer Versuch in {1:0} s...", ex.Message, delay.TotalSeconds));
                    await Task.Delay(delay);
                    continue;
                }
                catch (OperationCanceledException ex)
                {
                    throw new HttpRequestException("Zeitüberschreitung bei " + request.RequestUri, ex);
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && _oauth != null && !refreshed)
                {
                    refreshed = true;
                    if (await _oauth.Refresh(token))
                    {
                        response.Dispose();
                        attempt--;
                        continue;
                    }
                    return response;
                }

                if (IsTransient(response.StatusCode) && attempt < MaxAttempts)
                {
                    TimeSpan delay = RetryDelay(attempt + 1);
                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                    if (retryAfter.HasValue && retryAfter.Value > delay)
                    {
                        delay = retryAfter.Value > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : retryAfter.Value;
                    }
                    Log.Debug(string.Format("{0} {1}, neuer Versuch in {2:0} s...", (int)response.StatusCode, response.ReasonPhrase, delay.TotalSeconds));
                    response.Dispose();
                    await Task.Delay(delay);
                    continue;
                }
                return response;
            }
        }

        // ---------------------------------------------------------------------
        // JSON:API
        // ---------------------------------------------------------------------

        HttpRequestMessage ApiRequest(string path)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, path));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonApiMediaType));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }

        public async Task<JsonNode> GetApi(string path)
        {
            using HttpResponseMessage response = await Send(() => ApiRequest(path));
            string body = await response.Content.ReadAsStringAsync();

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedException("Nicht eingeloggt (401 Unauthorized): Cookie/Token abgelaufen oder Zugangsdaten falsch.");
            }
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound && path == "users/me")
                {
                    throw new StudIPException("Die JSON:API wurde unter " + _apiBase + " nicht gefunden. Ist die StudIP URL korrekt?", response.StatusCode);
                }
                throw new StudIPException(string.Format("{0} ({1} {2}){3}", path, (int)response.StatusCode, response.ReasonPhrase,
                    ExtractErrorDetail(body)), response.StatusCode);
            }

            string mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Contains("html") || body.TrimStart().StartsWith("<"))
            {
                // Typischerweise eine Weiterleitung auf die Login-Seite
                throw new UnauthorizedException("Server hat HTML statt JSON geliefert (vermutlich Weiterleitung auf die Login-Seite).");
            }

            try
            {
                return JsonNode.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new StudIPException("Ungültige Antwort der JSON:API für " + path + ": " + ex.Message, response.StatusCode, ex);
            }
        }

        static string ExtractErrorDetail(string body)
        {
            try
            {
                JsonArray errors = JsonNode.Parse(body)?["errors"] as JsonArray;
                string detail = string.Join("; ", errors?.Select(e => (string)e?["detail"] ?? (string)e?["title"]).Where(s => !string.IsNullOrEmpty(s)) ?? Enumerable.Empty<string>());
                return string.IsNullOrEmpty(detail) ? "" : ": " + detail;
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Lädt alle Einträge einer (paginierten) JSON:API Collection.
        /// </summary>
        public async Task<List<JsonObject>> GetCollection(string path)
        {
            List<JsonObject> result = new List<JsonObject>();
            HashSet<string> seen = new HashSet<string>();
            string separator = path.Contains('?') ? "&" : "?";
            int offset = 0;

            while (true)
            {
                JsonNode page = await GetApi(path + separator + "page[offset]=" + offset + "&page[limit]=" + PageLimit);
                JsonArray data = page?["data"] as JsonArray;
                if (data == null || data.Count == 0)
                {
                    break;
                }

                int added = 0;
                foreach (JsonObject item in data.OfType<JsonObject>())
                {
                    string key = (string)item["type"] + "/" + (string)item["id"];
                    if (seen.Add(key))
                    {
                        result.Add(item);
                        added++;
                    }
                }
                offset += data.Count;

                long? total = Json.Long(page?["meta"]?["page"]?["total"]);
                if (added == 0 || (total.HasValue && offset >= total.Value) || (!total.HasValue && page?["links"]?["next"] == null && data.Count < PageLimit))
                {
                    break;
                }
            }
            return result;
        }

        // ---------------------------------------------------------------------
        // Downloads
        // ---------------------------------------------------------------------

        /// <summary>
        /// Lädt den Inhalt einer Datei (file-ref) in <paramref name="tempFile"/>.
        /// Abgebrochene Downloads werden per HTTP Range fortgesetzt, sofern der Server das unterstützt.
        /// Schlägt die JSON:API fehl, wird sendfile.php der Weboberfläche versucht.
        /// </summary>
        public async Task<bool> DownloadFileRef(string fileRefId, string tempFile, Action<long, long> progress = null)
        {
            Uri api = new Uri(_apiBase, "file-refs/" + fileRefId + "/content");
            if (await DownloadTo(api, tempFile, false, progress))
            {
                return true;
            }
            // Fallback über die Weboberfläche (funktioniert nur mit Cookie/Formular-Login)
            Uri web = new Uri(_base, "sendfile.php?force_download=1&type=0&file_id=" + Uri.EscapeDataString(fileRefId));
            return await DownloadTo(web, tempFile, true, progress);
        }

        async Task<bool> DownloadTo(Uri uri, string file, bool rejectHtml, Action<long, long> progress)
        {
            for (int attempt = 1; ; attempt++)
            {
                long existing = File.Exists(file) ? new FileInfo(file).Length : 0;
                try
                {
                    using HttpResponseMessage response = await Send(() =>
                    {
                        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
                        if (existing > 0)
                        {
                            request.Headers.Range = new RangeHeaderValue(existing, null);
                        }
                        return request;
                    }, HttpCompletionOption.ResponseHeadersRead);

                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
                    {
                        File.Delete(file);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        return false;
                    }
                    if (rejectHtml && (response.Content.Headers.ContentType?.MediaType ?? "").Contains("html"))
                    {
                        return false; // vermutlich Login- oder Fehlerseite
                    }

                    bool append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
                    long total = (response.Content.Headers.ContentLength ?? -1) + (append ? existing : 0);
                    if (response.Content.Headers.ContentLength == null)
                    {
                        total = -1;
                    }

                    using Stream source = await response.Content.ReadAsStreamAsync();
                    using FileStream target = new FileStream(file, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[81920];
                    long written = append ? existing : 0;
                    int read;
                    using CancellationTokenSource stall = new CancellationTokenSource();
                    while (true)
                    {
                        // Abbruch, wenn 2 Minuten lang keine Daten kommen
                        stall.CancelAfter(TimeSpan.FromMinutes(2));
                        read = await source.ReadAsync(buffer, 0, buffer.Length, stall.Token);
                        if (read == 0)
                        {
                            break;
                        }
                        await target.WriteAsync(buffer, 0, read);
                        written += read;
                        progress?.Invoke(written, total);
                    }
                    if (total >= 0 && written < total)
                    {
                        throw new IOException("Verbindung vorzeitig beendet (" + written + " von " + total + " Bytes)");
                    }
                    return true;
                }
                catch (Exception ex) when ((ex is IOException || ex is HttpRequestException || ex is OperationCanceledException) && attempt < MaxAttempts)
                {
                    TimeSpan delay = RetryDelay(attempt + 1);
                    Log.Debug(string.Format("Download unterbrochen ({0}), wird in {1:0} s fortgesetzt...", ex.Message, delay.TotalSeconds));
                    await Task.Delay(delay);
                }
            }
        }

        public void Dispose()
        {
            _http?.Dispose();
        }
    }

    /// <summary>
    /// Hilfsfunktionen zum Lesen von JSON:API Ressourcen.
    /// </summary>
    static class Json
    {
        public static string Attr(JsonObject resource, string name)
        {
            JsonNode node = resource?["attributes"]?[name];
            return node is JsonValue value && value.TryGetValue(out string s) ? s : node?.ToString();
        }

        public static string RelationshipId(JsonObject resource, string relationship)
        {
            JsonNode data = resource?["relationships"]?[relationship]?["data"];
            return data is JsonObject obj ? (string)obj["id"] : null;
        }

        public static bool HasRelationship(JsonObject resource, string relationship)
        {
            return resource?["relationships"]?[relationship] != null;
        }

        public static long? Long(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out long l)) return l;
                if (value.TryGetValue(out int i)) return i;
                if (value.TryGetValue(out double d)) return (long)d;
                if (value.TryGetValue(out string s) && long.TryParse(s, out long parsed)) return parsed;
            }
            return null;
        }

        public static bool? Bool(JsonNode node)
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue(out bool b)) return b;
                if (value.TryGetValue(out string s) && bool.TryParse(s, out bool parsed)) return parsed;
            }
            return null;
        }

        /// <summary>
        /// Datumswerte liefert die JSON:API im ISO 8601 Format, zur Sicherheit werden auch Unix-Timestamps akzeptiert.
        /// </summary>
        public static DateTimeOffset? Date(JsonNode node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }
            if (value.TryGetValue(out string s))
            {
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
                {
                    return date;
                }
                if (long.TryParse(s, out long unixFromString))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixFromString);
                }
                return null;
            }
            long? unix = Long(node);
            return unix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(unix.Value) : null;
        }
    }
}
