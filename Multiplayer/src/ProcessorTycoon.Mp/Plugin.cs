using System;
using BepInEx;
using BepInEx.Configuration;
using ProcessorTycoonMp.Api;
using ProcessorTycoonMp.Core;
using ProcessorTycoonMp.Core.Transport;
using ProcessorTycoonMp.Diagnostics;
using ProcessorTycoonMp.Steam;
using ProcessorTycoonMp.UI;
using UnityEngine;

namespace ProcessorTycoonMp;

// Inert outside a session: only the load hooks for MP saves are installed at startup (D42); all other Harmony patches
// exist only while a session runs (D18).
[BepInPlugin(MpProtocol.PluginGuid, "Processor Tycoon Multiplayer", MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("Processor Tycoon Beta.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    internal ConfigEntry<string> PlayerName = null!;
    internal ConfigEntry<string> HostPort = null!;
    internal ConfigEntry<string> JoinAddress = null!;
    internal ConfigEntry<string> HostVia = null!;
    internal ConfigEntry<bool> ChatOverlay = null!;
    private ConfigEntry<string> clientId = null!;
    private ConfigEntry<bool> steamEnabled = null!;
    private ConfigEntry<bool> steamForceRelay = null!;
    private ConfigEntry<int> headlessFrameRate = null!;
    private float headlessCheck;
    private MpRuntime? runtime;
    private MpUi? ui;
    private ConfigEntry<int> caretakerDays = null!;
    private DevControl? dev;

    private void Awake()
    {
        PlayerName = Config.Bind("Player", "Name", "", "Your name in multiplayer sessions. Empty: your Steam name, or \"Player\".");
        // Earlier versions defaulted to the Windows user name; never show that to other players unless it was typed in.
        if (PlayerName.Value == Environment.UserName) PlayerName.Value = "";
        HostVia = Config.Bind("Network", "HostVia", "Steam", "How you host: Steam (friends join through Steam, no port forwarding) or Address (players connect to your IP address and HostPort).");
        ChatOverlay = Config.Bind("Chat", "Overlay", true, "Show chat at the bottom left of the screen during a session (hover it to read more, Enter to write). Off: chat lives in the Multiplayer window and messages arrive as notifications.");
        HostPort = Config.Bind("Network", "HostPort", TcpTransport.DefaultPort.ToString(), "TCP port to listen on when hosting (forward it for internet play).");
        JoinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1:" + TcpTransport.DefaultPort, "Last address joined (ip:port, or steam:<Steam id>).");
        steamEnabled = Config.Bind("Steam", "Enabled", true, "Offer Steam hosting and joining (friends, no port forwarding). Steam starts when you open the multiplayer panel and shows you as playing Spacewar (Valve's test app).");
        steamForceRelay = Config.Bind("Steam", "ForceRelay", false, "Send all Steam traffic through Valve's relays, never directly (testing an internet path on one LAN; hides your IP from other players).");
        headlessFrameRate = Config.Bind("Headless", "FrameRate", 30, "Frame cap when the game runs headless (-batchmode -nographics, driven through mp-dev/cmd.txt). Without it the game loop uses a whole CPU core.");
        clientId = Config.Bind("Player", "ClientId", "", "Identifies this installation to hosts (keeps your slot when you rejoin). Generated automatically.");
        caretakerDays = Config.Bind("Session", "CaretakerDays", 182, "Host: game days a disconnected player's company carries on exactly as they left it before the AI starts playing it (until they return).");
        if (string.IsNullOrEmpty(clientId.Value)) clientId.Value = Guid.NewGuid().ToString("N");
        Logger.LogInfo($"Multiplayer {ModInfo.Short} loaded (protocol {MpProtocol.Version}, target game {MpProtocol.TargetGameVersion}). Press F9 or use the Multiplayer tray entry.");
    }

    // After every plugin's Awake, so the mod list for the version check (D21) is complete.
    private void Start()
    {
        Adapter.LoadHooks.Install();
        runtime = new MpRuntime(Logger, Info.Metadata.Version.ToString(), clientId.Value);
        MpApi.Runtime = runtime;
        MpApi.RegisterChannel(Adapter.BusinessDeals.ProposalChannel, Adapter.BusinessDeals.OnProposal);
        MpApi.RegisterChannel(Adapter.BusinessDeals.DeclineChannel, Adapter.BusinessDeals.OnDeclined);
        MpApi.RegisterChannel(Adapter.BusinessDeals.ExpiredChannel, Adapter.BusinessDeals.OnExpired);
        ProcessorTycoonModApi.Game.Mail.Owner = MpProtocol.PluginGuid;
        runtime.CaretakerDays = System.Math.Max(1, caretakerDays.Value);
        if (!Application.isBatchMode) ui = new MpUi(runtime, this);
        dev = new DevControl(runtime, Paths.GameRootPath) { ShowPanel = visible => ui?.Show(visible), ShowCredits = () => ui?.ShowCredits(), CloseUi = () => ui?.CloseAll(), WriteChat = text => ui?.WriteChat(text) };
        SteamGate.Enabled = steamEnabled.Value;
        if (steamForceRelay.Value) SteamGate.SetForceRelay(true);
        SteamGate.Log += runtime.Log;
        // Steam friends list "Join game" / an accepted invite.
        SteamGate.JoinRequested += hostId =>
        {
            if (runtime.Session != null || runtime.Busy) return;
            ui?.Show(true);
            runtime.Join(SteamGate.AddressOf(hostId), PlayerName.Value, ui == null ? rules => JoinSetup.Auto(rules, PlayerName.Value, PlayerName.Value, 0) : null);
        };
    }

    private void Update()
    {
        if (runtime == null) return;
        if (Application.isBatchMode) EnforceHeadless();
        if (Input.GetKeyDown(KeyCode.F9)) ui?.Toggle();
        if (SteamGate.Ready) SteamGate.Update();
        runtime.Update(Time.unscaledDeltaTime);
        dev!.Update(PlayerName.Value);
        try { ui?.Update(); }
        catch (Exception e) { Logger.LogWarning("MP UI: " + e); }
    }

    // Headless peer (tools/start-headless.ps1): no vsync caps the loop and nobody listens. Re-applied every few seconds
    // because the game applies its own settings on scene loads.
    private void EnforceHeadless()
    {
        if (Time.unscaledTime < headlessCheck) return;
        headlessCheck = Time.unscaledTime + 5f;
        if (Application.targetFrameRate != headlessFrameRate.Value) Application.targetFrameRate = headlessFrameRate.Value;
        QualitySettings.vSyncCount = 0;
        AudioListener.volume = 0f;
    }

    private void OnDestroy()
    {
        runtime?.Leave();
        SteamGate.Shutdown();
    }
}
