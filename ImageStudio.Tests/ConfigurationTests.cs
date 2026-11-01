using ImageStudio.Configuration;

namespace ImageStudio.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "imagestudio-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // --- gradacja sekretów: zmienna środowiskowa > SecretStore > pusty ---

    [Fact]
    public void Secret_prefers_environment_variable()
    {
        const string name = "IMAGESTUDIO_TEST_SECRET_ENV";
        Environment.SetEnvironmentVariable(name, "z-env");
        try
        {
            Assert.Equal("z-env", AppSettings.Secret(name, "imagestudio-test-none"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Secret_treats_empty_environment_variable_as_unset()
    {
        const string name = "IMAGESTUDIO_TEST_SECRET_EMPTY";
        Environment.SetEnvironmentVariable(name, "");
        try
        {
            Assert.Equal("", AppSettings.Secret(name, "imagestudio-test-none"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Secret_is_empty_when_nothing_is_configured()
    {
        Assert.Equal("", AppSettings.Secret("IMAGESTUDIO_TEST_SECRET_MISSING", "imagestudio-test-none"));
    }

    // --- preferencje modeli ---

    [Fact]
    public void Preferences_load_from_missing_file_is_empty()
    {
        var prefs = ModelPreferences.Load(Path.Combine(_dir, "nie-ma.json"));

        Assert.Empty(prefs.Providers);
    }

    [Fact]
    public void Preferences_round_trip_per_provider()
    {
        var path = Path.Combine(_dir, "models.json");
        var prefs = ModelPreferences.Load(path);
        prefs.For("uberllm").PreferredOnly = true;
        prefs.For("uberllm").Preferred.Add("venice/z-image-turbo");
        prefs.For("openrouter").Preferred.Add("google/gemini-nano-banana-2.1");
        prefs.Save();

        var loaded = ModelPreferences.Load(path);

        Assert.True(loaded.For("uberllm").PreferredOnly);
        Assert.Equal(["venice/z-image-turbo"], loaded.For("uberllm").Preferred);
        Assert.False(loaded.For("openrouter").PreferredOnly);
        Assert.Equal(["google/gemini-nano-banana-2.1"], loaded.For("openrouter").Preferred);
        Assert.False(loaded.For("openai").PreferredOnly);
    }

    [Fact]
    public void Preferences_corrupt_file_does_not_block_startup()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "models.json");
        File.WriteAllText(path, "{ to nie jest json");

        var prefs = ModelPreferences.Load(path);

        Assert.Empty(prefs.Providers);
    }

    [Fact]
    public void Preferences_do_not_serialize_the_path_override()
    {
        var path = Path.Combine(_dir, "models.json");
        var prefs = ModelPreferences.Load(path);
        prefs.Save();

        Assert.DoesNotContain("PathOverride", File.ReadAllText(path));
    }
}
