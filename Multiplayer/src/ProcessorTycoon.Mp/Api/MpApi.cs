using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.Api;

// Public interop surface (D14). Other mods use it through a soft dependency on GUID "processortycoon.multiplayer"
// (typically by reflection, so they run without this mod). Call from the Unity main thread.
public static class MpApi
{
    internal static MpRuntime? Runtime;

    public static bool IsActive => Runtime?.Session != null;
    public static bool IsHost => Runtime?.Session?.IsHost ?? false;
    public static int LocalSlot => Runtime?.Session?.LocalSlot ?? 0;
    // Only the host controls time during a session (D6).
    public static bool CanControlTime => !IsActive || IsHost;
    // This player's company went bankrupt during the session; the player stays as a spectator (D57).
    public static bool LocalBankrupt => Adapter.Bankruptcy.LocalSpectator;
    public static IReadOnlyList<string> Players => Runtime?.Session?.Players.Select(p => p.Name).ToList() ?? new List<string>();
    public static string State => Runtime?.Session?.State.ToString() ?? (Runtime?.Busy == true ? "Loading" : "None");
    public static string SessionId => Runtime?.Session?.SessionId ?? "";
    public static string LastError => Runtime?.LastError ?? "";

    public static event Action<int, string>? OnChat
    {
        add { if (Runtime != null) Runtime.Chat += value; }
        remove { if (Runtime != null) Runtime.Chat -= value; }
    }

    public static void SendChat(string text) => Runtime?.SendChat(text);

    // The number of the latest chat line (0: none yet). Numbers keep growing across sessions.
    public static int ChatLast => Runtime?.ChatLast ?? 0;

    // The session's chat lines numbered above `since` (at most the last 100), oldest first: seq, from and to (player names;
    // from "" is a note from this mod, such as an unknown @name, to "" means everyone), text, private, mine.
    public static IList<IDictionary<string, object>> ChatSince(int since)
    {
        var s = Runtime?.Session;
        if (s == null) return new List<IDictionary<string, object>>();
        string Name(int slot) => slot < 0 ? "" : s.Players.FirstOrDefault(p => p.Slot == slot)?.Name ?? "?";
        return Runtime!.ChatLines.Where(l => l.Seq > since).Select(l => (IDictionary<string, object>)new Dictionary<string, object>
        {
            ["seq"] = l.Seq, ["from"] = l.From < 0 ? "" : l.Name, ["to"] = Name(l.To), ["text"] = l.Text, ["private"] = l.Private, ["mine"] = l.From == s.LocalSlot,
        }).ToList();
    }

    // Mod data channels: handlers receive (sender slot, bytes) for their channel name; Send reaches every other player.
    private static readonly Dictionary<string, Action<int, byte[]>> channels = new();

    public static void RegisterChannel(string name, Action<int, byte[]> handler) => channels[name] = handler;
    public static void UnregisterChannel(string name) => channels.Remove(name);
    public static void Send(string name, byte[] data) => Runtime?.Session?.SendChannel(name, data);

    internal static void Deliver(string name, int slot, byte[] data)
    {
        if (channels.TryGetValue(name, out var handler))
            try { handler(slot, data); } catch (Exception e) { Runtime?.Log($"MP: channel {name} handler failed: {e.Message}"); }
    }

    // Session control, same as the F9 window. Results are asynchronous: poll State / LastError / Status().
    public static void Host(string address, string playerName) => Runtime?.Host(address, playerName);
    // In a game without a window (-batchmode) nobody can fill in the company setup screen: the company is set up at once
    // with the player's name, as a CPU company.
    public static void Join(string address, string playerName)
    {
        if (Application.isBatchMode) JoinAs(address, playerName, playerName, 0);
        else Runtime?.Join(address, playerName);
    }

    // Joins with the company set up at once instead of on the setup screen: companyType 0 CPU, 1 CPU fabless, 2 foundry;
    // funds, factory and technology as a new game started now (D58).
    public static void JoinAs(string address, string playerName, string companyName, int companyType) =>
        Runtime?.Join(address, playerName, rules => UI.JoinSetup.Auto(rules, companyName.Length > 0 ? companyName : playerName, playerName, companyType));
    public static void Resume(string sessionId, string address, string playerName) => Runtime?.Resume(sessionId, address, playerName);
    public static void Leave() => Runtime?.Leave();

    // Compact status as a flat dictionary (strings, numbers, booleans) for tools such as the Agent mod.
    public static IDictionary<string, object> Status()
    {
        var s = Runtime?.Session;
        var result = new Dictionary<string, object>
        {
            ["installed"] = true,
            ["active"] = s != null,
            ["state"] = State,
            ["host"] = s?.IsHost ?? false,
            ["slot"] = s?.LocalSlot ?? 0,
            ["canControlTime"] = CanControlTime,
            ["bankrupt"] = s != null && LocalBankrupt,
            ["sessionId"] = s?.SessionId ?? "",
            ["players"] = s == null ? "" : string.Join("; ", s.Players.Select(p => p.ToString())),
            ["lastCheckpointDay"] = s?.LastCheckpointDay ?? -1,
            ["resyncs"] = s?.Resyncs ?? 0,
            ["waitingForPlayers"] = s is HostSession h && h.WaitingForPeers,
            ["waitingFor"] = s is HostSession hw ? hw.WaitingFor : "",
            ["resumable"] = string.Join(", ", Runtime?.Resumable().Select(r => r.sessionId) ?? Enumerable.Empty<string>()),
            ["chatLast"] = ChatLast,
            // Where a game copy on this PC joins this host's session (D64).
            ["localAddress"] = Runtime?.LocalAddress ?? "",
            ["error"] = LastError,
        };
        if (s is PeerSession p) { result["hostSpeed"] = p.HostSpeed; result["lastCheckpointResult"] = p.LastCheckpointResult; }
        return result;
    }
}
