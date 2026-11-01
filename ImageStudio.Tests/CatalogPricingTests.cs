using System.Net;
using System.Text;
using ImageStudio.Services;

namespace ImageStudio.Tests;

// Cena modelu UberLLM, którego rynek nie wycenia (brak media_price_now_usd), bierze się z pola pricing katalogu modeli
public sealed class CatalogPricingTests
{
    private const string Image = """
    {"data":[
      {"id":"qwen/venice-qwen-image-2-1-turbo","available":true,"pricing":{"list_usd":0.02,"unit":"image","usd":0.02}},
      {"id":"fal-ai/flux/schnell","available":true,"pricing":{"list_usd":0.003,"unit":"image"}},
      {"id":"venice/qwen-image-3","available":true,"pricing":{"list_usd":0.04,"unit":"image","usd":0.024}},
      {"id":"bez/ceny","available":true}
    ]}
    """;

    private const string Video = """
    {"data":[{"id":"fal-ai/kling-video/v2.6/pro/text-to-video","available":true,"pricing":{"list_usd":0.07,"unit":"second","usd":0.042}}]}
    """;

    private const string Chat = """
    {"data":[{"id":"z-ai/glm-5.3-flash-uncensored","available":true,
              "pricing":{"input_usd":0.15,"output_usd":0.5,"cache_read_usd":0.03,"list_input_usd":0.0285,"unit":"1M tokens"}}]}
    """;

    // Rynek zna tylko venice/qwen-image-3 (z ceną na żywo) i qwen/venice-qwen-image-2-1-turbo bez media_price_now_usd
    private const string Market = """
    {"models":{
      "venice/qwen-image-3":{"media_price_now_usd":0.02,"media_unit":"image","media_list_usd":0.04,"market_offers":4},
      "qwen/venice-qwen-image-2-1-turbo":{"media_unit":"image","media_list_usd":0.02,"market_offers":2}
    }}
    """;

    [Fact]
    public void Catalog_pricing_uses_usd_then_list_usd_and_computes_the_discount()
    {
        var prices = ImageGenerationService.ParseCatalogPricing(Image);

        Assert.Equal(0.02m, prices["qwen/venice-qwen-image-2-1-turbo"].PriceNow);
        Assert.Equal("image", prices["qwen/venice-qwen-image-2-1-turbo"].Unit);
        Assert.Equal(0.003m, prices["fal-ai/flux/schnell"].PriceNow);  // tylko list_usd
        Assert.Null(prices["fal-ai/flux/schnell"].DiscountPct);
        var discounted = prices["venice/qwen-image-3"];
        Assert.Equal((0.024m, 0.04m, 40m), (discounted.PriceNow, discounted.ListUsd, discounted.DiscountPct));
        Assert.False(prices.ContainsKey("bez/ceny"));
    }

    [Fact]
    public void Catalog_pricing_of_text_models_is_per_million_tokens()
    {
        var price = ImageGenerationService.ParseCatalogPricing(Chat)["z-ai/glm-5.3-flash-uncensored"];

        Assert.Equal((0.15m, 0.5m, 0.03m), (price.PriceNow, price.OutputPrice, price.CachePrice));
        Assert.Equal(ModelPricing.TokenUnit, price.Unit);
    }

    [Fact]
    public async Task Provider_fills_missing_market_prices_from_the_catalog_and_keeps_live_market_prices()
    {
        using var listener = new HttpListener();
        var url = $"http://127.0.0.1:{TestPorts.Free()}";
        listener.Prefixes.Add(url + "/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }

                var query = context.Request.Url!.Query;
                var body = context.Request.Url.AbsolutePath.Contains("market") ? Market
                    : query.Contains("type=image") ? Image
                    : query.Contains("type=video") ? Video
                    : Chat;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
                context.Response.Close();
            }
        });
        using IAiProvider provider = new UberLlmProvider(url, "k", url);

        var prices = await provider.GetPricingAsync();

        Assert.Equal(0.02m, prices["qwen/venice-qwen-image-2-1-turbo"].PriceNow);  // z katalogu: rynek nie ma ceny teraz
        Assert.Equal(0.02m, prices["venice/qwen-image-3"].PriceNow);  // rynek na żywo wygrywa z katalogiem (0,024)
        Assert.Equal(0.042m, prices["fal-ai/kling-video/v2.6/pro/text-to-video"].PriceNow);
        Assert.Equal(0.15m, prices["z-ai/glm-5.3-flash-uncensored"].PriceNow);
        listener.Stop();
        await serving;
    }
}
