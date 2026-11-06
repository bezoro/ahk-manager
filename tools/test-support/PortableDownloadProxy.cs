using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AhkManager.TestSupport;

public sealed class PortableDownloadProxy : IDisposable
{
    public const long HoldAfterBytes = 8 * 1024 * 1024;
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentQueue<Tunnel> tunnels = new();
    private readonly Task run;
    private volatile bool hold;

    public PortableDownloadProxy()
    {
        listener.Start(8);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        run = RunAsync();
    }

    public int Port { get; }
    public bool Hold { get => hold; set => hold = value; }
    public Tunnel[] Tunnels => tunnels.ToArray();

    public sealed class Tunnel
    {
        internal Tunnel(int id, string host) { Id = id; Host = host; }

        internal long bytes;
        internal volatile string heldAtUtc = "";
        internal volatile string clientClosedAtUtc = "";

        public int Id { get; }
        public string Host { get; }
        public long DownstreamBytes => Interlocked.Read(ref bytes);
        public string HeldAtUtc => heldAtUtc;
        public string ClientClosedAtUtc => clientClosedAtUtc;
    }

    private async Task RunAsync()
    {
        var connections = new List<Task>();
        try
        {
            // Bound the total fixture workload even if a client repeatedly reconnects.
            while (connections.Count < 64)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                connections.Add(HandleAsync(client, connections.Count + 1));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(connections); }
    }

    private async Task HandleAsync(TcpClient client, int id)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(10));
            var token = lifetime.Token;
            var browser = client.GetStream();
            try
            {
                var host = await ReadHostAsync(browser, token);
                if (host == null)
                {
                    await browser.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), token);
                    return;
                }

                await upstream.ConnectAsync(host, 443, token);
                await browser.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
                lifetime.CancelAfter(TimeSpan.FromMinutes(7));
                var tunnel = new Tunnel(id, host);
                tunnels.Enqueue(tunnel);
                var server = upstream.GetStream();
                var browserRead = CopyFromBrowserAsync(browser, server, token);
                var serverRead = CopyFromServerAsync(server, browser, tunnel, token);
                var first = await Task.WhenAny(browserRead, serverRead);
                lifetime.Cancel();
                // Record a client EOF/reset only when it ended the tunnel independently.
                if (first == browserRead && browserRead.IsCompletedSuccessfully)
                {
                    client.Dispose();
                    upstream.Dispose();
                    tunnel.clientClosedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                }
                await Task.WhenAll(browserRead, serverRead);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task<string> ReadHostAsync(NetworkStream browser, CancellationToken token)
    {
        // Avoid overreading the first TLS record and never interpret tunneled bytes.
        var header = new byte[8192];
        for (int length = 0; length < header.Length; length++)
        {
            if (await browser.ReadAsync(header.AsMemory(length, 1), token) == 0) return null;
            if (length < 3 || header[length - 3] != '\r' || header[length - 2] != '\n' || header[length - 1] != '\r' || header[length] != '\n') continue;
            var text = Encoding.ASCII.GetString(header, 0, length + 1);
            var request = text.Substring(0, text.IndexOf("\r\n", StringComparison.Ordinal)).Split(' ');
            if (request.Length != 3 || request[0] != "CONNECT" || request[2] != "HTTP/1.1") return null;
            return request[1] switch
            {
                "api.github.com:443" => "api.github.com",
                "release-assets.githubusercontent.com:443" => "release-assets.githubusercontent.com",
                "objects.githubusercontent.com:443" => "objects.githubusercontent.com",
                _ => null,
            };
        }
        return null;
    }

    private static async Task CopyFromBrowserAsync(NetworkStream browser, NetworkStream server, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (true)
        {
            int read;
            try { read = await browser.ReadAsync(buffer, token); }
            catch (IOException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionReset) { return; }
            if (read == 0) return;
            await server.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private async Task CopyFromServerAsync(NetworkStream server, NetworkStream browser, Tunnel tunnel, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (true)
        {
            var limit = buffer.Length;
            if (hold && tunnel.Host != "api.github.com")
            {
                var remaining = HoldAfterBytes - tunnel.DownstreamBytes;
                if (remaining <= 0)
                {
                    if (tunnel.heldAtUtc.Length == 0) tunnel.heldAtUtc = DateTimeOffset.UtcNow.ToString("O");
                    await Task.Delay(25, token);
                    continue;
                }
                limit = (int)Math.Min(limit, remaining);
            }
            var read = await server.ReadAsync(buffer.AsMemory(0, limit), token);
            if (read == 0) return;
            await browser.WriteAsync(buffer.AsMemory(0, read), token);
            Interlocked.Add(ref tunnel.bytes, read);
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { run.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        finally { listener.Stop(); stop.Dispose(); }
    }
}
