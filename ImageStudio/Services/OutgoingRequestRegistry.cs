using System.IO;
using System.Net.Http;
using ImageStudio.Networking;

namespace ImageStudio.Services;

public sealed record OutgoingRequestInfo(Guid Id, string Method, string Host, DateTimeOffset StartedAt);

public sealed class OutgoingRequestRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Operation> _operations = [];
    private readonly Func<HttpMessageHandler>? _innerFactory;

    public OutgoingRequestRegistry() { }
    public OutgoingRequestRegistry(Func<HttpMessageHandler> innerFactory) => _innerFactory = innerFactory;

    public event EventHandler? Changed;

    public HttpMessageHandler CreateHandler(HttpMessageHandler? inner = null) =>
        new OutgoingRequestHandler(this) { InnerHandler = inner ?? _innerFactory?.Invoke() ?? new HttpClientHandler() };

    public IReadOnlyList<OutgoingRequestInfo> ActiveRequests
    {
        get
        {
            lock (_gate)
                return _operations.Values
                    .Select(operation => operation.Info)
                    .OrderBy(info => info.StartedAt)
                    .ToArray();
        }
    }

    public bool Cancel(Guid id)
    {
        Operation? operation;
        lock (_gate)
            _operations.TryGetValue(id, out operation);

        return operation?.Cancel() ?? false;
    }

    internal Operation Register(HttpRequestMessage request, CancellationToken callerToken)
    {
        var method = request.Method.Method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" or "TRACE" or "CONNECT"))
            method = "HTTP";
        var host = request.RequestUri is { IsAbsoluteUri: true } uri ? uri.Host : "";
        var operation = new Operation(this, new OutgoingRequestInfo(Guid.NewGuid(), method, host, DateTimeOffset.UtcNow), callerToken);
        lock (_gate)
            _operations.Add(operation.Info.Id, operation);
        Changed?.Invoke(this, EventArgs.Empty);
        return operation;
    }

    private void Remove(Guid id)
    {
        bool removed;
        lock (_gate)
            removed = _operations.Remove(id);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class Operation
    {
        private readonly OutgoingRequestRegistry _registry;
        private readonly CancellationTokenSource _cancellation;
        private readonly object _gate = new();
        private int _cancellationsInProgress;
        private bool _completed;
        private bool _disposed;
        private int _userCancelled;

        public OutgoingRequestInfo Info { get; }
        public CancellationToken Token { get; }
        public bool UserCancelled => Volatile.Read(ref _userCancelled) != 0;

        public Operation(OutgoingRequestRegistry registry, OutgoingRequestInfo info, CancellationToken callerToken)
        {
            _registry = registry;
            Info = info;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            Token = _cancellation.Token;
        }

        public bool Cancel()
        {
            lock (_gate)
            {
                if (_completed || _disposed)
                    return false;
                _cancellationsInProgress++;
                Volatile.Write(ref _userCancelled, 1);
            }

            try
            {
                _cancellation.Cancel();
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _cancellationsInProgress--;
                    DisposeIfReady();
                }
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                    return;
                _completed = true;
                DisposeIfReady();
            }

            _registry.Remove(Info.Id);
        }

        private void DisposeIfReady()
        {
            if (!_completed || _cancellationsInProgress != 0 || _disposed)
                return;
            _disposed = true;
            _cancellation.Dispose();
        }
    }
}

public sealed class OutgoingRequestHandler(OutgoingRequestRegistry registry) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var operation = registry.Register(request, cancellationToken);
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, operation.Token).ConfigureAwait(false);
            if (response.Content is { } content)
                response.Content = new TrackedContent(content, operation);
            else
                operation.Complete();
            return response;
        }
        catch (Exception ex)
        {
            try
            {
                response?.Dispose();
            }
            finally
            {
                operation.Complete();
            }
            if (ex is OperationCanceledException && operation.UserCancelled)
                throw new UserCanceledRequestException(ex, operation.Token);
            throw;
        }
    }

    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent _content;
        private readonly OutgoingRequestRegistry.Operation _operation;

        public TrackedContent(HttpContent content, OutgoingRequestRegistry.Operation operation)
        {
            _content = content;
            _operation = operation;
            foreach (var header in content.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            try
            {
                await _content.CopyToAsync(stream, context, _operation.Token).ConfigureAwait(false);
                _operation.Complete();
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            System.Net.TransportContext? context,
            CancellationToken cancellationToken)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token, cancellationToken);
                await _content.CopyToAsync(stream, context, linked.Token).ConfigureAwait(false);
                _operation.Complete();
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            try
            {
                return new TrackedStream(await _content.ReadAsStreamAsync(_operation.Token).ConfigureAwait(false), _operation);
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token, cancellationToken);
                return new TrackedStream(await _content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false), _operation);
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            if (_content.Headers.ContentLength is long contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                    _content.Dispose();
            }
            finally
            {
                try
                {
                    _operation.Complete();
                }
                finally
                {
                    base.Dispose(disposing);
                }
            }
        }
    }

    private sealed class TrackedStream : Stream
    {
        private readonly Stream _inner;
        private readonly OutgoingRequestRegistry.Operation _operation;
        private bool _disposed;

        public TrackedStream(Stream inner, OutgoingRequestRegistry.Operation operation)
        {
            _inner = inner;
            _operation = operation;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => Track(() => _inner.Read(buffer, offset, count), value => value == 0 && count > 0);
        public override int Read(Span<byte> buffer)
        {
            try
            {
                var result = _inner.Read(buffer);
                if (result == 0 && buffer.Length > 0)
                    _operation.Complete();
                return result;
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }
        public override int ReadByte() => Track(_inner.ReadByte, value => value == -1);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            TrackReadAsyncTask(token => _inner.ReadAsync(buffer, offset, count, token), cancellationToken, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            TrackReadAsyncValue(token => _inner.ReadAsync(buffer, token), cancellationToken, buffer.Length);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _operation.Complete();
                }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                try
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    _operation.Complete();
                }
            }
            GC.SuppressFinalize(this);
        }

        private int Track(Func<int> read, Func<int, bool> isEnd)
        {
            try
            {
                var result = read();
                if (isEnd(result))
                    _operation.Complete();
                return result;
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }

        private async Task<int> TrackReadAsyncTask(Func<CancellationToken, Task<int>> read, CancellationToken callerToken, int count)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token, callerToken);
            try
            {
                var result = await read(linked.Token).ConfigureAwait(false);
                if (result == 0 && count > 0)
                    _operation.Complete();
                return result;
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }

        private async ValueTask<int> TrackReadAsyncValue(Func<CancellationToken, ValueTask<int>> read, CancellationToken callerToken, int count)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_operation.Token, callerToken);
            try
            {
                var result = await read(linked.Token).ConfigureAwait(false);
                if (result == 0 && count > 0)
                    _operation.Complete();
                return result;
            }
            catch (Exception ex)
            {
                _operation.Complete();
                if (ex is OperationCanceledException && _operation.UserCancelled)
                    throw new UserCanceledRequestException(ex, _operation.Token);
                throw;
            }
        }
    }
}
