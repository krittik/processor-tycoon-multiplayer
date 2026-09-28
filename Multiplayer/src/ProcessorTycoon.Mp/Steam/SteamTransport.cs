using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ProcessorTycoonMp.Core.Transport;
using Steamworks;

namespace ProcessorTycoonMp.Steam;

// ITransport over Steam Networking Sockets (D50): P2P connections by Steam id through Valve's relay (SDR), so nobody
// forwards ports. Host address "steam"; peer address "steam:<host Steam id>". Development: "steamip:<port>" /
// "steamip:<ip>:<port>" run the same code over Steam's IP sockets, because one Steam account cannot connect to itself
// (TESTING.md). Reliable ordered messages; frames above
// Steam's 512 KiB message limit travel as fragments (Core Fragments). Everything runs on the main thread: status
// callbacks arrive in SteamAPI.RunCallbacks (SteamGate.Update), messages are read in Poll.
internal sealed class SteamTransport : ITransport
{
    private const int MaxMessage = 64 * 1024 * 1024;
    private const int FragmentPayload = 256 * 1024;

    // Closing a listen socket drops its connections without notice, so a disposed host keeps it a few seconds while
    // lingering connections deliver their last messages (Leave) and close gracefully.
    private static readonly List<(HSteamListenSocket socket, DateTime closeAt)> retiring = new();

    private readonly Queue<NetEvent> events = new();
    private readonly Dictionary<uint, Link> links = new();
    private readonly IntPtr[] received = new IntPtr[64];
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> statusChanged;
    private HSteamListenSocket listen = HSteamListenSocket.Invalid;
    private int nextPeer;

    private sealed class Link
    {
        public Link(int peer, HSteamNetConnection connection) { Peer = peer; Connection = connection; }
        public int Peer { get; }
        public HSteamNetConnection Connection { get; }
        public bool Connected;
        public readonly Queue<byte[]> Outgoing = new();
        public readonly FragmentAssembler Assembler = new(MaxMessage);
    }

    public SteamTransport()
    {
        statusChanged = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
    }

    public const string DevIpPrefix = "steamip:";

    public void Host(string address)
    {
        CloseRetired(force: true);
        if (address.StartsWith(DevIpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var local = new SteamNetworkingIPAddr();
            local.Clear();
            local.SetIPv4(0, ushort.Parse(address.Substring(DevIpPrefix.Length)));
            listen = SteamNetworkingSockets.CreateListenSocketIP(ref local, 0, null);
        }
        else listen = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, null);
        if (listen == HSteamListenSocket.Invalid) throw new InvalidOperationException("Steam could not open a listen socket.");
    }

    public void Connect(string address)
    {
        HSteamNetConnection connection;
        if (address.StartsWith(DevIpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remote = new SteamNetworkingIPAddr();
            if (!remote.ParseString(address.Substring(DevIpPrefix.Length))) throw new ArgumentException($"Not an ip:port: '{address}'.");
            connection = SteamNetworkingSockets.ConnectByIPAddress(ref remote, 0, null);
        }
        else
        {
            if (!SteamGate.TryParse(address, out ulong hostId)) throw new ArgumentException($"Not a Steam address: '{address}' (expected steam:<Steam id>).");
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(hostId);
            connection = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
        }
        if (connection == HSteamNetConnection.Invalid) { events.Enqueue(new NetEvent(NetEventKind.Disconnected, 0, reason: "Steam could not start the connection")); return; }
        links[connection.m_HSteamNetConnection] = new Link(0, connection);
    }

    public void Send(int peer, byte[] message)
    {
        var link = Find(peer);
        if (link == null) return;
        foreach (var fragment in Fragments.Split(message, FragmentPayload)) link.Outgoing.Enqueue(fragment);
        if (link.Connected) Flush(link);
    }

    public void Disconnect(int peer, string reason)
    {
        var link = Find(peer);
        if (link == null) return;
        if (link.Connected) Flush(link);
        // Linger delivers what Steam already queued (e.g. Reject, Leave) before closing.
        Close(link, reason, linger: true);
    }

    public bool Poll(out NetEvent e)
    {
        if (events.Count == 0) Pump();
        if (events.Count > 0) { e = events.Dequeue(); return true; }
        e = default;
        return false;
    }

    public void Dispose()
    {
        foreach (var link in links.Values.ToList())
        {
            if (link.Connected) Flush(link);
            SteamNetworkingSockets.CloseConnection(link.Connection, 0, "transport closed", true);
        }
        links.Clear();
        if (listen != HSteamListenSocket.Invalid) retiring.Add((listen, DateTime.UtcNow.AddSeconds(5)));
        listen = HSteamListenSocket.Invalid;
        statusChanged.Dispose();
    }

    // Called every frame (SteamServices.Update), before hosting again and at shutdown (quitting the game as host).
    public static void CloseRetired(bool force)
    {
        if (force && retiring.Count > 0 && retiring.Any(r => DateTime.UtcNow < r.closeAt)) System.Threading.Thread.Sleep(500);
        for (int i = retiring.Count - 1; i >= 0; i--)
        {
            if (!force && DateTime.UtcNow < retiring[i].closeAt) continue;
            SteamNetworkingSockets.CloseListenSocket(retiring[i].socket);
            retiring.RemoveAt(i);
        }
    }

    // How each connection travels: through a Steam relay (and which data centres) or directly, with the ping.
    public List<string> Routes()
    {
        var result = new List<string>();
        foreach (var link in links.Values.Where(l => l.Connected).OrderBy(l => l.Peer))
        {
            if (!SteamNetworkingSockets.GetConnectionInfo(link.Connection, out var info)) continue;
            var status = new SteamNetConnectionRealTimeStatus_t();
            var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
            SteamNetworkingSockets.GetConnectionRealTimeStatus(link.Connection, ref status, 0, ref lanes);
            bool relayed = (info.m_nFlags & Constants.k_nSteamNetworkConnectionInfoFlags_Relayed) != 0;
            info.m_addrRemote.ToString(out string address, true);
            string route = relayed
                ? $"relayed through Steam data centre {Pop(info.m_idPOPRelay)} (remote end near {Pop(info.m_idPOPRemote)})"
                : $"direct to {address}";
            result.Add($"peer {link.Peer}: {route}, ping {status.m_nPing} ms");
        }
        return result;
    }

    // Steam's own multi-line diagnostic text per connection (route candidates, relays, ping, quality).
    public string Detailed()
    {
        var text = new StringBuilder();
        foreach (var link in links.Values.Where(l => l.Connected).OrderBy(l => l.Peer))
            if (SteamNetworkingSockets.GetDetailedConnectionStatus(link.Connection, out string detail, 16384) >= 0)
                text.Append($"--- peer {link.Peer}\n{detail}\n");
        return text.ToString();
    }

    private static string Pop(SteamNetworkingPOPID id)
    {
        uint v = id.m_SteamNetworkingPOPID;
        if (v == 0) return "?";
        var chars = new[] { (char)((v >> 16) & 0xFF), (char)((v >> 8) & 0xFF), (char)(v & 0xFF), (char)((v >> 24) & 0xFF) };
        return new string(chars.Where(c => c != 0).ToArray());
    }

    private Link? Find(int peer) => links.Values.FirstOrDefault(l => l.Peer == peer);

    private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t status)
    {
        uint handle = status.m_hConn.m_HSteamNetConnection;
        links.TryGetValue(handle, out var link);
        switch (status.m_info.m_eState)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                if (link == null && listen != HSteamListenSocket.Invalid && status.m_info.m_hListenSocket == listen)
                {
                    if (SteamNetworkingSockets.AcceptConnection(status.m_hConn) == EResult.k_EResultOK) links[handle] = new Link(++nextPeer, status.m_hConn);
                    else SteamNetworkingSockets.CloseConnection(status.m_hConn, 0, "accept failed", false);
                }
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                if (link != null && !link.Connected)
                {
                    link.Connected = true;
                    events.Enqueue(new NetEvent(NetEventKind.Connected, link.Peer));
                    Flush(link);
                }
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                string reason = status.m_info.m_szEndDebug;
                if (link != null) Close(link, reason.Length > 0 ? reason : "connection closed", linger: false);
                else SteamNetworkingSockets.CloseConnection(status.m_hConn, 0, null, false);
                break;
        }
    }

    // Retries sends Steam refused for a full buffer, then reads everything that arrived.
    private void Pump()
    {
        foreach (var link in links.Values.ToList())
        {
            if (!link.Connected) continue;
            Flush(link);
            if (!links.ContainsKey(link.Connection.m_HSteamNetConnection)) continue;
            int count;
            do
            {
                count = SteamNetworkingSockets.ReceiveMessagesOnConnection(link.Connection, received, received.Length);
                var fragments = new List<byte[]>(Math.Max(count, 0));
                for (int i = 0; i < count; i++)
                {
                    var message = SteamNetworkingMessage_t.FromIntPtr(received[i]);
                    var data = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
                    SteamNetworkingMessage_t.Release(received[i]);
                    fragments.Add(data);
                }
                foreach (var fragment in fragments)
                {
                    byte[]? frame;
                    try { frame = link.Assembler.Add(fragment); }
                    catch (InvalidDataException e) { Close(link, e.Message, linger: false); return; }
                    if (frame != null) events.Enqueue(new NetEvent(NetEventKind.Data, link.Peer, frame));
                }
            } while (count == received.Length);
        }
    }

    private unsafe void Flush(Link link)
    {
        while (link.Outgoing.Count > 0)
        {
            var fragment = link.Outgoing.Peek();
            EResult result;
            fixed (byte* data = fragment)
                result = SteamNetworkingSockets.SendMessageToConnection(link.Connection, (IntPtr)data, (uint)fragment.Length, Constants.k_nSteamNetworkingSend_ReliableNoNagle, out _);
            if (result == EResult.k_EResultOK) { link.Outgoing.Dequeue(); continue; }
            if (result == EResult.k_EResultLimitExceeded) return; // send buffer full: the next Poll retries
            Close(link, "Steam send failed: " + result, linger: false);
            return;
        }
    }

    private void Close(Link link, string reason, bool linger)
    {
        if (!links.Remove(link.Connection.m_HSteamNetConnection)) return;
        SteamNetworkingSockets.CloseConnection(link.Connection, 0, reason.Length > 100 ? reason.Substring(0, 100) : reason, linger);
        // A host never announced a connection that did not complete; a client always hears why its connect failed.
        if (link.Connected || listen == HSteamListenSocket.Invalid) events.Enqueue(new NetEvent(NetEventKind.Disconnected, link.Peer, reason: reason));
    }
}
