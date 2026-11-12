using System.Net;
using System.Text;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// "Sign in with ChatGPT": PKCE, adres autoryzacji, ID token, wymiana i odświeżenie tokenów, żądanie i strumień Responses API
public sealed class ChatGptProviderTests
{
    private static string Jwt(object claims) =>
        "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    private static object Claims(string aud = "oaiapp_1", string iss = ChatGptAuth.Issuer, string? nonce = "n1", long? exp = null) => new
    {
        iss, aud, nonce, email = "ja@example.com", exp = exp ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
    };

    // Serwer udaje endpoint tokenów i API (zapisuje żądania)
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        public List<(string Method, string Path, string Body, string? Auth)> Requests { get; } = [];
        public string Url { get; } = $"http://127.0.0.1:{TestPorts.Free()}";

        public Server(Func<string, string, (int, string, string)> route)
        {
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }

                    using var reader = new StreamReader(c.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    var path = c.Request.Url!.AbsolutePath;
                    Requests.Add((c.Request.HttpMethod, path, body, c.Request.Headers["Authorization"]));
                    var (status, type, text) = route(c.Request.HttpMethod, path);
                    c.Response.StatusCode = status;
                    c.Response.ContentType = type;
                    await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
                    c.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public void Pkce_challenge_is_the_base64url_sha256_of_the_verifier()
    {
        // wektor z RFC 7636, dodatek B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ChatGptAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.NotEqual(ChatGptAuth.NewVerifier(), ChatGptAuth.NewVerifier());
    }

    [Fact]
    public void First_sign_in_registers_the_app_and_later_ones_use_the_issued_client_id()
    {
        var auth = new ChatGptAuth();
        var fresh = new ChatGptSession("urn:uuid:host");

        var first = new Uri(auth.BuildAuthorizeUrl(fresh, "ImageStudio", "http://127.0.0.1:1455/auth/callback", "st", "no", "ch"));
        var query = System.Web.HttpUtility.ParseQueryString(first.Query);
        Assert.Equal("auth.openai.com", first.Host);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("ImageStudio", query["agent_name_hint"]);
        Assert.Equal("urn:uuid:host", query["ext_agent_host_id"]);
        Assert.Equal("http://127.0.0.1:1455/auth/callback", query["redirect_uri"]);
        Assert.Equal(ChatGptAuth.Resource, query["resource"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Contains("chatgpt.tokens.use.direct", query["scope"]);
        Assert.Contains("offline_access", query["scope"]);

        var again = System.Web.HttpUtility.ParseQueryString(new Uri(auth.BuildAuthorizeUrl(fresh with { ClientId = "oaiapp_1" }, "ImageStudio", "http://127.0.0.1:1455/auth/callback", "st", "no", "ch")).Query);
        Assert.Equal("oaiapp_1", again["client_id"]);
        Assert.Null(again["agent_name_hint"]);
    }

    [Fact]
    public void Id_token_must_match_issuer_audience_expiry_and_nonce()
    {
        Assert.Equal("ja@example.com", ChatGptAuth.ValidateIdToken(Jwt(Claims()), "oaiapp_1", "n1"));
        Assert.Throws<InvalidOperationException>(() => ChatGptAuth.ValidateIdToken(Jwt(Claims(iss: "https://evil.example")), "oaiapp_1", "n1"));
        Assert.Throws<InvalidOperationException>(() => ChatGptAuth.ValidateIdToken(Jwt(Claims(aud: "inna")), "oaiapp_1", "n1"));
        Assert.Throws<InvalidOperationException>(() => ChatGptAuth.ValidateIdToken(Jwt(Claims(nonce: "zly")), "oaiapp_1", "n1"));
        Assert.Throws<InvalidOperationException>(() => ChatGptAuth.ValidateIdToken(Jwt(Claims(exp: 1)), "oaiapp_1", "n1"));
        Assert.Equal("ja@example.com", ChatGptAuth.ValidateIdToken(Jwt(Claims(nonce: null)), "oaiapp_1", null));  // odświeżenie nie ma nonce
    }

    [Fact]
    public async Task Refresh_exchanges_the_rotating_token_and_keeps_registration()
    {
        var idToken = Jwt(Claims(nonce: null));
        using var server = new Server((_, _) => (200, "application/json",
            $$"""{"access_token":"nowy-access","refresh_token":"nowy-refresh","id_token":"{{idToken}}","expires_in":3600,"scope":"openid chatgpt.tokens.use.direct"}"""));
        var auth = new ChatGptAuth(null, tokenUrl: server.Url + "/token");
        var session = new ChatGptSession("urn:uuid:h", "oaiapp_1", "stary", "stary-refresh", DateTimeOffset.UtcNow.AddMinutes(-1));

        var renewed = await auth.RefreshAsync(session, default);

        Assert.Equal("nowy-access", renewed.AccessToken);
        Assert.Equal("nowy-refresh", renewed.RefreshToken);
        Assert.Equal("oaiapp_1", renewed.ClientId);
        Assert.Equal("ja@example.com", renewed.Email);
        Assert.True(renewed.PlanEnabled);
        Assert.True(renewed.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
        var form = System.Web.HttpUtility.ParseQueryString(server.Requests.Single().Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("stary-refresh", form["refresh_token"]);
        Assert.Equal("oaiapp_1", form["client_id"]);
        Assert.Equal(ChatGptAuth.Resource, form["resource"]);
        Assert.Null(form["scope"]);  // bez scope zostaje dotychczasowa zgoda
    }

    [Fact]
    public async Task Refresh_failure_shows_the_api_message_and_signed_out_session_cannot_refresh()
    {
        using var server = new Server((_, _) => (400, "application/json", """{"error":"invalid_grant","error_description":"Refresh token reused"}"""));
        var auth = new ChatGptAuth(null, tokenUrl: server.Url + "/token");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            auth.RefreshAsync(new ChatGptSession("h", "c", "a", "r"), default));
        Assert.StartsWith("HTTP 400: ", error.Message);

        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.RefreshAsync(new ChatGptSession("h", "c"), default));
    }

    [Fact]
    public void Session_survives_json_and_sign_out_keeps_the_registration()
    {
        var session = new ChatGptSession("urn:uuid:h", "oaiapp_1", "a", "r", DateTimeOffset.UtcNow, "x@y", "chatgpt.tokens.use.direct");

        var loaded = ChatGptSession.Parse(session.ToJson());
        Assert.Equal(session, loaded);
        Assert.True(loaded.IsSignedIn);

        var signedOut = loaded.SignedOut();
        Assert.False(signedOut.IsSignedIn);
        Assert.Equal(("urn:uuid:h", "oaiapp_1"), (signedOut.HostId, signedOut.ClientId));

        Assert.False(ChatGptSession.Parse("{zepsute").IsSignedIn);
        Assert.StartsWith("urn:uuid:", ChatGptSession.Parse(null).HostId);
    }

    [Fact]
    public void Request_body_follows_the_plan_route_rules()
    {
        var messages = new List<ChatMessage>
        {
            new("system", "Bądź zwięzły"),
            new("user", "Cześć"),
            new("assistant", "", [new ToolCall("call_1", "szukaj", "{\"q\":1}")]),
            new("tool", "wynik", ToolCallId: "call_1", ToolName: "szukaj"),
            new("assistant", "Gotowe"),
            new("user", "Dzięki", Images: [new ChatImage([1, 2], "image/png")]),
        };
        var tools = new[] { new ToolDefinition("szukaj", "Szuka", """{"type":"object","properties":{}}""") };

        var body = JsonSerializer.SerializeToElement(ChatGptProvider.BuildBody("gpt-x", messages, tools));

        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("Bądź zwięzły", body.GetProperty("instructions").GetString());
        foreach (var forbidden in new[] { "temperature", "max_output_tokens", "top_p", "previous_response_id", "stream_options" })
            Assert.False(body.TryGetProperty(forbidden, out _), forbidden);

        var input = body.GetProperty("input").EnumerateArray().ToList();
        Assert.DoesNotContain(input, i => i.TryGetProperty("role", out var r) && r.GetString() == "system");
        Assert.Equal(["user", "function_call", "function_call_output", "assistant", "user"],
                     input.Select(i => i.TryGetProperty("type", out var t) ? t.GetString()! : i.GetProperty("role").GetString()!));
        Assert.Equal("call_1", input[1].GetProperty("call_id").GetString());
        Assert.Equal("input_image", input[4].GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal("szukaj", body.GetProperty("tools")[0].GetProperty("name").GetString());  // płaska definicja funkcji
    }

    [Fact]
    public void Model_catalog_keeps_listed_models_and_uses_slugs()
    {
        var root = JsonDocument.Parse("""{"models":[{"slug":"a","display_name":"Model A","visibility":"list"},{"slug":"b","visibility":"hide"},{"slug":"c"}]}""").RootElement;

        var models = ChatGptProvider.ParseModels(root);

        Assert.Equal(["a", "c"], models.Select(m => m.Id));
        Assert.Equal("Model A", models[0].Description);
    }

    private static string Sse(params string[] events) => string.Join("\n\n", events.Select(e => "data: " + e)) + "\n\n";

    [Fact]
    public async Task Rebuild_uses_three_fields_and_history_in_responses_api()
    {
        var content = """{"system":"S","concept":"C","negative":"N","changes":"zmiana"}""";
        var delta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = content });
        using var server = new Server((_, _) => (200, "text/event-stream",
            Sse(delta, """{"type":"response.completed","response":{}}""")));
        var session = new ChatGptSession("h", "c", "tok", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson();
        using IAiProvider provider = new ChatGptProvider(session, null, null, server.Url + "/");
        var history = new[] { new PromptRevision("s0", "c0", "S0", "C0", "i0", null, "n0", "N0") };

        var result = await provider.EnhancePromptAsync("m", "i", "s", "c", new Dictionary<string, object>(), history, negative: "n");

        Assert.Equal(new PromptParts("S", "C", "zmiana", "N"), result);
        var body = server.Requests.Single().Body;
        using var json = JsonDocument.Parse(body);
        Assert.Contains("negative", json.RootElement.GetProperty("instructions").GetString());
        var inputs = json.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains("(negative):\nn0", inputs[0].GetProperty("content").GetString());
        Assert.Contains("\"negative\":\"N0\"", inputs[1].GetProperty("content").GetString());
        Assert.Contains("(negative):\nn", inputs[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Chat_streams_text_collects_calls_and_usage_and_refreshes_an_expiring_token()
    {
        var stream = Sse(
            """{"type":"response.output_text.delta","delta":"Hel"}""",
            """{"type":"response.output_text.delta","delta":"lo"}""",
            """{"type":"response.reasoning_summary_text.delta","delta":"myślę"}""",
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c1","name":"szukaj","arguments":"{\"q\":2}"}}""",
            """{"type":"response.completed","response":{"usage":{"input_tokens":10,"output_tokens":4,"input_tokens_details":{"cached_tokens":6}}}}""");
        var idToken = Jwt(Claims(nonce: null));
        using var server = new Server((method, path) => path.EndsWith("/token")
            ? (200, "application/json", $$"""{"access_token":"swiezy","refresh_token":"r2","id_token":"{{idToken}}","expires_in":3600}""")
            : (200, "text/event-stream", stream));
        var saved = new List<string>();
        using var provider = new ChatGptProvider(new ChatGptSession("h", "oaiapp_1", "wygasa", "r1", DateTimeOffset.UtcNow.AddSeconds(30)).ToJson(),
                                                 saved.Add, new ChatGptAuth(null, tokenUrl: server.Url + "/token"), server.Url + "/");
        var deltas = new List<ChatDelta>();

        var reply = await provider.ChatStreamAsync("gpt-x", [new ChatMessage("user", "hej")], null, deltas.Add);

        Assert.Equal("Hello", reply.Content);
        Assert.Equal("myślę", reply.Reasoning);
        Assert.Equal(new ToolCall("c1", "szukaj", "{\"q\":2}"), reply.ToolCalls.Single());
        Assert.Equal(new TokenUsage(10, 4, 6), reply.Usage);
        Assert.Equal(["Hel", "lo", null], deltas.Select(d => d.Content));
        var api = server.Requests.Single(r => r.Path.EndsWith("/responses"));
        Assert.Equal("Bearer swiezy", api.Auth);
        Assert.Equal("r2", ChatGptSession.Parse(saved.Single()).RefreshToken);  // obrócony refresh token jest zapisany
    }

    [Fact]
    public async Task Stream_failure_and_missing_completion_are_errors()
    {
        var session = new ChatGptSession("h", "c", "tok", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson();

        using var limited = new Server((_, _) => (200, "text/event-stream",
            Sse("""{"type":"response.failed","response":{"error":{"code":"subscription_sharing_usage_limit_exceeded","message":"Limit planu"}}}""")));
        using (var provider = new ChatGptProvider(session, null, null, limited.Url + "/"))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ChatAsync("m", [new ChatMessage("user", "x")]));
            Assert.Equal("subscription_sharing_usage_limit_exceeded: Limit planu", error.Message);
        }

        using var cut = new Server((_, _) => (200, "text/event-stream", Sse("""{"type":"response.output_text.delta","delta":"ur"}""")));
        using (var provider = new ChatGptProvider(session, null, null, cut.Url + "/"))
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ChatAsync("m", [new ChatMessage("user", "x")]));

        using var denied = new Server((_, _) => (403, "application/json", """{"detail":"Region not supported"}"""));
        using (var provider = new ChatGptProvider(session, null, null, denied.Url + "/"))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ChatAsync("m", [new ChatMessage("user", "x")]));
            Assert.Equal("HTTP 403: Region not supported", error.Message);
        }
    }

    [Fact]
    public async Task Not_signed_in_gives_a_clear_message_and_no_request()
    {
        using var server = new Server((_, _) => (200, "text/event-stream", ""));
        using var provider = new ChatGptProvider("", null, null, server.Url + "/");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatModelsAsync());

        Assert.Contains("zaloguj", error.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Sign_in_and_sign_out_buttons_are_mutually_exclusive()
    {
        var signedIn = new ChatGptSession("h", "c", "a", "r", DateTimeOffset.UtcNow.AddHours(1), "x@y", ChatGptSession.PlanScope);
        var vm = new ImageStudio.ViewModels.ChatGptSettingsViewModel("", (_, _) => Task.FromResult(signedIn));

        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.False(vm.SignOutCommand.CanExecute(null));

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.SignInCommand.CanExecute(null));
        Assert.True(vm.SignOutCommand.CanExecute(null));

        vm.SignOutCommand.Execute(null);

        Assert.False(vm.IsSignedIn);
        Assert.True(vm.SignInCommand.CanExecute(null));
        Assert.False(vm.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public void Reasoning_is_requested_with_effort_and_summary_only_when_enabled()
    {
        var on = JsonSerializer.SerializeToElement(ChatGptProvider.BuildBody("m", [new ChatMessage("user", "x")], null, new ChatOptions("reasoning_effort", true, "high")));
        Assert.Equal("high", on.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("auto", on.GetProperty("reasoning").GetProperty("summary").GetString());

        var off = JsonSerializer.SerializeToElement(ChatGptProvider.BuildBody("m", [new ChatMessage("user", "x")], null, new ChatOptions("reasoning_effort", false, "high")));
        Assert.False(off.TryGetProperty("reasoning", out _));
        Assert.False(JsonSerializer.SerializeToElement(ChatGptProvider.BuildBody("m", [new ChatMessage("user", "x")], null)).TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void Models_declare_reasoning_unless_the_catalog_says_they_have_none()
    {
        var root = JsonDocument.Parse("""
            {"models":[
              {"slug":"nieznany","visibility":"list"},
              {"slug":"mysli","visibility":"list","supported_reasoning_levels":[{"effort":"low"},{"effort":"high"}]},
              {"slug":"bez","visibility":"list","supported_reasoning_levels":[]}
            ]}
            """).RootElement;

        var models = ChatGptProvider.ParseModels(root).ToDictionary(m => m.Id);

        Assert.Equal("reasoning_effort", ReasoningSupport.ParameterFor(models["nieznany"].SupportedParameters));
        Assert.Equal("reasoning_effort", ReasoningSupport.ParameterFor(models["mysli"].SupportedParameters));
        Assert.Null(ReasoningSupport.ParameterFor(models["bez"].SupportedParameters));
    }

    [Fact]
    public async Task Chat_streams_reasoning_summary_and_retries_without_reasoning_when_the_model_rejects_it()
    {
        var calls = 0;
        using var server = new Server((_, _) => ++calls == 1
            ? (400, "application/json", """{"error":{"message":"Unsupported parameter: 'reasoning.effort' is not supported with this model."}}""")
            : (200, "text/event-stream", Sse("""{"type":"response.output_text.delta","delta":"ok"}""", """{"type":"response.completed","response":{}}""")));
        using var provider = new ChatGptProvider(new ChatGptSession("h", "c", "tok", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson(), null, null, server.Url + "/");

        var reply = await provider.ChatAsync("gpt-x", [new ChatMessage("user", "x")], null, default, new ChatOptions("reasoning_effort", true, "medium"));

        Assert.Equal("ok", reply.Content);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("\"reasoning\"", server.Requests[0].Body);
        Assert.DoesNotContain("\"reasoning\"", server.Requests[1].Body);
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        using var server = new Server((_, _) => (403, "application/json", """{"detail":"Region not supported"}"""));
        using var provider = new ChatGptProvider(new ChatGptSession("h", "c", "tok", "r", DateTimeOffset.UtcNow.AddHours(1)).ToJson(), null, null, server.Url + "/");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ChatAsync("m", [new ChatMessage("user", "x")], null, default, new ChatOptions("reasoning_effort", true, "high")));

        Assert.Single(server.Requests);
    }
}
