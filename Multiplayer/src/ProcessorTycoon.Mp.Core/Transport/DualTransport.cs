using System;
using System.Collections.Generic;

namespace ProcessorTycoonMp.Core.Transport;

// A host on its main transport (Steam) that also accepts players on this PC (D64): a second game copy here, such as an
// agent's companion, cannot join through Steam with the same account, so it connects to a loopback-only TCP listener.
// The first free address of localAddresses is used (LocalAddress; empty if none was free, and the main transport works
// alone). Peers of the local transport get ids from LocalOffset up. Clients (Connect) use only the main transport.
public sealed class DualTransport : ITransport
{
    public const int LocalOffset = 1_000_000;
    private readonly ITransport main;
    private readonly Func<ITransport> newLocal;
    private readonly IReadOnlyList<string> localAddresses;
    private ITransport? local;

    public DualTransport(ITransport main, Func<ITransport> newLocal, IReadOnlyList<string> localAddresses)
    {
        this.main = main;
        this.newLocal = newLocal;
        this.localAddresses = localAddresses;
    }

    public string LocalAddress { get; private set; } = "";
    public string LocalError { get; private set; } = "";

    public void Host(string address)
    {
        main.Host(address);
        foreach (var candidate in localAddresses)
        {
            var transport = newLocal();
            try { transport.Host(candidate); local = transport; LocalAddress = candidate; LocalError = ""; return; }
            catch (Exception e) { transport.Dispose(); LocalError = e.Message; }
        }
    }

    public void Connect(string address) => main.Connect(address);

    public void Send(int peer, byte[] message)
    {
        if (peer >= LocalOffset) local?.Send(peer - LocalOffset, message);
        else main.Send(peer, message);
    }

    public void Disconnect(int peer, string reason)
    {
        if (peer >= LocalOffset) local?.Disconnect(peer - LocalOffset, reason);
        else main.Disconnect(peer, reason);
    }

    public bool Poll(out NetEvent e)
    {
        if (main.Poll(out e)) return true;
        if (local != null && local.Poll(out var l)) { e = new NetEvent(l.Kind, l.Peer + LocalOffset, l.Data, l.Reason); return true; }
        return false;
    }

    public void Dispose()
    {
        main.Dispose();
        local?.Dispose();
    }
}
