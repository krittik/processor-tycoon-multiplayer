using System;

namespace ProcessorTycoonMp.Core.Transport;

public enum NetEventKind { Connected, Disconnected, Data }

public readonly struct NetEvent
{
    public NetEvent(NetEventKind kind, int peer, byte[]? data = null, string reason = "")
    {
        Kind = kind; Peer = peer; Data = data; Reason = reason;
    }
    public NetEventKind Kind { get; }
    public int Peer { get; }          // on a client the host is always peer 0
    public byte[]? Data { get; }
    public string Reason { get; }
}

// Reliable, ordered, message-oriented connections (docs/PROTOCOL.md "Transport contract").
// Network I/O may run on background threads; events are queued and consumed with Poll on the main thread.
public interface ITransport : IDisposable
{
    void Host(string address);
    void Connect(string address);
    void Send(int peer, byte[] message);
    void Disconnect(int peer, string reason);
    bool Poll(out NetEvent e);
}
