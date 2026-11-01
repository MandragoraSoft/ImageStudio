using System.IO;
using System.Windows.Media;
using NAudio.Wave;

namespace ImageStudio.Services;

public sealed record AudioDevice(int Index, string Name);

// Nagrywanie z mikrofonu do pliku WAV (16 kHz, mono, 16 bitów: format, który przyjmują modele rozpoznawania mowy).
// Za interfejsem, żeby ViewModel nie zależał od NAudio i dało się go testować bez sprzętu
public interface IAudioRecorder
{
    IReadOnlyList<AudioDevice> Devices();

    // Poziom głośności 0..1 (szczyt ostatniego bufora); wywoływane na dowolnym wątku
    event Action<double>? LevelChanged;

    // Rzuca wyjątek, gdy urządzenie nie działa (brak mikrofonu, zablokowany dostęp w ustawieniach prywatności)
    void Start(int deviceIndex, string path);

    // Kończy nagranie i czeka, aż plik zostanie domknięty
    Task StopAsync();
}

// Odtwarzanie pliku dźwiękowego; jedno naraz (nowe odtwarzanie zatrzymuje poprzednie)
public interface IAudioPlayer
{
    void Play(string path, Action? ended = null);
    void Stop();
}

// WaveIn (winmm): najstarsze API Windows, działa z każdym sterownikiem. Zdarzenia wracają na wątek UI,
// na którym utworzono nagrywarkę, więc StopAsync nie może blokować wątku
public sealed class NAudioRecorder : IAudioRecorder
{
    private WaveIn? _input;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _stopped;

    public event Action<double>? LevelChanged;

    public IReadOnlyList<AudioDevice> Devices() =>
        Enumerable.Range(0, WaveIn.DeviceCount).Select(i => new AudioDevice(i, WaveIn.GetCapabilities(i).ProductName)).ToList();

    public void Start(int deviceIndex, string path)
    {
        if (_input is not null) throw new InvalidOperationException("Nagrywanie już trwa.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var format = new WaveFormat(16000, 16, 1);
        var input = new WaveIn { DeviceNumber = deviceIndex, WaveFormat = format, BufferMilliseconds = 100 };
        var writer = new WaveFileWriter(path, format);
        input.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            var peak = 0;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(e.Buffer, i)));
            LevelChanged?.Invoke(Math.Min(1.0, peak / 32768.0));
        };
        input.RecordingStopped += (_, _) =>
        {
            writer.Dispose();
            input.Dispose();
            _writer = null;
            _input = null;
            _stopped?.TrySetResult();
        };

        try
        {
            input.StartRecording();
        }
        catch
        {
            writer.Dispose();
            input.Dispose();
            throw;
        }

        (_input, _writer) = (input, writer);
    }

    public Task StopAsync()
    {
        if (_input is null) return Task.CompletedTask;

        _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _input.StopRecording();
        return Task.WhenAny(_stopped.Task, Task.Delay(TimeSpan.FromSeconds(3)));
    }
}

public sealed class WpfAudioPlayer : IAudioPlayer
{
    private MediaPlayer? _player;
    private Action? _ended;

    public void Play(string path, Action? ended = null)
    {
        Stop();
        _player ??= CreatePlayer();
        _ended = ended;
        _player.Open(new Uri(path));
        _player.Play();
    }

    public void Stop()
    {
        _player?.Stop();
        _player?.Close();
        _ended = null;
    }

    private MediaPlayer CreatePlayer()
    {
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) => Finish();
        player.MediaFailed += (_, _) => Finish();
        return player;
    }

    private void Finish()
    {
        var ended = _ended;
        Stop();
        ended?.Invoke();
    }
}
