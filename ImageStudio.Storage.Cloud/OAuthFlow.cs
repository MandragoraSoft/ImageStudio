using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ImageStudio.Storage.Cloud;

// Adresy i zakres jednego dostawcy OAuth. RedirectHost: "localhost" (Microsoft) albo "127.0.0.1" (Google);
// AuthorizeExtra to dodatkowe parametry strony logowania (np. access_type=offline)
public sealed record OAuthEndpoints(string AuthorizeUrl, string TokenUrl, string Scope, string RedirectHost = "127.0.0.1",
                                    IReadOnlyDictionary<string, string>? AuthorizeExtra = null);

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Email = null)
{
    // Odświeżamy z zapasem, żeby token nie wygasł w trakcie wysyłki
    public bool IsFresh => ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60);
}

// Logowanie przez przeglądarkę systemową: kod autoryzacji + PKCE, powrót na lokalny adres, wymiana kodu na tokeny.
// Klient publiczny (aplikacja desktopowa); sekret klienta wysyłany tylko, gdy dostawca go wymaga (Google)
public sealed class OAuthFlow(HttpClient http, Action<string>? openBrowser = null)
{
    // Bez ścieżki: Entra ignoruje port adresu http://localhost, ale resztę adresu porównuje z zarejestrowanym
    private const string CallbackPath = "/";
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly Action<string> _openBrowser = openBrowser ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));

    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string BuildAuthorizeUrl(OAuthEndpoints ep, string clientId, string redirectUri, string state, string challenge)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", clientId), new("redirect_uri", redirectUri), new("scope", ep.Scope),
            new("state", state), new("code_challenge_method", "S256"), new("code_challenge", challenge),
        };
        if (ep.AuthorizeExtra is not null) query.AddRange(ep.AuthorizeExtra);
        return ep.AuthorizeUrl + "?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    public async Task<OAuthTokens> SignInAsync(OAuthEndpoints ep, string clientId, string? clientSecret, CancellationToken ct)
    {
        var verifier = NewVerifier();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = StartListener(ep.RedirectHost, out var port);
        var redirectUri = $"http://{ep.RedirectHost}:{port}";
        _openBrowser(BuildAuthorizeUrl(ep, clientId, redirectUri, state, Challenge(verifier)));

        var query = await WaitForCallbackAsync(listener, ct);
        if (query["state"] != state) throw new InvalidOperationException("Logowanie: nieprawidłowy parametr state.");
        if (query["error"] is { Length: > 0 } error)
            throw new InvalidOperationException($"Logowanie przerwane: {query["error_description"] ?? error}");
        var code = query["code"] ?? throw new InvalidOperationException("Logowanie: brak kodu autoryzacji.");

        return await PostTokenAsync(ep, [("grant_type", "authorization_code"), ("client_id", clientId), ("code", code),
                                         ("code_verifier", verifier), ("redirect_uri", redirectUri)], clientSecret, previousRefresh: null, ct);
    }

    public Task<OAuthTokens> RefreshAsync(OAuthEndpoints ep, string clientId, string? clientSecret, string refreshToken, CancellationToken ct,
                                          string? previousEmail = null) =>
        PostTokenAsync(ep, [("grant_type", "refresh_token"), ("client_id", clientId), ("refresh_token", refreshToken)],
                       clientSecret, refreshToken, ct, previousEmail);

    private async Task<OAuthTokens> PostTokenAsync(OAuthEndpoints ep, IEnumerable<(string, string)> form, string? clientSecret,
                                                   string? previousRefresh, CancellationToken ct, string? previousEmail = null)
    {
        var fields = form.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)).ToList();
        if (!string.IsNullOrEmpty(clientSecret)) fields.Add(new("client_secret", clientSecret));

        using var response = await http.PostAsync(ep.TokenUrl, new FormUrlEncodedContent(fields), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Logowanie odrzucone ({(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("Odpowiedź logowania nie zawiera access_token.");
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600;
        // Odświeżenie często nie zwraca nowego refresh tokenu: zostaje poprzedni
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rt ? rt : previousRefresh;
        var email = root.TryGetProperty("id_token", out var idToken) ? EmailFrom(idToken.GetString()) : null;
        return new OAuthTokens(access, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn), email ?? previousEmail);
    }

    // E-mail do wyświetlenia w ustawieniach. Podpisu tokenu nie sprawdzamy: przychodzi wprost z endpointu tokenów po TLS,
    // a wartość służy tylko do pokazania, kto jest zalogowany
    // ponytail: bez weryfikacji podpisu JWT; dodać, gdy e-mail zacznie decydować o czymkolwiek poza etykietą
    public static string? EmailFrom(string? idToken)
    {
        try
        {
            var payload = idToken?.Split('.') is { Length: 3 } parts ? parts[1].Replace('-', '+').Replace('_', '/') : null;
            if (payload is null) return null;
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            foreach (var claim in new[] { "email", "preferred_username" })
                if (doc.RootElement.TryGetProperty(claim, out var v) && v.GetString() is { Length: > 0 } text) return text;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }

        return null;
    }

    // Wolny port wskazany przez system; host musi zgadzać się z zarejestrowanym adresem przekierowania
    private static HttpListener StartListener(string host, out int port)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = Random.Shared.Next(49152, 65000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{candidate}/");
            try
            {
                listener.Start();
                port = candidate;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Nie udało się otworzyć lokalnego portu na powrót z logowania.");
    }

    private static async Task<System.Collections.Specialized.NameValueCollection> WaitForCallbackAsync(HttpListener listener, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var isCallback = context.Request.Url is { } url && url.AbsolutePath == CallbackPath
                                 && HttpUtility.ParseQueryString(url.Query) is { } q && (q["code"] is not null || q["error"] is not null);
                var bytes = Encoding.UTF8.GetBytes(isCallback
                    ? "<!doctype html><meta charset=\"utf-8\"><title>ImageStudio</title><p>Możesz wrócić do ImageStudio. / You can return to ImageStudio.</p>"
                    : "");
                context.Response.StatusCode = isCallback ? 200 : 404;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes, ct);
                context.Response.Close();
                if (isCallback) return HttpUtility.ParseQueryString(context.Request.Url!.Query);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Logowanie: upłynął czas oczekiwania na powrót z przeglądarki.");
        }
    }
}
