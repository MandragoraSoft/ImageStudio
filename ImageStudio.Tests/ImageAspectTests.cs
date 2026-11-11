using ImageStudio.Services;

namespace ImageStudio.Tests;

public sealed class ImageAspectTests
{
    [Fact]
    public void Ratio_parses_label()
    {
        Assert.Equal(16.0 / 9, ImageAspect.Ratio("16:9")!.Value, 4);
    }

    [Fact]
    public void Ratio_of_invalid_label_is_null()
    {
        Assert.Null(ImageAspect.Ratio("abc"));
        Assert.Null(ImageAspect.Ratio("4:0"));
        Assert.Null(ImageAspect.Ratio(null));
    }

    [Fact]
    public void Label_for_size_matches_preset_ratio()
    {
        Assert.Equal("1:1", ImageAspect.LabelFor(1024, 1024));
        Assert.Equal("16:9", ImageAspect.LabelFor(1280, 720));
        // 1344x768 (1,75) jest poza tolerancją 0,02 od 16:9 także w dotychczasowym kodzie: bez etykiety
        Assert.Null(ImageAspect.LabelFor(1344, 768));
    }

    [Fact]
    public void Label_for_custom_size_is_null()
    {
        Assert.Null(ImageAspect.LabelFor(1000, 333));
        Assert.Null(ImageAspect.LabelFor(1024, 0));
    }

    [Fact]
    public void Snap_rounds_to_multiple_of_eight_with_minimum_eight()
    {
        // Wartości zmierzone na dotychczasowej implementacji MainViewModel.Snap
        Assert.Equal(104, ImageAspect.Snap(100.3));
        Assert.Equal(8, ImageAspect.Snap(3));
        Assert.Equal(1024, ImageAspect.Snap(1024));
    }

    [Fact]
    public void Lists_have_expected_values()
    {
        Assert.Equal(["1:1", "4:3", "3:2", "16:9", "9:16"], StudioDefaults.ImageAspects);
        Assert.Equal(["auto", "low", "medium", "high"], StudioDefaults.Qualities);
    }
}
