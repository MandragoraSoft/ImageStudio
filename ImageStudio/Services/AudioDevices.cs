using System.IO;
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
    // ended: koniec pliku albo błąd; failed (opcjonalne): powód, gdy pliku nie da się odtworzyć (brak kodeka, urządzenie)
    void Play(string path, Action? ended = null, Action<string>? failed = null);
    void Stop();

    // Wyjścia dźwięku: pozycja o indeksie -1 to domyślne urządzenie systemu. Komputery z wirtualnymi urządzeniami
    // (Sonar, Steam Streaming) bywają ustawione tak, że domyślne wyjście nic nie słychać, więc wybór jest dla użytkownika
    IReadOnlyList<AudioDevice> OutputDevices();
    int OutputDevice { get; set; }
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
        [new(NAudioPlayer.DefaultDevice, "Domyślne urządzenie systemu"),
         .. Enumerable.Range(0, WaveIn.DeviceCount).Select(i => new AudioDevice(i, WaveIn.GetCapabilities(i).ProductName))];

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

// Odtwarzanie przez NAudio: dekoder Media Foundation (mp3, wav, m4a, aac...) i wyjście winmm z wybranym urządzeniem.
// PlaybackStopped wraca na wątek UI, na którym utworzono odtwarzacz
public sealed class NAudioPlayer : IAudioPlayer
{
    public const int DefaultDevice = -1;

    private WaveOut? _output;
    private MediaFoundationReader? _reader;
    private int _generation;

    public int OutputDevice { get; set; } = DefaultDevice;

    public IReadOnlyList<AudioDevice> OutputDevices() =>
        [new(DefaultDevice, "Domyślne urządzenie systemu"),
         .. Enumerable.Range(0, WaveOut.DeviceCount).Select(i => new AudioDevice(i, WaveOut.GetCapabilities(i).ProductName))];

    public void Play(string path, Action? ended = null, Action<string>? failed = null)
    {
        Stop();
        var generation = _generation;
        try
        {
            var reader = new MediaFoundationReader(path);
            var output = new WaveOut { DeviceNumber = OutputDevice };
            output.PlaybackStopped += (_, e) =>
            {
                if (generation != _generation) return;  // zatrzymane przez nas (Stop albo nowe odtwarzanie)
                Release();
                if (e.Exception is not null) failed?.Invoke(e.Exception.Message);
                ended?.Invoke();
            };
            output.Init(reader);
            (_reader, _output) = (reader, output);
            output.Play();
        }
        catch (Exception ex)  // dekoder i sterownik zgłaszają różne wyjątki (COM, MmException, IO): każdy to "nie da się odtworzyć"
        {
            Release();
            failed?.Invoke(ex.Message);
            ended?.Invoke();
        }
    }

    public void Stop()
    {
        _generation++;
        Release();
    }

    private void Release()
    {
        _output?.Dispose();
        _reader?.Dispose();
        (_output, _reader) = (null, null);
    }
}
