using System.Net;
using System.Text;
using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

// Ceny modeli tekstowych (za 1M tokenów) z rynku UberLLM i ich pokazanie na liście i w dymku
public sealed class PricingTests
{
    private const string Market = """
    {"models":{
      "venice/uncensored":{"price_now":{"input_usd_per_mtok":0.775,"output_usd_per_mtok":1.55,"cache_read_usd_per_mtok":0.0775},
                           "source_discount_pct":38,"market_offers":25,"requests_24h":4278},
      "venice/z-image-turbo":{"media_price_now_usd":0.004,"media_unit":"image","media_list_usd":0.005,"market_offers":3},
      "no/price":{"market_offers":1},
      "only/input":{"price_now":{"input_usd_per_mtok":2}}
    }}
    """;

    // Lokalny serwer udający publiczny rynek cen
    private static async Task<IReadOnlyDictionary<string, ModelPricing>> FetchAsync(string json)
    {
        using var listener = new HttpListener();
        var port = TestPorts.Free();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

        using var market = new MarketService($"http://127.0.0.1:{port}");
        var prices = await market.GetPricingAsync();
        await serving;
        return prices;
    }

    [Fact]
    public async Task Market_prices_cover_text_models_in_tokens_and_still_media()
    {
        var prices = await FetchAsync(Market);

        var text = prices["venice/uncensored"];
        Assert.Equal(0.775m, text.PriceNow);
        Assert.Equal(1.55m, text.OutputPrice);
        Assert.Equal(0.0775m, text.CachePrice);
        Assert.Equal(ModelPricing.TokenUnit, text.Unit);
        Assert.Equal(38m, text.DiscountPct);
        Assert.Equal(25m, text.MarketOffers);
        Assert.Null(text.PerResult);  // cena za tokeny nie jest ceną wyniku

        var image = prices["venice/z-image-turbo"];
        Assert.Equal(0.004m, image.PriceNow);
        Assert.Equal("image", image.Unit);
        Assert.Null(image.OutputPrice);

        Assert.DoesNotContain("no/price", prices.Keys);
        Assert.Equal(2m, prices["only/input"].PriceNow);
        Assert.Null(prices["only/input"].OutputPrice);
    }

    [Fact]
    public void Token_prices_show_input_and_output_in_the_list_and_the_tooltip()
    {
        var option = new ModelOption("m", "o", false, ["text"],
                                     new ModelPricing(0.775m, null, 38m, ModelPricing.TokenUnit, 25m, 4278m, 1.55m, 0.0775m));

        Assert.Equal("$0,775/$1,55 za 1M", option.PriceText.Replace('.', ','));
        var lines = option.CostText.Replace('.', ',').Split(Environment.NewLine);
        Assert.Equal("Wejście: $0,775 za 1M tokenów", lines[0]);
        Assert.Equal("Wyjście: $1,55 za 1M tokenów", lines[1]);
        Assert.Equal("Odczyt z cache: $0,0775 za 1M tokenów", lines[2]);
        Assert.Contains("Rabat: 38%", lines);
    }

    [Fact]
    public void Chat_models_get_their_price_from_the_catalog_pricing()
    {
        Sta.Run(() =>
        {
            using var app = TestApp.Create();
            app.Provider.ChatCatalog = [new ChatModelInfo("venice/uncensored", []), new ChatModelInfo("brak/ceny", [])];
            app.Provider.PricingCatalog = new Dictionary<string, ModelPricing>
            {
                ["venice/uncensored"] = new(0.775m, null, null, ModelPricing.TokenUnit, null, null, 1.55m),
            };

            app.Vm.InitializeAsync().GetAwaiter().GetResult();

            var options = app.Vm.ChatModelOptions.ToDictionary(o => o.Id);
            Assert.Equal(0.775m, options["venice/uncensored"].Pricing!.PriceNow);
            Assert.Null(options["brak/ceny"].Pricing);
            Assert.Equal("—", options["brak/ceny"].PriceText);
        });
    }
}
