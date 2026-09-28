using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Steamworks;
using UnityEngine;

namespace ProcessorTycoonMp.Steam;

// Steamworks initialization, callbacks and the discovery lobby (D50). Only SteamGate calls this class.
internal static class SteamServices
{
    // Spacewar, Valve's public test app: the game has no Steam app of its own for this mod (D13).
    public const uint AppId = 480;
    private const string VersionKey = "pt_mp";
    private const string HostKey = "pt_mp_host";
    private const string SessionKey = "pt_mp_session";

    private static Callback<GameLobbyJoinRequested_t>? joinRequested;
    private static Callback<LobbyDataUpdate_t>? lobbyDataUpdated;
    private static CallResult<LobbyCreated_t>? lobbyCreated;
    private static CSteamID lobby = CSteamID.Nil;
    private static bool wantLobby;
    private static string lobbySession = "";
    private static string lobbyVersion = "";
    private static readonly Dictionary<ulong, float> lobbyDataRequested = new();

    public static string PersonaName { get; private set; } = "";
    public static ulong LocalId { get; private set; }
    public static bool LobbyOpen => lobby.IsValid();

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    public static string Init()
    {
        // The app id comes from the environment, not a steam_appid.txt in the game folder (D13). It must be set before
        // the native library loads.
        Environment.SetEnvironmentVariable("SteamAppId", AppId.ToString());
        Environment.SetEnvironmentVariable("SteamGameId", AppId.ToString());
        // steam_api64.dll ships next to the plugin, outside Mono's search path: loading it by full path first makes the
        // P/Invokes bind to it.
        if (Application.platform == RuntimePlatform.WindowsPlayer)
        {
            string native = Path.Combine(Path.GetDirectoryName(typeof(SteamServices).Assembly.Location)!, "steam_api64.dll");
            if (!File.Exists(native)) return "steam_api64.dll is missing from the plugin folder (reinstall the mod).";
            if (LoadLibraryW(native) == IntPtr.Zero) return $"could not load steam_api64.dll (Windows error {Marshal.GetLastWin32Error()}).";
        }
        var result = SteamAPI.InitEx(out string message);
        if (result == ESteamAPIInitResult.k_ESteamAPIInitResult_NoSteamClient) return "Steam is not running: start Steam, sign in, then click Retry.";
        if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK) return $"Steam could not start ({result}: {message}).";

        PersonaName = SteamFriends.GetPersonaName();
        LocalId = SteamUser.GetSteamID().m_SteamID;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        // Snapshots are hundreds of KiB: a larger send buffer avoids stalls; relay rendezvous can take a while.
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 32 * 1024 * 1024);
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, 30_000);
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, 30_000);
        // Only the development "steamip:" mode uses IP sockets; both ends of such a test share one account.
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_IP_AllowWithoutAuth, 1);
        joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
        lobbyDataUpdated = Callback<LobbyDataUpdate_t>.Create(_ => { });
        lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        return "";
    }

    // Off: only Valve's relays (emulates an internet path between machines on one LAN). On: Steam picks direct routes
    // (ICE) when it can. Applies to new connections.
    public static void SetForceRelay(bool relayOnly) =>
        SetGlobal(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable,
            relayOnly ? Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable : Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Default);

    public static void Update()
    {
        SteamAPI.RunCallbacks();
        SteamTransport.CloseRetired(force: false);
    }

    public static void Shutdown()
    {
        SteamTransport.CloseRetired(force: true);
        CloseLobby();
        joinRequested?.Dispose();
        lobbyDataUpdated?.Dispose();
        lobbyCreated?.Dispose();
        SteamAPI.Shutdown();
    }

    // The lobby only makes the host visible to friends (friends list, "Join game"); players connect to the host's
    // Steam id directly (SteamTransport).
    public static void OpenLobby(string sessionId, string version)
    {
        CloseLobby();
        wantLobby = true;
        lobbySession = sessionId;
        lobbyVersion = version;
        lobbyCreated!.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 8));
    }

    public static void CloseLobby()
    {
        wantLobby = false;
        if (lobby.IsValid()) SteamMatchmaking.LeaveLobby(lobby);
        lobby = CSteamID.Nil;
        SteamFriends.ClearRichPresence();
    }

    public static List<HostingFriend> FriendsHosting()
    {
        var result = new List<HostingFriend>();
        int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (int i = 0; i < count; i++)
        {
            var friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(friend, out var game)) continue;
            if (game.m_gameID.AppID().m_AppId != AppId || !game.m_steamIDLobby.IsValid()) continue;
            string version = SteamMatchmaking.GetLobbyData(game.m_steamIDLobby, VersionKey);
            if (version.Length == 0)
            {
                // Lobby data of a lobby we are not in arrives asynchronously; ask again every 5 s until it does.
                ulong id = game.m_steamIDLobby.m_SteamID;
                if (!lobbyDataRequested.TryGetValue(id, out float at) || Time.unscaledTime - at > 5f)
                {
                    lobbyDataRequested[id] = Time.unscaledTime;
                    SteamMatchmaking.RequestLobbyData(game.m_steamIDLobby);
                }
                continue;
            }
            result.Add(new HostingFriend(friend.m_SteamID, SteamFriends.GetFriendPersonaName(friend), version));
        }
        return result;
    }

    private static void OnLobbyCreated(LobbyCreated_t created, bool ioFailure)
    {
        if (ioFailure || created.m_eResult != EResult.k_EResultOK)
        {
            SteamGate.RaiseLog($"MP: Steam lobby not created ({(ioFailure ? "I/O failure" : created.m_eResult.ToString())}); friends can still join with the Steam address");
            return;
        }
        var id = new CSteamID(created.m_ulSteamIDLobby);
        if (!wantLobby) { SteamMatchmaking.LeaveLobby(id); return; }
        lobby = id;
        SteamMatchmaking.SetLobbyData(lobby, VersionKey, lobbyVersion);
        SteamMatchmaking.SetLobbyData(lobby, HostKey, PersonaName);
        SteamMatchmaking.SetLobbyData(lobby, SessionKey, lobbySession);
        SteamFriends.SetRichPresence("status", "Hosting Processor Tycoon multiplayer");
        SteamGate.RaiseLog($"MP: Steam lobby {lobby.m_SteamID} open for friends");
    }

    // Steam friends list "Join game" (or an accepted lobby invite) while the game runs with Steam started. The friend
    // in the lobby is its host: only hosts join their own discovery lobby.
    private static void OnJoinRequested(GameLobbyJoinRequested_t request) => SteamGate.RaiseJoinRequested(request.m_steamIDFriend.m_SteamID);

    private static void SetGlobal(ESteamNetworkingConfigValue key, int value)
    {
        var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
        try
        {
            SteamNetworkingUtils.SetConfigValue(key, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero,
                ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32, handle.AddrOfPinnedObject());
        }
        finally { handle.Free(); }
    }
}
