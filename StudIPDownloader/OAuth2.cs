using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StudIPDownloader
{
    /// <summary>
    /// OAuth2 Authorization Code Flow mit PKCE für Stud.IP (ab 5.x/6.0).
    /// Der OAuth2-Client muss vom Stud.IP Admin angelegt werden (Admin → System → OAuth2).
    /// Endpunkte, Scope und Redirect-URI sind konfigurierbar, die Standardwerte entsprechen Stud.IP 6.
    /// </summary>
    class OAuth2Session : IDisposable
    {
        public const string DefaultRedirectUri = "http://localhost:8642/callback";
        public const string DefaultScope = "api";

        public string ClientId { get; }
        public string ClientSecret { get; }
        public string Scope { get; }
        public Uri AuthorizeUrl { get; }
        public Uri TokenUrl { get; }
        public Uri RedirectUri { get; }

        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
        public DateTimeOffset AccessTokenExpires { get; private set; } = DateTimeOffset.MinValue;

        /// <summary>Wird aufgerufen, wenn sich der Refresh-Token ändert (zum Speichern in der config.ini).</summary>
        public Action<string> RefreshTokenChanged { get; set; }

        /// <summary>Zum Testen: Öffnen des Browsers ersetzen.</summary>
        public Action<Uri> OpenBrowser { get; set; } = TryOpenBrowser;

        /// <summary>Wie lange auf die Rückleitung aus dem Browser gewartet wird.</summary>
        public TimeSpan LoginTimeout { get; set; } = TimeSpan.FromMinutes(5);

        readonly HttpClient _http;
        readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

        public OAuth2Session(Uri studipBase, string clientId, string clientSecret = null, string scope = null,
            string authorizeUrl = null, string tokenUrl = null, string redirectUri = null, string refreshToken = null)
        {
            ClientId = clientId;
            ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret;
            Scope = string.IsNullOrWhiteSpace(scope) ? DefaultScope : scope;
            AuthorizeUrl = string.IsNullOrWhiteSpace(authorizeUrl) ? new Uri(studipBase, "dispatch.php/api/oauth2/authorize") : new Uri(authorizeUrl);
            TokenUrl = string.IsNullOrWhiteSpace(tokenUrl) ? new Uri(studipBase, "dispatch.php/api/oauth2/token") : new Uri(tokenUrl);
            RedirectUri = new Uri(string.IsNullOrWhiteSpace(redirectUri) ? DefaultRedirectUri : redirectUri);
            RefreshToken = string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken;

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("StudIP-Downloader");
        }

        public bool HasRefreshToken => !string.IsNullOrEmpty(RefreshToken);

        /// <summary>
        /// Liefert einen gültigen Access-Token (erneuert ihn bei Bedarf).
        /// </summary>
        public async Task<string> GetAccessToken()
        {
            if (!string.IsNullOrEmpty(AccessToken) && AccessTokenExpires > DateTimeOffset.UtcNow.AddSeconds(60))
            {
                return AccessToken;
            }
            await Refresh(AccessToken);
            return AccessToken;
        }

        /// <summary>
        /// Erneuert den Access-Token mit dem Refresh-Token. <paramref name="failedToken"/> ist der Token,
        /// mit dem die Anfrage fehlgeschlagen ist. Hat ein anderer Thread inzwischen erneuert, passiert nichts.
        /// </summary>
        public async Task<bool> Refresh(string failedToken)
        {
            await _refreshLock.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(AccessToken) && AccessToken != failedToken && AccessTokenExpires > DateTimeOffset.UtcNow.AddSeconds(60))
                {
                    return true;
                }
                if (!HasRefreshToken)
                {
                    return false;
                }
                Dictionary<string, string> form = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = RefreshToken,
                };
                return await RequestToken(form, true);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// Interaktiver Login: Öffnet die Stud.IP Anmeldeseite im Browser und wartet auf die Rückleitung
        /// an die Redirect-URI. Alternativ kann die URL, auf die der Browser weitergeleitet wurde, in die
        /// Konsole kopiert werden (z.B. wenn die Anwendung auf einem NAS ohne Browser läuft).
        /// </summary>
        public async Task<bool> InteractiveLogin(bool allowConsoleInput, Func<Task<string>> readLine = null)
        {
            string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            string state = Base64Url(RandomNumberGenerator.GetBytes(16));

            Uri authorize = BuildAuthorizeUri(challenge, state);

            Log.Info("OAuth2 Login: Bitte im Browser bei Stud.IP anmelden und den Zugriff erlauben:");
            Log.Info(authorize.ToString());

            using CancellationTokenSource cts = new CancellationTokenSource(LoginTimeout);
            List<Task<string>> waiters = new List<Task<string>>();

            HttpListener listener = TryStartListener();
            if (listener != null)
            {
                waiters.Add(WaitForCallback(listener, cts.Token));
            }
            if (allowConsoleInput)
            {
                Log.Info("Falls die Rückleitung nicht automatisch funktioniert: Die komplette URL aus der Adresszeile des Browsers (beginnt mit "
                    + RedirectUri.GetLeftPart(UriPartial.Path) + ") hier einfügen:");
                waiters.Add(readLine != null ? readLine() : Task.Run(() => Console.ReadLine() ?? ""));
            }
            if (waiters.Count == 0)
            {
                Log.Error("OAuth2 Login nicht möglich: Redirect-URI " + RedirectUri + " kann nicht lokal empfangen werden und Eingaben sind deaktiviert.");
                return false;
            }

            OpenBrowser?.Invoke(authorize);

            string callback;
            try
            {
                Task timeout = Task.Delay(Timeout.Infinite, cts.Token);
                Task finished = await Task.WhenAny(waiters.Cast<Task>().Append(timeout));
                if (finished == timeout)
                {
                    Log.Error("OAuth2 Login: Zeitüberschreitung.");
                    return false;
                }
                callback = await (Task<string>)finished;
            }
            catch (OperationCanceledException)
            {
                Log.Error("OAuth2 Login: Zeitüberschreitung.");
                return false;
            }
            finally
            {
                cts.Cancel();
                try { listener?.Stop(); listener?.Close(); } catch (Exception) { }
            }

            Dictionary<string, string> query = ParseCallback(callback);
            if (query.TryGetValue("error", out string error))
            {
                Log.Error("OAuth2 Login abgelehnt: " + error + (query.TryGetValue("error_description", out string desc) ? " (" + desc + ")" : ""));
                return false;
            }
            if (!query.TryGetValue("code", out string code) || string.IsNullOrEmpty(code))
            {
                Log.Error("OAuth2 Login: Kein Autorisierungscode erhalten.");
                return false;
            }
            if (query.TryGetValue("state", out string returnedState) && returnedState != state)
            {
                Log.Error("OAuth2 Login: Ungültiger state Parameter.");
                return false;
            }

            Dictionary<string, string> form = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri.ToString(),
                ["code_verifier"] = verifier,
            };
            return await RequestToken(form, false);
        }

        public Uri BuildAuthorizeUri(string codeChallenge, string state)
        {
            Dictionary<string, string> parameters = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri.ToString(),
                ["scope"] = Scope,
                ["state"] = state,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
            };
            string query = string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            string separator = string.IsNullOrEmpty(AuthorizeUrl.Query) ? "?" : "&";
            return new Uri(AuthorizeUrl + separator + query);
        }

        /// <summary>
        /// Akzeptiert die komplette Redirect-URL, nur den Query-String oder nur den Code.
        /// </summary>
        public static Dictionary<string, string> ParseCallback(string input)
        {
            input = (input ?? "").Trim();
            Dictionary<string, string> result = new Dictionary<string, string>();
            if (input.Length == 0)
            {
                return result;
            }
            int q = input.IndexOf('?');
            string query = q >= 0 ? input.Substring(q + 1) : input;
            int hash = query.IndexOf('#');
            if (hash >= 0)
            {
                query = query.Substring(0, hash);
            }
            if (!query.Contains('='))
            {
                result["code"] = query; // nur der Code wurde eingefügt
                return result;
            }
            foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split('=', 2);
                string key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
                string value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
                result[key] = value;
            }
            return result;
        }

        async Task<bool> RequestToken(Dictionary<string, string> form, bool isRefresh)
        {
            form["client_id"] = ClientId;
            if (ClientSecret != null)
            {
                form["client_secret"] = ClientSecret;
            }

            try
            {
                using HttpResponseMessage response = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
                string body = await response.Content.ReadAsStringAsync();
                JsonNode json = null;
                try { json = JsonNode.Parse(body); } catch (Exception) { }

                if (!response.IsSuccessStatusCode || json?["access_token"] == null)
                {
                    string detail = (string)json?["error_description"] ?? (string)json?["message"] ?? (string)json?["error"] ?? ((int)response.StatusCode + " " + response.ReasonPhrase);
                    Log.Error((isRefresh ? "OAuth2 Token konnte nicht erneuert werden: " : "OAuth2 Token konnte nicht abgerufen werden: ") + detail);
                    if (isRefresh && (response.StatusCode == HttpStatusCode.BadRequest || response.StatusCode == HttpStatusCode.Unauthorized))
                    {
                        // Refresh-Token ungültig/abgelaufen
                        SetRefreshToken(null);
                    }
                    return false;
                }

                AccessToken = (string)json["access_token"];
                long expiresIn = json["expires_in"] is JsonValue v && v.TryGetValue(out long e) ? e : 3600;
                AccessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                string newRefresh = (string)json["refresh_token"];
                if (!string.IsNullOrEmpty(newRefresh) && newRefresh != RefreshToken)
                {
                    SetRefreshToken(newRefresh);
                }
                return true;
            }
            catch (HttpRequestException ex)
            {
                Log.Error("OAuth2 Token-Endpunkt nicht erreichbar (" + TokenUrl + "): " + ex.Message);
                return false;
            }
        }

        void SetRefreshToken(string token)
        {
            RefreshToken = token;
            RefreshTokenChanged?.Invoke(token ?? "");
        }

        HttpListener TryStartListener()
        {
            if (!RedirectUri.IsLoopback || RedirectUri.Scheme != Uri.UriSchemeHttp)
            {
                return null;
            }
            try
            {
                HttpListener listener = new HttpListener();
                listener.Prefixes.Add(RedirectUri.GetLeftPart(UriPartial.Authority) + "/");
                listener.Start();
                return listener;
            }
            catch (Exception ex)
            {
                Log.Debug("Lokaler Empfang der OAuth2-Rückleitung nicht möglich: " + ex.Message);
                return null;
            }
        }

        async Task<string> WaitForCallback(HttpListener listener, CancellationToken token)
        {
            using CancellationTokenRegistration reg = token.Register(() => { try { listener.Stop(); } catch (Exception) { } });
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }

                bool isCallback = context.Request.Url != null
                    && string.Equals(context.Request.Url.AbsolutePath.TrimEnd('/'), RedirectUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                string html = isCallback
                    ? "<html><body><h3>StudIP-Downloader: Anmeldung abgeschlossen.</h3>Dieses Fenster kann geschlossen werden.</body></html>"
                    : "<html><body>Not found</body></html>";
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                context.Response.StatusCode = isCallback ? 200 : 404;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                context.Response.Close();

                if (isCallback)
                {
                    return context.Request.Url.ToString();
                }
            }
            throw new OperationCanceledException(token);
        }

        static void TryOpenBrowser(Uri uri)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", uri.ToString());
                }
                else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                {
                    Process.Start("xdg-open", uri.ToString());
                }
            }
            catch (Exception)
            {
                // Kein Browser verfügbar -> URL wurde bereits ausgegeben
            }
        }

        static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public void Dispose()
        {
            _http.Dispose();
            _refreshLock.Dispose();
        }
    }
}
