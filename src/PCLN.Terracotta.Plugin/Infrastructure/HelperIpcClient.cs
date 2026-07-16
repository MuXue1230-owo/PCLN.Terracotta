using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Cn.Pcln.Terracotta.Contracts;
using PCL.N.Plugin;

namespace Cn.Pcln.Terracotta.Infrastructure;

/// <summary>
/// Duplex IPC client: a background reader demultiplexes request responses and unsolicited push events.
/// </summary>
public sealed class HelperIpcClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly Channel<HelperPushEvent> _events = Channel.CreateBounded<HelperPushEvent>(
        new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true
        });
    private readonly CancellationTokenSource _readerCts = new();
    private IPluginTaskRegistration? _readerTask;
    private bool _disposed;

    private HelperIpcClient(Stream stream)
    {
        _stream = stream;
    }

    public string? HelperVersion { get; private set; }

    public IReadOnlyList<string> Capabilities { get; private set; } = [];

    public bool SupportsPushEvents =>
        Capabilities.Any(static capability => string.Equals(capability, "events.push", StringComparison.Ordinal));

    /// <summary>Consumes unsolicited Helper push events (peer/network/state).</summary>
    public ChannelReader<HelperPushEvent> Events => _events.Reader;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<TerracottaRoomState>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<TerracottaRoomRole>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<TerracottaConnectionMode>(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static async ValueTask<HelperIpcClient> ConnectAsync(
        string endpoint,
        string authenticationToken,
        string pluginVersion,
        IPluginTaskService tasks,
        string readerTaskName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        Stream stream = await ConnectStreamAsync(endpoint, cancellationToken).ConfigureAwait(false);
        HelperIpcClient client = new(stream);
        try
        {
            client.StartReader(tasks, readerTaskName);
            HelperHelloResponse response = await client.SendAsync<HelperHelloRequest, HelperHelloResponse>(
                HelperMessageTypes.Hello,
                new HelperHelloRequest(authenticationToken, "pcln", pluginVersion),
                HelperMessageTypes.HelloAccepted,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.HelperVersion))
                throw new HelperProtocolException("Helper handshake omitted its version.");
            client.HelperVersion = response.HelperVersion;
            client.Capabilities = response.Capabilities.ToArray();
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void StartReader(IPluginTaskService tasks, string readerTaskName)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (_readerTask is not null)
            return;
        _readerTask = tasks.Run(readerTaskName, async token =>
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _readerCts.Token);
            await ReadLoopAsync(linked.Token).ConfigureAwait(false);
        });
    }

    public async ValueTask<TResponse> SendAsync<TRequest, TResponse>(
        string messageType,
        TRequest request,
        string expectedResponseType,
        CancellationToken cancellationToken = default)
    {
        IpcEnvelope outbound = IpcEnvelope.Create(messageType, request, options: JsonOptions);
        TaskCompletionSource<IpcEnvelope> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(outbound.Id, completion))
            throw new HelperProtocolException("Duplicate IPC request id generation failure.");

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await IpcFraming.WriteAsync(_stream, outbound, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<IpcEnvelope>)state!).TrySetCanceled(),
                completion);
            IpcEnvelope inbound = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (inbound.Protocol != ProtocolVersion.Current)
                throw new HelperProtocolException($"Unsupported Helper protocol version: {inbound.Protocol}.");
            if (string.Equals(inbound.Type, HelperMessageTypes.Error, StringComparison.Ordinal))
            {
                HelperError error = inbound.ReadPayload<HelperError>(JsonOptions);
                throw new HelperProtocolException(error.Code, error.Message);
            }
            if (!string.Equals(inbound.Type, expectedResponseType, StringComparison.Ordinal))
                throw new HelperProtocolException($"Unexpected Helper response type: {inbound.Type}.");

            return inbound.ReadPayload<TResponse>(JsonOptions);
        }
        finally
        {
            _pending.TryRemove(outbound.Id, out _);
        }
    }

    public async ValueTask SendAsync(
        string messageType,
        object request,
        string expectedResponseType,
        CancellationToken cancellationToken = default)
    {
        await SendAsync<object, JsonElement>(messageType, request, expectedResponseType, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _readerCts.Cancel();
        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore reader teardown
            }
        }

        foreach (KeyValuePair<string, TaskCompletionSource<IpcEnvelope>> pair in _pending)
            pair.Value.TrySetCanceled();
        _pending.Clear();
        _events.Writer.TryComplete();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        _readerCts.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IpcEnvelope inbound = await IpcFraming.ReadAsync(_stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (_pending.TryRemove(inbound.Id, out TaskCompletionSource<IpcEnvelope>? completion))
                {
                    completion.TrySetResult(inbound);
                    continue;
                }

                if (IsPushEvent(inbound.Type))
                {
                    _events.Writer.TryWrite(new HelperPushEvent(inbound.Type, inbound));
                    continue;
                }

                _events.Writer.TryWrite(new HelperPushEvent(inbound.Type, inbound));
            }
        }
        catch (OperationCanceledException)
        {
            // dispose
        }
        catch (Exception exception)
        {
            foreach (KeyValuePair<string, TaskCompletionSource<IpcEnvelope>> pair in _pending)
                pair.Value.TrySetException(exception);
            _events.Writer.TryComplete(exception);
        }
    }

    private static bool IsPushEvent(string type) =>
        type is HelperMessageTypes.PeerJoined
            or HelperMessageTypes.PeerLeft
            or HelperMessageTypes.PeerUpdated
            or HelperMessageTypes.NetworkUpdated
            or HelperMessageTypes.RoomStateChanged
            or HelperMessageTypes.RoomFailed
            or HelperMessageTypes.Log
            or HelperMessageTypes.Fatal;

    private static async ValueTask<Stream> ConnectStreamAsync(
        string endpoint,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            const string prefix = @"\\.\pipe\";
            if (!endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Windows IPC endpoint must be a local named pipe.", nameof(endpoint));
            string name = endpoint[prefix.Length..];
            NamedPipeClientStream pipe = new(
                ".",
                name,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Anonymous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

public sealed record HelperPushEvent(string Type, IpcEnvelope Envelope);
