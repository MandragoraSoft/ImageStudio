using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using ImageStudio.Services;
using ImageStudio.ViewModels;
using ImageStudio.Views;

namespace ImageStudio.Tests;

// Czytelne błędy API, lista dostawców w filtrze modeli (per zakładka, bez zerowania wyboru) oraz katalogi nowych dostawców
public sealed class ApiErrorMessageTests
{
    [Theory]
    [InlineData("""{"error":{"message":"Incorrect API key provided","type":"invalid_request_error","code":"invalid_api_key"}}""", "Incorrect API key provided")]
    [InlineData("""{"error":"zły klucz"}""", "zły klucz")]
    [InlineData("""{"detail":"Invalid API Key"}""", "Invalid API Key")]
    [InlineData("""{"detail":[{"loc":["body","model"],"msg":"Field required"},{"msg":"Bad input"}]}""", "Field required; Bad input")]
    [InlineData("""{"type":"error","error":{"type":"MissingSessionID","message":"Request is missing x-opencode-session"}}""", "Request is missing x-opencode-session")]
    [InlineData("""{"error":{"code":"PAID_MODEL_AUTH_REQUIRED","message":"You need to sign in to use this model."},"error_type":"paid_model_auth_required"}""", "You need to sign in to use this model.")]
    [InlineData("""{"message":"Rate limit"}""", "Rate limit")]
    [InlineData("to nie json", "to nie json")]
    [InlineData("""{"inny":"kształt"}""", """{"inny":"kształt"}""")]
    public void Message_is_taken_from_the_json_body(string body, string expected) =>
        Assert.Equal(expected, ApiErrors.Message(body));

    [Fact]
    public void Empty_body_falls_back_to_the_status_and_long_text_is_shortened()
    {
        Assert.Equal("HTTP 502: BadGateway", ApiErrors.Failure(HttpStatusCode.BadGateway, "").Message);
        Assert.True(ApiErrors.Message(new string('x', 1000)).Length <= 301);
    }

    [Fact]
    public async Task Chat_error_shows_http_status_and_the_api_message_not_raw_json()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided"}}"""))
        {
            BaseAddress = new Uri("https://example.test"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OpenAiCompat.ChatAsync(http, "/v1/chat/completions", "m", [new ChatMessage("user", "x")], null, default));

        Assert.Equal("HTTP 401: Incorrect API key provided", error.Message);
    }
}

public sealed class ModelOwnerFilterTests
{
    private static ModelOption Option(string id, string? owner) => new(id, owner, false, ["text"], null);

    [Fact]
    public void Filter_bar_lists_only_providers_that_have_models_in_this_picker()
    {
        Sta.Run(() =>
        {
            var models = new ObservableCollection<ModelOption> { Option("a", "openai"), Option("b", "OpenAI"), Option("c", "venice") };
            var bar = new ModelFilterBar { ModelsSource = models };

            Assert.Equal([ModelOption.AllOwners, "openai", "venice"], bar.Providers);

            models.Add(Option("d", "google"));
            models.RemoveAt(2);
            Assert.Equal([ModelOption.AllOwners, "google", "openai"], bar.Providers);
        });
    }

    [Fact]
    public void Selected_provider_stays_on_the_list_even_without_models_here()
    {
        Sta.Run(() =>
        {
            var bar = new ModelFilterBar { ModelsSource = new[] { Option("a", "openai") }, ActiveProvider = "xai" };

            Assert.Equal([ModelOption.AllOwners, "openai", "xai"], bar.Providers);
            bar.ActiveProvider = ModelOption.AllOwners;
            Assert.Equal([ModelOption.AllOwners, "openai"], bar.Providers);
        });
    }

    [Fact]
    public void Null_from_the_combo_does_not_reset_the_provider_filter()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            app.Vm.SelectedProvider = "openai";

            app.Vm.SelectedProvider = null!;

            Assert.Equal("openai", app.Vm.SelectedProvider);
        });
    }

    [Fact]
    public void Provider_filter_ignores_letter_case()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Vm.InitializeAsync().GetAwaiter().GetResult();
            app.Vm.Models.Clear();
            app.Vm.Models.Add(Option("img", "OpenAI"));
            app.Vm.SelectedProvider = "openai";

            Assert.Single(app.Vm.ModelsView.Cast<ModelOption>());
        });
    }
}

public sealed class NewProviderCatalogTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void OpenAi_prices_come_from_the_openrouter_catalog_for_exact_and_dated_ids()
    {
        var catalog = """
            {"data":[
              {"id":"openai/gpt-4o-mini","pricing":{"prompt":"0.00000015","completion":"0.0000006","input_cache_read":"0.000000075"}},
              {"id":"openai/gpt-4o-mini:free","pricing":{"prompt":"0","completion":"0"}},
              {"id":"openai/gpt-x","pricing":{"prompt":"-1","completion":"-1"}},
              {"id":"anthropic/claude","pricing":{"prompt":"0.000003","completion":"0.000015"}}
            ]}
            """;

        var prices = OpenAiProvider.ParseCatalogPricing(catalog, ["gpt-4o-mini", "gpt-4o-mini-2024-07-18", "gpt-x", "whisper-1", "claude"]);

        Assert.Equal(["gpt-4o-mini", "gpt-4o-mini-2024-07-18"], prices.Keys.Order());
        var price = prices["gpt-4o-mini-2024-07-18"];
        Assert.Equal(0.15m, price.PriceNow);
        Assert.Equal(0.6m, price.OutputPrice);
        Assert.Equal(0.075m, price.CachePrice);
        Assert.Equal(ModelPricing.TokenUnit, price.Unit);
    }

    [Fact]
    public void OpenCodeGo_catalog_gives_modalities_owner_reasoning_and_prices()
    {
        var root = Parse("""
            {"object":"list","data":[
              {"id":"deepseek-v4.1-flash","type":"chat","canonical_id":"deepseek/deepseek-v4.1-flash","description":"Flash",
               "modalities":{"input":["text","image/png","application/pdf"],"output":["text"]},
               "capabilities":{"tools":{"supported":true},"reasoning":{"supported":true,"effort":["low","high","max"]}},
               "cost":{"text":{"input":{"price":0.15,"cache":{"read":0.003}},"output":{"price":0.6}}}},
              {"id":"step-5-preview","type":"chat","canonical_id":"stepfun/step-5-preview",
               "modalities":{"input":["text","video/mp4","audio/wav"],"output":["text"]},
               "capabilities":{"reasoning":{"supported":true,"effort":["low","medium","high"]}},
               "cost":{"text":{"input":{"price":1},"output":{"price":2.7}}}},
              {"id":"embed","type":"embedding","modalities":{"input":["text"],"output":["text"]}}
            ]}
            """);

        var models = OpenCodeGoProvider.ParseChatModels(root);

        Assert.Equal(["deepseek-v4.1-flash", "step-5-preview"], models.Select(m => m.Id));
        var flash = models[0];
        Assert.Equal("deepseek", flash.OwnedBy);
        Assert.True(flash.AcceptsImage);
        Assert.Equal(["text", "image", "file"], flash.InputModalities);
        Assert.Contains("tools", flash.SupportedParameters);
        Assert.Null(ReasoningSupport.ParameterFor(flash.SupportedParameters));  // brak "medium" = bez sterowania poziomem
        var step = models[1];
        Assert.Equal(["text", "video", "audio"], step.InputModalities);
        Assert.Equal("reasoning_effort", ReasoningSupport.ParameterFor(step.SupportedParameters));

        var prices = OpenCodeGoProvider.ParsePricing(root);
        Assert.Equal(2, prices.Count);
        Assert.Equal((0.15m, 0.6m, 0.003m), (prices["deepseek-v4.1-flash"].PriceNow, prices["deepseek-v4.1-flash"].OutputPrice, prices["deepseek-v4.1-flash"].CachePrice));
        Assert.Null(prices["step-5-preview"].CachePrice);
    }

    [Fact]
    public void Kilo_catalog_converts_token_prices_and_skips_router_prices()
    {
        var root = Parse("""
            {"data":[
              {"id":"anthropic/claude-opus-5.5","description":"Opus","architecture":{"input_modalities":["text","image","file","pdf"],"output_modalities":["text"]},
               "pricing":{"prompt":"0.000004","completion":"0.00002","input_cache_read":"0.0000002"},"supported_parameters":["tools","reasoning_effort"]},
              {"id":"kilo-auto/efficient","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"pricing":{"prompt":"-1","completion":"-1"},"supported_parameters":[]},
              {"id":"google/lyria","architecture":{"input_modalities":["text"],"output_modalities":["audio"]},"pricing":{"prompt":"0","completion":"0"}}
            ]}
            """);

        var models = KiloProvider.ParseChatModels(root);

        Assert.Equal(["anthropic/claude-opus-5.5", "kilo-auto/efficient"], models.Select(m => m.Id));
        Assert.Equal("anthropic", models[0].OwnedBy);
        Assert.Equal(["text", "image", "file"], models[0].InputModalities);  // pdf i file to jedno "file"
        Assert.Equal("reasoning_effort", ReasoningSupport.ParameterFor(models[0].SupportedParameters));

        var prices = KiloProvider.ParsePricing(root);
        Assert.False(prices.ContainsKey("kilo-auto/efficient"));
        Assert.Equal(4m, prices["anthropic/claude-opus-5.5"].PriceNow);
        Assert.Equal(20m, prices["anthropic/claude-opus-5.5"].OutputPrice);
        Assert.Equal(0.2m, prices["anthropic/claude-opus-5.5"].CachePrice);
        Assert.Equal(0m, prices["google/lyria"].PriceNow);
    }

    [Theory]
    [InlineData("""{"object":"list","data":[{"id":"mistral-small-latest","capabilities":{"completion_chat":true,"vision":true,"function_calling":true}},{"id":"mistral-embed","capabilities":{"completion_chat":false}},{"id":"old","archived":true,"capabilities":{"completion_chat":true}}]}""")]
    [InlineData("""[{"id":"mistral-small-latest","capabilities":{"completion_chat":true,"vision":true,"function_calling":true}},{"id":"mistral-embed","capabilities":{"completion_chat":false}}]""")]
    public void Mistral_catalog_keeps_chat_models_in_both_response_shapes(string json)
    {
        var models = MistralProvider.ParseChatModels(Parse(json));

        var model = Assert.Single(models);
        Assert.Equal("mistral-small-latest", model.Id);
        Assert.True(model.AcceptsImage);
        Assert.Contains("tools", model.SupportedParameters);
        Assert.DoesNotContain("seed", model.SupportedParameters);  // u Mistrala random_seed
    }

    [Fact]
    public async Task Text_only_providers_refuse_images_video_and_speech()
    {
        foreach (IAiProvider provider in new IAiProvider[] { new MistralProvider("k"), new KiloProvider("k"), new OpenCodeGoProvider("k") })
        {
            using (provider)
            {
                Assert.Empty((await provider.GetImageModelsAsync()).Generation);
                Assert.Empty(await provider.GetVideoModelsAsync());
                await Assert.ThrowsAsync<NotSupportedException>(() => provider.GenerateImageAsync("m", "p", "1x1", null));
                await Assert.ThrowsAsync<NotSupportedException>(() => provider.SynthesizeAsync("m", "t", null, null, "mp3"));
            }
        }
    }

    [Fact]
    public void Registry_creates_every_listed_provider()
    {
        var settings = new ImageStudio.Configuration.AppSettings("http://x", "http://x", 1024, "", "system");

        foreach (var info in ProviderRegistry.Available)
        {
            using var provider = ProviderRegistry.Create(info.Id, settings);
            Assert.Equal(info.Id, provider.Id);
            Assert.Equal(info.DefaultTextModel, provider.DefaultTextModel);
        }

        Assert.Contains(ProviderRegistry.Available, p => p.Id == "mistral");
        Assert.Contains(ProviderRegistry.Available, p => p.Id == "kilo");
        Assert.Contains(ProviderRegistry.Available, p => p.Id == "opencodego");
    }
}
