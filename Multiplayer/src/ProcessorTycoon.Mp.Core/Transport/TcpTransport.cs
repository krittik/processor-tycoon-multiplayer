using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ProcessorTycoonMp.Core.Transport;

// Development / LAN / direct-IP transport (D13): 4-byte little-endian length prefix per message, one reader and
// one writer thread per connection.
public sealed class TcpTransport : ITransport
{
    public const int DefaultPort = 27960;
    private const int MaxMessage = 64 * 1024 * 1024;

    private readonly ConcurrentQueue<NetEvent> events = new();
    private readonly ConcurrentDictionary<int, Connection> connections = new();
    private TcpListener? listener;
    private int nextId;
    private volatile bool disposed;

    public static (string host, int port) Parse(string address, string defaultHost)
    {
        address = address.Trim();
        if (address.Length == 0) return (defaultHost, DefaultPort);
        int colon = address.LastIndexOf(':');
        if (colon < 0) return int.TryParse(address, out int p) ? (defaultHost, p) : (address, DefaultPort);
        string host = address.Substring(0, colon);
        return (host.Length == 0 ? defaultHost : host, int.Parse(address.Substring(colon + 1)));
    }

    public void Host(string address)
    {
        var (host, port) = Parse(address, "0.0.0.0");
        listener = new TcpListener(IPAddress.Parse(host == "*" ? "0.0.0.0" : host), port);
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "mp-tcp-accept" }.Start();
    }

    public void Connect(string address)
    {
        var (host, port) = Parse(address, "127.0.0.1");
        new Thread(() =>
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                client.Connect(host, port);
                Start(0, client);
            }
            catch (Exception e) { events.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, reason: e.Message)); }
        }) { IsBackground = true, Name = "mp-tcp-connect" }.Start();
    }

    public void Send(int peer, byte[] message)
    {
        if (connections.TryGetValue(peer, out var c)) c.Outgoing.Add(message);
    }

    public void Disconnect(int peer, string reason)
    {
        if (connections.TryGetValue(peer, out var c)) c.Close(reason, graceful: true);
    }

    public bool Poll(out NetEvent e) => events.TryDequeue(out e);

    public void Dispose()
    {
        disposed = true;
        try { listener?.Stop(); } catch { }
        foreach (var c in connections.Values) c.Close("transport closed", graceful: true);
    }

    private void AcceptLoop()
    {
        while (!disposed)
        {
            try
            {
                var client = listener!.AcceptTcpClient();
                client.NoDelay = true;
                Start(Interlocked.Increment(ref nextId), client);
            }
            catch when (disposed) { return; }
            catch (Exception) { Thread.Sleep(100); }
        }
    }

    private void Start(int id, TcpClient client)
    {
        var c = new Connection(this, id, client);
        connections[id] = c;
        events.Enqueue(new NetEvent(NetEventKind.Connected, id));
        new Thread(c.ReadLoop) { IsBackground = true, Name = $"mp-tcp-read-{id}" }.Start();
        new Thread(c.WriteLoop) { IsBackground = true, Name = $"mp-tcp-write-{id}" }.Start();
    }

    private sealed class Connection
    {
        private readonly TcpTransport owner;
        private readonly int id;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private int closed;
        public readonly BlockingCollection<byte[]> Outgoing = new();

        public Connection(TcpTransport owner, int id, TcpClient client)
        {
            this.owner = owner; this.id = id; this.client = client;
            stream = client.GetStream();
        }

        public void ReadLoop()
        {
            var header = new byte[4];
            try
            {
                while (true)
                {
                    ReadExactly(header);
                    int length = BitConverter.ToInt32(header, 0);
                    if (length < 0 || length > MaxMessage) throw new IOException($"bad message length {length}");
                    var data = new byte[length];
                    ReadExactly(data);
                    owner.events.Enqueue(new NetEvent(NetEventKind.Data, id, data));
                }
            }
            catch (Exception e) { Close(e is EndOfStreamException ? "connection closed" : e.Message, graceful: false); }
        }

        public void WriteLoop()
        {
            try
            {
                foreach (var message in Outgoing.GetConsumingEnumerable())
                {
                    stream.Write(BitConverter.GetBytes(message.Length), 0, 4);
                    stream.Write(message, 0, message.Length);
                }
            }
            catch (Exception e) { Close(e.Message, graceful: false); }
            finally { try { client.Close(); } catch { } }
        }

        private void ReadExactly(byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        // Graceful close lets queued messages (e.g. Reject, Leave) go out first.
        public void Close(string reason, bool graceful)
        {
            if (Interlocked.Exchange(ref closed, 1) == 1) return;
            owner.connections.TryRemove(id, out _);
            Outgoing.CompleteAdding();
            if (!graceful) { try { client.Close(); } catch { } }
            owner.events.Enqueue(new NetEvent(NetEventKind.Disconnected, id, reason: reason));
        }
    }
}
