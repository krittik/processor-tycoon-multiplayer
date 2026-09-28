using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ProcessorTycoonMp.Core.Transport;
using UnityEngine;

namespace ProcessorTycoonMp.Steam;

public readonly struct HostingFriend
{
    public HostingFriend(ulong steamId, string name, string version) { SteamId = steamId; Name = name; Version = version; }
    public ulong SteamId { get; }
    public string Name { get; }
    public string Version { get; }
}

// Everything Steam goes through here (D50). No Steamworks types appear in this class: calls into SteamServices sit in
// separate non-inlined methods inside try blocks, so a machine without Steam (or without the native library) keeps
// TCP working and just sees the error text.
internal static class SteamGate
{
    public const string AddressPrefix = "steam";

    private static float nextAttempt;
    private static float nextFriendsRefresh;
    private static List<HostingFriend> friends = new();

    public static bool Enabled { get; set; } = true;
    public static bool ForceRelay { get; private set; }
    public static bool Ready { get; private set; }
    public static string Error { get; private set; } = "";
    public static string LocalName { get; private set; } = "";
    public static ulong LocalId { get; private set; }
    public static bool LobbyOpen => Ready && LobbyOpenCore();
    public static event Action<ulong>? JoinRequested;
    public static event Action<string>? Log;

    public static bool IsSteamAddress(string address) => address.Trim().StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string address, out ulong steamId)
    {
        steamId = 0;
        string a = address.Trim();
        int colon = a.IndexOf(':');
        return IsSteamAddress(a) && colon > 0 && ulong.TryParse(a.Substring(colon + 1).Trim(), out steamId) && steamId != 0;
    }

    public static string AddressOf(ulong steamId) => AddressPrefix + ":" + steamId;

    // Lazy (D50): Steam starts when the player opens the multiplayer panel or uses a Steam address, so players who
    // never use Steam multiplayer are not shown as "playing Spacewar". Failed attempts retry at most every 10 s.
    public static bool TryInit(bool force = false)
    {
        if (Ready) return true;
        if (!Enabled) { Error = "Steam is disabled in the multiplayer config (Steam.Enabled)."; return false; }
        if (!force && Time.unscaledTime < nextAttempt) return false;
        nextAttempt = Time.unscaledTime + 10f;
        try
        {
            string error = InitCore();
            Ready = error.Length == 0;
            Error = error;
            if (Ready) { Log?.Invoke($"MP: Steam ready, signed in as {LocalName} ({LocalId})"); if (ForceRelay) SetForceRelay(true); }
            else Log?.Invoke("MP: Steam unavailable: " + error);
        }
        catch (Exception e)
        {
            Error = "Steam unavailable: " + e.Message;
            Log?.Invoke("MP: " + Error);
        }
        return Ready;
    }

    public static void Update()
    {
        if (!Ready) return;
        try { UpdateCore(); }
        catch (Exception e) { Log?.Invoke("MP: Steam callbacks failed: " + e.Message); }
    }

    public static ITransport CreateTransport()
    {
        if (!TryInit(force: true)) throw new InvalidOperationException(Error);
        return CreateTransportCore();
    }

    public static void OpenLobby(string sessionId, string version)
    {
        if (!Ready) return;
        try { OpenLobbyCore(sessionId, version); }
        catch (Exception e) { Log?.Invoke("MP: could not open the Steam lobby: " + e.Message); }
    }

    public static void CloseLobby()
    {
        if (!Ready) return;
        try { CloseLobbyCore(); }
        catch (Exception e) { Log?.Invoke("MP: could not close the Steam lobby: " + e.Message); }
    }

    // Friends currently hosting a Processor Tycoon session; refreshed at most every 2 s (the panel calls this per frame).
    public static IReadOnlyList<HostingFriend> FriendsHosting()
    {
        if (!Ready) return Array.Empty<HostingFriend>();
        if (Time.unscaledTime >= nextFriendsRefresh)
        {
            nextFriendsRefresh = Time.unscaledTime + 2f;
            try { friends = FriendsHostingCore(); }
            catch (Exception e) { Log?.Invoke("MP: could not read Steam friends: " + e.Message); }
        }
        return friends;
    }

    public static void Shutdown()
    {
        if (!Ready) return;
        try { ShutdownCore(); } catch { }
        Ready = false;
    }

    public static void SetForceRelay(bool relayOnly)
    {
        ForceRelay = relayOnly;
        if (!Ready) return;
        try { SetForceRelayCore(relayOnly); Log?.Invoke($"MP: Steam routes: {(relayOnly ? "relays only" : "direct when possible")} (new connections)"); }
        catch (Exception e) { Log?.Invoke("MP: could not change Steam routing: " + e.Message); }
    }

    // Route summary per connection of a Steam transport; empty for other transports.
    public static IReadOnlyList<string> Routes(ITransport? transport)
    {
        if (!Ready || transport == null || !IsSteamTransport(transport)) return Array.Empty<string>();
        try { return RoutesCore(transport); }
        catch (Exception e) { return new[] { "route unavailable: " + e.Message }; }
    }

    public static string DetailedRoutes(ITransport? transport)
    {
        if (!Ready || transport == null || !IsSteamTransport(transport)) return "";
        try { return DetailedCore(transport); }
        catch (Exception e) { return "route unavailable: " + e.Message; }
    }

    private static bool IsSteamTransport(ITransport transport) => transport.GetType().Name == "SteamTransport";

    internal static void RaiseJoinRequested(ulong hostId) => JoinRequested?.Invoke(hostId);
    internal static void RaiseLog(string message) => Log?.Invoke(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string InitCore()
    {
        string error = SteamServices.Init();
        if (error.Length == 0) { LocalName = SteamServices.PersonaName; LocalId = SteamServices.LocalId; }
        return error;
    }

    [MethodImpl(MethodImplOptions.NoInlining)] private static void UpdateCore() => SteamServices.Update();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void SetForceRelayCore(bool relayOnly) => SteamServices.SetForceRelay(relayOnly);
    [MethodImpl(MethodImplOptions.NoInlining)] private static List<string> RoutesCore(ITransport transport) => ((SteamTransport)transport).Routes();
    [MethodImpl(MethodImplOptions.NoInlining)] private static string DetailedCore(ITransport transport) => ((SteamTransport)transport).Detailed();
    [MethodImpl(MethodImplOptions.NoInlining)] private static ITransport CreateTransportCore() => new SteamTransport();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void OpenLobbyCore(string sessionId, string version) => SteamServices.OpenLobby(sessionId, version);
    [MethodImpl(MethodImplOptions.NoInlining)] private static void CloseLobbyCore() => SteamServices.CloseLobby();
    [MethodImpl(MethodImplOptions.NoInlining)] private static bool LobbyOpenCore() => SteamServices.LobbyOpen;
    [MethodImpl(MethodImplOptions.NoInlining)] private static List<HostingFriend> FriendsHostingCore() => SteamServices.FriendsHosting();
    [MethodImpl(MethodImplOptions.NoInlining)] private static void ShutdownCore() => SteamServices.Shutdown();
}
