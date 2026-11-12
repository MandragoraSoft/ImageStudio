using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ImageStudio.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ActivateCommand = "ACTIVATE";
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;
    private NamedPipeServerStream? _server;
    private Task? _serverTask;
    private Action<Action>? _dispatch;
    private Action? _activate;
    private bool _ready;
    private bool _pendingActivation;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
                  ?? throw new InvalidOperationException("Nie można ustalić identyfikatora bieżącego użytkownika.");
        var names = CreateNames(sid, Process.GetCurrentProcess().SessionId);
        _mutexName = names.MutexName;
        _pipeName = names.PipeName;
    }

    internal SingleInstanceCoordinator(string uniqueName)
    {
        _mutexName = $@"Local\{uniqueName}";
        _pipeName = uniqueName;
    }

    internal static (string MutexName, string PipeName) CreateNames(string userSid, int sessionId)
    {
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..24];
        return ($@"Local\ImageStudio-{suffix}", $"ImageStudio-{suffix}-{sessionId}");
    }

    public bool AcquireOrActivate(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mutex = new Mutex(initiallyOwned: false, _mutexName, out _);
        _ownsMutex = TryTakeMutex();
        if (_ownsMutex)
        {
            StartServer();
            return true;
        }

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            try
            {
                if (RequestActivation()) return false;
            }
            catch (IOException)
            {
                // Serwer może być dopiero tworzony po objęciu mutexu.
            }
            catch (TimeoutException)
            {
                // Pierwsza instancja może jeszcze uruchamiać serwer; ponawiamy i sprawdzamy,
                // czy nie zakończyła się w międzyczasie.
            }

            if (TryTakeMutex())
            {
                _ownsMutex = true;
                StartServer();
                return true;
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("Nie udało się aktywować działającej instancji aplikacji.");
    }

    public void SetActivationHandler(Action<Action> dispatch, Action activate)
    {
        lock (_gate)
        {
            _dispatch = dispatch;
            _activate = activate;
        }
    }

    public void MarkWindowReady()
    {
        lock (_gate)
        {
            _ready = true;
            if (_pendingActivation)
            {
                _pendingActivation = false;
                DispatchActivation();
            }
        }
    }

    private bool TryTakeMutex()
    {
        try
        {
            return _mutex!.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private bool RequestActivation()
    {
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(350);
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        writer.WriteLine(ActivateCommand);
        var response = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        return string.Equals(response, "OK", StringComparison.Ordinal);
    }

    private void StartServer()
    {
        var firstServer = CreateServer();
        _server = firstServer;
        _serverTask = Task.Run(() => ServeAsync(firstServer, _stop.Token));
    }

    private NamedPipeServerStream CreateServer() =>
        new(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ServeAsync(NamedPipeServerStream firstServer, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? server = firstServer;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                server ??= CreateServer();
                lock (_gate) _server = server;
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using (server)
                using (var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
                using (var writer = new StreamWriter(server, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { AutoFlush = true })
                {
                    if (string.Equals(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false),
                                      ActivateCommand, StringComparison.Ordinal))
                    {
                        RequestWindowActivation();
                        await writer.WriteLineAsync("OK").ConfigureAwait(false);
                    }
                    else
                    {
                        await writer.WriteLineAsync("ERROR").ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                server?.Dispose();
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                server?.Dispose();
                server = null;
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested) return;
            server = null;
        }
    }

    private void RequestWindowActivation()
    {
        lock (_gate)
        {
            if (!_ready || _dispatch is null || _activate is null)
            {
                _pendingActivation = true;
                return;
            }
            DispatchActivation();
        }
    }

    private void DispatchActivation() => _dispatch?.Invoke(_activate!);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        lock (_gate) _server?.Dispose();
        try
        {
            _serverTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            _stop.Dispose();
        }
    }
}
