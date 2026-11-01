using ImageStudio.Services;
using ImageStudio.ViewModels;

namespace ImageStudio.Tests;

public class ModelTests
{
    [Fact]
    public void Token_price_is_not_a_per_result_price()
    {
        var tokens = new ModelPricing(30m, null, null, ModelPricing.TokenUnit, null, null);
        var perImage = new ModelPricing(0.04m, null, null, "obraz", null, null);
        var noUnit = new ModelPricing(0.04m, null, null, null, null, null);

        Assert.Null(tokens.PerResult);
        Assert.Equal(0.04m, perImage.PerResult);
        Assert.Equal(0.04m, noUnit.PerResult);
    }

    [Fact]
    public void Capabilities_come_from_input_and_output_modalities()
    {
        var all = new ModelOption("m", "o", false, ["text", "image", "file", "audio", "video"], null, null, ["image", "text"]);
        var plain = new ModelOption("m", "o", false, ["text"], null, null, ["image"]);
        var noOutputInfo = new ModelOption("m", "o", false, ["text"], null);

        Assert.True(all.AcceptsImage);
        Assert.True(all.AcceptsFile);
        Assert.True(all.AcceptsAudio);
        Assert.True(all.AcceptsVideo);
        Assert.True(all.ReturnsText);

        Assert.False(plain.AcceptsImage);
        Assert.False(plain.AcceptsFile);
        Assert.False(plain.AcceptsAudio);
        Assert.False(plain.AcceptsVideo);
        Assert.False(plain.ReturnsText);

        Assert.False(noOutputInfo.ReturnsText);
    }

    [Fact]
    public void Price_text_shows_dash_without_pricing_and_unit_with_it()
    {
        var none = new ModelOption("m", "o", false, ["text"], null);
        var perSecond = new ModelOption("m", "o", false, ["text"], new ModelPricing(0.5m, null, null, "s", null, null));
        var noUnit = new ModelOption("m", "o", false, ["text"], new ModelPricing(0.5m, null, null, null, null, null));

        Assert.Equal("—", none.PriceText);
        Assert.Equal($"${0.5m:0.####}/s", perSecond.PriceText);
        Assert.EndsWith("/szt.", noUnit.PriceText);
    }
}
