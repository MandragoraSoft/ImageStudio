using System.Net;
using System.Text.Json;
using ImageStudio.Services;

namespace ImageStudio.Tests;

public sealed class ChatModelCapabilitiesCatalogTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Uber_catalog_preserves_explicit_flags_and_falls_back_to_declared_parameters_and_modalities()
    {
        const string body = """
            {"data":[
              {"id":"explicit-true","available":true,"endpoints":["/v1/chat/completions"],"supports_tools":true,"supported_parameters":[],"supports_vision":true},
              {"id":"explicit-false","available":true,"endpoints":["/v1/chat/completions"],"supports_tools":false,"supported_parameters":["tools"],"supports_vision":false},
              {"id":"parameters","available":true,"endpoints":["/v1/chat/completions"],"supported_parameters":["tools"],"input_modalities":["text","image"],"supports_vision":false},
              {"id":"fallback","available":true,"endpoints":["/v1/chat/completions"],"supports_tools":null,"supported_parameters":["temperature"]},
              {"id":"empty","available":true,"endpoints":["/v1/chat/completions"],"supported_parameters":[],"input_modalities":[],"supports_vision":true},
              {"id":"unknown","available":true,"endpoints":["/v1/chat/completions"]},
              {"id":"invalid","available":true,"endpoints":["/v1/chat/completions"],"supports_tools":"true","supported_parameters":null,"supports_vision":null},
              {"id":"invalid-list","available":true,"endpoints":["/v1/chat/completions"],"supported_parameters":[null]},
              {"id":"other","available":false,"endpoints":["/v1/chat/completions"],"supports_tools":true}
            ]}
            """;
        using var service = new ImageGenerationService("https://example.test", "", () => new StubHandler(HttpStatusCode.OK, body));

        var models = (await service.GetChatModelsAsync()).ToDictionary(m => m.Id);

        Assert.Equal(8, models.Count);
        Assert.True(models["explicit-true"].SupportsTools);
        Assert.True(models["explicit-true"].AcceptsImage);
        Assert.Null(models["explicit-true"].InputModalities);
        Assert.False(models["explicit-false"].SupportsTools);
        Assert.False(models["explicit-false"].AcceptsImage);
        Assert.True(models["parameters"].SupportsTools);
        Assert.True(models["parameters"].AcceptsImage);
        Assert.Equal(["text", "image"], models["parameters"].InputModalities);
        Assert.False(models["fallback"].SupportsTools);
        Assert.False(models["empty"].SupportsTools);
        Assert.False(models["empty"].AcceptsImage);
        Assert.Empty(models["empty"].InputModalities!);
        Assert.Null(models["unknown"].SupportsTools);
        Assert.Null(models["unknown"].AcceptsImage);
        Assert.Null(models["invalid"].SupportsTools);
        Assert.Null(models["invalid"].AcceptsImage);
        Assert.Null(models["invalid-list"].SupportsTools);
    }

    [Fact]
    public async Task OpenRouter_catalog_distinguishes_empty_missing_and_invalid_parameters()
    {
        const string body = """
            {"data":[
              {"id":"a/yes","architecture":{"input_modalities":["image"],"output_modalities":["text"]},"supported_parameters":["tools"]},
              {"id":"b/no","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":[]},
              {"id":"c/unknown","architecture":{"output_modalities":["text"]}},
              {"id":"d/invalid","architecture":{"output_modalities":["text"]},"supported_parameters":null}
            ]}
            """;
        using var provider = new OpenRouterProvider("", "https://example.test", () => new StubHandler(HttpStatusCode.OK, body));
        var models = (await provider.GetChatModelsAsync()).ToDictionary(m => m.Id);

        Assert.True(models["a/yes"].SupportsTools);
        Assert.True(models["a/yes"].AcceptsImage);
        Assert.False(models["b/no"].SupportsTools);
        Assert.False(models["b/no"].AcceptsImage);
        Assert.Null(models["c/unknown"].SupportsTools);
        Assert.Null(models["c/unknown"].AcceptsImage);
        Assert.Null(models["d/invalid"].SupportsTools);
    }

    [Fact]
    public void Kilo_catalog_distinguishes_empty_missing_and_invalid_parameters()
    {
        var models = KiloProvider.ParseChatModels(Parse("""
            {"data":[
              {"id":"a/yes","architecture":{"output_modalities":["text"]},"supported_parameters":["tools"]},
              {"id":"b/no","architecture":{"output_modalities":["text"]},"supported_parameters":[]},
              {"id":"c/unknown","architecture":{"output_modalities":["text"]}},
              {"id":"d/invalid","architecture":{"output_modalities":["text"]},"supported_parameters":[null]}
            ]}
            """)).ToDictionary(m => m.Id);

        Assert.True(models["a/yes"].SupportsTools);
        Assert.False(models["b/no"].SupportsTools);
        Assert.Null(models["c/unknown"].SupportsTools);
        Assert.Null(models["d/invalid"].SupportsTools);
        Assert.Null(models["c/unknown"].AcceptsImage);
    }

    [Fact]
    public void Mistral_and_opencode_preserve_true_false_and_missing_flags()
    {
        var mistral = MistralProvider.ParseChatModels(Parse("""
            {"data":[
              {"id":"yes","capabilities":{"completion_chat":true,"function_calling":true,"vision":true}},
              {"id":"no","capabilities":{"completion_chat":true,"function_calling":false,"vision":false}},
              {"id":"unknown","capabilities":{"completion_chat":true,"function_calling":null}},
              {"id":"absent"}
            ]}
            """)).ToDictionary(m => m.Id);
        Assert.True(mistral["yes"].SupportsTools);
        Assert.False(mistral["no"].SupportsTools);
        Assert.Null(mistral["unknown"].SupportsTools);
        Assert.Null(mistral["absent"].SupportsTools);
        Assert.Null(mistral["absent"].AcceptsImage);

        var opencode = OpenCodeGoProvider.ParseChatModels(Parse("""
            {"data":[
              {"id":"yes","capabilities":{"tools":{"supported":true}}},
              {"id":"no","capabilities":{"tools":{"supported":false}}},
              {"id":"unknown","capabilities":{"tools":{"supported":null}}},
              {"id":"absent"}
            ]}
            """)).ToDictionary(m => m.Id);
        Assert.True(opencode["yes"].SupportsTools);
        Assert.False(opencode["no"].SupportsTools);
        Assert.Null(opencode["unknown"].SupportsTools);
        Assert.Null(opencode["absent"].SupportsTools);
        Assert.Null(opencode["absent"].AcceptsImage);
    }

    [Fact]
    public void Legacy_constructor_and_chatgpt_catalog_do_not_infer_tool_support()
    {
        var old = new ChatModelInfo("model", ["tools"]);
        var (id, parameters, image, owner, modalities, description, context) = old;
        Assert.Equal("model", id);
        Assert.Equal(["tools"], parameters);
        Assert.Null(image);
        Assert.Null(owner);
        Assert.Null(modalities);
        Assert.Null(description);
        Assert.Null(context);
        Assert.Null(old.SupportsTools);
        Assert.True((old with { SupportsTools = true }).SupportsTools);
        Assert.Null(Assert.Single(ChatGptProvider.ParseModels(Parse("""{"data":[{"id":"model"}]}"""))).SupportsTools);
    }

    [Fact]
    public async Task OpenAi_catalog_does_not_infer_tool_support_from_fixed_parameters()
    {
        using var provider = new OpenAiProvider("https://example.test", "",
            () => new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"gpt-4o-mini"}]}"""));

        var model = Assert.Single(await provider.GetChatModelsAsync());
        Assert.Null(model.SupportsTools);
    }
}
