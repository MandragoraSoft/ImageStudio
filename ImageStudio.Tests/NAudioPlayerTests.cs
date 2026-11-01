using ImageStudio.Services;

namespace ImageStudio.Tests;

// Prawdziwy odtwarzacz bez sprzętu: pliki, których nie da się odtworzyć, kończą się komunikatem, a nie ciszą
public sealed class NAudioPlayerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "is-player-" + Guid.NewGuid().ToString("N"));

    public NAudioPlayerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void A_file_that_is_not_audio_reports_the_reason_and_ends()
    {
        var path = Path.Combine(_dir, "to-nie-audio.mp3");
        File.WriteAllText(path, "to nie jest dźwięk");
        var player = new NAudioPlayer();
        string? reason = null;
        var ended = false;

        player.Play(path, () => ended = true, r => reason = r);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.True(ended);
    }

    [Fact]
    public void A_missing_file_reports_the_reason()
    {
        var player = new NAudioPlayer();
        string? reason = null;

        player.Play(Path.Combine(_dir, "brak.mp3"), failed: r => reason = r);

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Output_list_starts_with_the_system_default_and_stop_without_playing_is_harmless()
    {
        var player = new NAudioPlayer();

        Assert.Equal(new AudioDevice(-1, "Domyślne urządzenie systemu"), player.OutputDevices()[0]);
        Assert.Equal(-1, player.OutputDevice);
        player.Stop();
    }
}
