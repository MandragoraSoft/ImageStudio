using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ImageStudio.Services;

// Logowanie "Sign in with ChatGPT" dla klienta open-source (OIDC + PKCE, dynamiczna rejestracja, bez sekretu klienta).
// Protokół: https://developers.openai.com/siwc/token-sharing-open-source/sign-in
// Zakres chatgpt.tokens.use.direct pozwala wykonywać zapytania Responses API z planu ChatGPT użytkownika
public sealed class ChatGptAuth(HttpClient? http = null, string authorizeUrl = ChatGptAuth.AuthorizeUrl, string tokenUrl = ChatGptAuth.TokenUrl)
{
    public const string AuthorizeUrl = "https://auth.openai.com/api/accounts/authorize";
    public const string TokenUrl = "https://auth.openai.com/api/accounts/oauth/token";
    public const string Issuer = "https://auth.openai.com";
    public const string Resource = "https://api.openai.com/v1";
    public const string Scope = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

    private const string RegistrationClientId = "dynamic_agent_client";
    private const string CallbackPath = "/auth/callback";
    private const int PreferredPort = 1455;
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    // PKCE: verifier to 32 losowe bajty, challenge to SHA-256 verifiera (oba base64url bez dopełnienia)
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Pierwsze logowanie rejestruje aplikację (dynamic_agent_client + nazwa), kolejne używa nadanego client_id
    public string BuildAuthorizeUrl(ChatGptSession session, string appName, string redirectUri, string state, string nonce, string challenge)
    {
        var query = new List<(string, string)>
        {
            ("response_type", "code"),
            ("client_id", session.ClientId ?? RegistrationClientId),
        };
        if (session.ClientId is null) query.Add(("agent_name_hint", appName));
        query.AddRange(
        [
            ("ext_agent_host_id", session.HostId),
            ("redirect_uri", redirectUri),
            ("scope", Scope),
            ("resource", Resource),
            ("state", state),
            ("nonce", nonce),
            ("code_challenge_method", "S256"),
            ("code_challenge", challenge),
        ]);
        return authorizeUrl + "?" + string.Join("&", query.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
    }

    // Całe logowanie: nasłuch na 127.0.0.1, przeglądarka systemowa, wymiana kodu na tokeny. doneHtml to treść strony po powrocie
    public async Task<ChatGptSession> SignInAsync(ChatGptSession? previous, string appName, Action<string> openBrowser,
                                                  string doneHtml, CancellationToken ct)
    {
        var session = previous ?? ChatGptSession.New();
        var verifier = NewVerifier();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = StartListener(out var port);
        var redirectUri = $"http://127.0.0.1:{port}{CallbackPath}";
        openBrowser(BuildAuthorizeUrl(session, appName, redirectUri, state, nonce, Challenge(verifier)));

        var query = await WaitForCallbackAsync(listener, doneHtml, ct);
        if (query["state"] != state) throw new InvalidOperationException("Logowanie ChatGPT: nieprawidłowy parametr state.");
        if (query["error"] is { Length: > 0 } error)
            throw new InvalidOperationException($"Logowanie ChatGPT przerwane: {query["error_description"] ?? error}");
        var code = query["code"] ?? throw new InvalidOperationException("Logowanie ChatGPT: brak kodu autoryzacji.");

        // Nowy client_id wraca w callbacku; przy ponownym logowaniu może go nie być, ale inny niż zapisany jest błędem
        var issued = query["client_id"];
        if (session.ClientId is not null && issued is { Length: > 0 } && issued != session.ClientId)
            throw new InvalidOperationException("Logowanie ChatGPT: callback podał inny client_id niż zapisany.");
        var clientId = session.ClientId ?? issued;
        if (string.IsNullOrEmpty(clientId))
            throw new InvalidOperationException("Logowanie ChatGPT: rejestracja niekompletna (brak nadanego client_id).");

        var token = await PostTokenAsync(
        [
            ("grant_type", "authorization_code"), ("client_id", clientId), ("code", code),
            ("code_verifier", verifier), ("redirect_uri", redirectUri), ("resource", Resource),
        ], ct);
        var signedIn = ApplyTokens(session with { ClientId = clientId }, token, nonce);
        if (!signedIn.PlanEnabled)
            throw new InvalidOperationException("Plan ChatGPT nie jest włączony dla tej aplikacji (brak zgody chatgpt.tokens.use.direct). " +
                                                "Zaloguj się ponownie i zaakceptuj dostęp do planu albo użyj klucza API.");
        return signedIn;
    }

    // Odświeżenie przed wygaśnięciem access tokenu; refresh token jest wymieniany na nowy (zapisz zwróconą sesję)
    public async Task<ChatGptSession> RefreshAsync(ChatGptSession session, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(session.RefreshToken) || string.IsNullOrEmpty(session.ClientId))
            throw new InvalidOperationException("Sesja ChatGPT wygasła: zaloguj się ponownie w Ustawieniach (Dostawca AI → ChatGPT).");

        var token = await PostTokenAsync(
        [
            ("grant_type", "refresh_token"), ("client_id", session.ClientId), ("refresh_token", session.RefreshToken),
            ("resource", Resource),
        ], ct);
        return ApplyTokens(session, token, nonce: null);
    }

    private async Task<JsonElement> PostTokenAsync(IEnumerable<(string, string)> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form.Select(p => new KeyValuePair<string, string>(p.Item1, p.Item2)));
        using var response = await _http.PostAsync(tokenUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ApiErrors.Failure(response.StatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    // Wynik wymiany: access_token, refresh_token (tylko z offline_access), id_token, expires_in, scope.
    // ID token sprawdzamy (iss, aud, exp, nonce); podpisu nie, bo przychodzi wprost z endpointu tokenów po TLS
    // ponytail: bez weryfikacji podpisu JWKS; dodać, gdy ID token zacznie służyć do czegoś więcej niż wyświetlenia e-maila
    private static ChatGptSession ApplyTokens(ChatGptSession session, JsonElement token, string? nonce)
    {
        string? Text(string name) => token.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var access = Text("access_token") ?? throw new InvalidOperationException("Odpowiedź logowania ChatGPT nie zawiera access_token.");
        var expiresIn = token.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : 3600;
        var email = session.Email;
        if (Text("id_token") is { } idToken) email = ValidateIdToken(idToken, session.ClientId!, nonce) ?? email;

        return session with
        {
            AccessToken = access,
            RefreshToken = Text("refresh_token") ?? session.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Email = email,
            Scope = Text("scope") ?? session.Scope,
        };
    }

    // Zwraca e-mail z ID tokenu; zły issuer, odbiorca, termin albo nonce przerywa logowanie
    public static string? ValidateIdToken(string idToken, string clientId, string? nonce)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("Nieprawidłowy ID token ChatGPT.");

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var claims = doc.RootElement;

        if (!claims.TryGetProperty("iss", out var iss) || iss.GetString() != Issuer)
            throw new InvalidOperationException("ID token ChatGPT ma nieprawidłowego wystawcę.");
        if (!claims.TryGetProperty("aud", out var aud)
            || !(aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Any(a => a.GetString() == clientId) : aud.GetString() == clientId))
            throw new InvalidOperationException("ID token ChatGPT jest wystawiony dla innej aplikacji.");
        if (!claims.TryGetProperty("exp", out var exp) || DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("ID token ChatGPT wygasł.");
        if (nonce is not null && (!claims.TryGetProperty("nonce", out var n) || n.GetString() != nonce))
            throw new InvalidOperationException("ID token ChatGPT ma nieprawidłowy nonce.");

        return claims.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String ? email.GetString() : null;
    }

    // Port 1455 z dokumentacji, a gdy zajęty, losowy (dozwolona jest zmiana samego portu); host musi być 127.0.0.1, nie localhost
    private static HttpListener StartListener(out int port)
    {
        var candidates = new[] { PreferredPort }.Concat(Enumerable.Range(0, 20).Select(_ => Random.Shared.Next(49152, 65000)));
        foreach (var candidate in candidates)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
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

        throw new InvalidOperationException("Nie udało się otworzyć lokalnego portu na callback logowania ChatGPT.");
    }

    private static async Task<System.Collections.Specialized.NameValueCollection> WaitForCallbackAsync(
        HttpListener listener, string doneHtml, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        try
        {
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var isCallback = context.Request.Url?.AbsolutePath == CallbackPath;
                var bytes = Encoding.UTF8.GetBytes(isCallback ? $"<!doctype html><meta charset=\"utf-8\"><title>ImageStudio</title><p>{WebUtility.HtmlEncode(doneHtml)}</p>" : "");
                context.Response.StatusCode = isCallback ? 200 : 404;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(bytes, ct);
                context.Response.Close();
                if (isCallback) return HttpUtility.ParseQueryString(context.Request.Url!.Query);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Logowanie ChatGPT: upłynął czas oczekiwania na powrót z przeglądarki.");
        }
    }
}
