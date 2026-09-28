using System.Collections.Generic;

namespace ProcessorTycoonMp.Core.Transport;

// In-process transport for tests: one hub, one host endpoint, any number of client endpoints. Single-threaded.
public sealed class LoopbackHub
{
    internal LoopbackTransport? HostEnd;
    internal readonly Dictionary<int, LoopbackTransport> Clients = new();
    internal int NextId;
}

public sealed class LoopbackTransport : ITransport
{
    private readonly LoopbackHub hub;
    private readonly Queue<NetEvent> events = new();
    private int idOnHost = -1;   // client side: its id at the host

    public LoopbackTransport(LoopbackHub hub) { this.hub = hub; }

    public void Host(string address) => hub.HostEnd = this;

    public void Connect(string address)
    {
        if (hub.HostEnd == null) { events.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, reason: "no host")); return; }
        idOnHost = ++hub.NextId;
        hub.Clients[idOnHost] = this;
        events.Enqueue(new NetEvent(NetEventKind.Connected, 0));
        hub.HostEnd.events.Enqueue(new NetEvent(NetEventKind.Connected, idOnHost));
    }

    public void Send(int peer, byte[] message)
    {
        if (this == hub.HostEnd)
        {
            if (hub.Clients.TryGetValue(peer, out var c)) c.events.Enqueue(new NetEvent(NetEventKind.Data, 0, message));
        }
        else if (idOnHost > 0 && hub.HostEnd != null && hub.Clients.ContainsKey(idOnHost))
            hub.HostEnd.events.Enqueue(new NetEvent(NetEventKind.Data, idOnHost, message));
    }

    public void Disconnect(int peer, string reason)
    {
        if (this == hub.HostEnd)
        {
            if (!hub.Clients.TryGetValue(peer, out var c)) return;
            hub.Clients.Remove(peer);
            c.events.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, reason: reason));
            events.Enqueue(new NetEvent(NetEventKind.Disconnected, peer, reason: reason));
        }
        else if (idOnHost > 0 && hub.Clients.Remove(idOnHost))
        {
            hub.HostEnd?.events.Enqueue(new NetEvent(NetEventKind.Disconnected, idOnHost, reason: reason));
            events.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, reason: reason));
        }
    }

    public bool Poll(out NetEvent e)
    {
        if (events.Count > 0) { e = events.Dequeue(); return true; }
        e = default;
        return false;
    }

    public void Dispose()
    {
        if (this == hub.HostEnd)
            foreach (var id in new List<int>(hub.Clients.Keys)) Disconnect(id, "host closed");
        else Disconnect(0, "closed");
    }
}
