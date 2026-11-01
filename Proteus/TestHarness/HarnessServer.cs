using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Proteus.TestHarness;

/// <summary>
/// The harness's listener: IPv4 loopback only, an ephemeral port, one request per connection, at most
/// <see cref="MaxConnections"/> at once, a read timeout per request. Socket IO and parsing stay on its own tasks; only
/// a route's game-touching part reaches the framework thread. A bad client never stops the accept loop.
/// </summary>
internal sealed class HarnessServer : IDisposable
{
    internal const int MaxConnections = 4;

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Func<HarnessRequest, Task<HarnessResponse>> handle;
    private readonly string token;
    private readonly TimeSpan readTimeout;
    private readonly SemaphoreSlim connections = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;

    public HarnessServer(Func<HarnessRequest, Task<HarnessResponse>> handle, string token, TimeSpan? readTimeout = null)
    {
        this.handle = handle;
        this.token = token;
        this.readTimeout = readTimeout ?? TimeSpan.FromSeconds(5);
    }

    public int Port { get; private set; }

    public IPEndPoint LocalEndpoint => (IPEndPoint)listener.LocalEndpoint;

    public void Start()
    {
        listener.Start();
        Port = LocalEndpoint.Port;
        loop = Task.Factory.StartNew(Accept, TaskCreationOptions.LongRunning).Unwrap();
    }

    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            if (!connections.Wait(0))
            {
                _ = RefuseBusy(client);
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await Serve(client).ConfigureAwait(false); }
                finally { connections.Release(); }
            });
        }
    }

    private static async Task RefuseBusy(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await HarnessHttp.WriteAsync(client.GetStream(),
                                             HarnessResponse.Error(503, "busy", "too many connections; retry"), cts.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* the client went away */ }
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                HarnessHttp.ParseResult parsed;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    read.CancelAfter(readTimeout);
                    try { parsed = await HarnessHttp.ReadAsync(stream, read.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }   // stalled client: closed
                }
                if (parsed.Request == null && parsed.Error == null) return;
                HarnessResponse response;
                if (parsed.Error != null) response = parsed.Error;
                else
                    response = HarnessAuth.Check(parsed.Request!, client.Client.RemoteEndPoint as IPEndPoint, Port, token)
                               ?? await handle(parsed.Request!).ConfigureAwait(false);
                using var write = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await HarnessHttp.WriteAsync(stream, response, write.Token).ConfigureAwait(false);
            }
            catch (Exception) { /* IO failure or client gone: nothing to answer */ }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listener.Stop(); } catch { /* already stopped */ }
        // Never waited on: Dispose may run on the framework thread, and the loop ends on its own once stopped.
        _ = loop?.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }
}
