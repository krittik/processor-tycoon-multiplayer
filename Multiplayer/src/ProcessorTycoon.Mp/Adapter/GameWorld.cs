using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// The game as seen by Core sessions. Main thread only.
internal sealed class GameWorld : IGameWorld
{
    private static readonly DateTime Epoch = new(1970, 1, 1);
    private readonly Action<string> log;

    public GameWorld(VersionInfo version, Action<string> log)
    {
        Version = version;
        this.log = log;
        EntityIO.Log = log;
    }

    public VersionInfo Version { get; }
    public Stats Stats { get; } = new();

    public static bool CampaignLoaded => DateController.Instance != null && Player.Instance != null && Player.Instance.Company != null && SaveHandler.Instance != null;

    public int Day => (int)(DateController.Instance.CurrentDate - Epoch).TotalDays;
    public bool IsCheckpointDay(int day) => Epoch.AddDays(day).Day == 1;
    public int Speed => DateController.Instance.currentTimeSpeed;

    public void ShowSpeed(int speed)
    {
        var dc = DateController.Instance;
        int index = dc.speeds.IndexOf(speed);
        if (index < 0 || index >= dc.speedButtons.Count) return;
        var button = dc.speedButtons[index];
        if (button != dc.currentSpeedButton) dc.ManualSetTimeSpeed(index, playsound: false);
        // After a load the button can look selected while the selection registry holds none (Select() is then a no-op;
        // in single player the clock's own update fixes it, which is off on peers). Readers such as the Agent mod ask
        // the registry.
        var registry = ProcessorTycoon.UI.SelectableButtonsHandler.Instance;
        if (registry != null && registry.CurrentButtonWithID(button.UniqueID) != button) registry.SetCurrentButtonWithID(button);
    }

    // D6: peers see the host's speed on greyed-out native buttons.
    public void SetSpeedButtonsInteractable(bool interactable)
    {
        var dc = DateController.Instance;
        if (dc == null) return;
        foreach (var button in dc.speedButtons)
        {
            if (button == null) continue;
            var group = button.GetComponent<CanvasGroup>();
            if (group == null) group = button.gameObject.AddComponent<CanvasGroup>();
            group.interactable = interactable;
            group.blocksRaycasts = interactable;
            group.alpha = interactable ? 1f : 0.4f;
        }
    }

    public void StepDay()
    {
        if (Ownership.IsHost) Contracts.RetryWaiting(log);
        var sw = Stopwatch.StartNew();
        DateController.Instance.TriggerTicks();
        Stats.Tick(sw.Elapsed.TotalMilliseconds);
    }

    public List<EntityState> CaptureOwned()
    {
        var sw = Stopwatch.StartNew();
        var result = EntityIO.Capture(ownedOnly: true, full: IsCheckpointDay(Day + 1), day: Day);
        Stats.Capture(sw.Elapsed.TotalMilliseconds);
        return result;
    }

    public List<EntityState> CaptureReplicated(ISet<long>? owned = null) => EntityIO.Capture(ownedOnly: false, ownedKeys: owned);

    public List<EntityState> Recapture(IReadOnlyCollection<long> keys) => EntityIO.Recapture(keys);

    public void Apply(int fromSlot, IReadOnlyList<EntityDelta> deltas)
    {
        var sw = Stopwatch.StartNew();
        EntityIO.Apply(deltas);
        Stats.Apply(sw.Elapsed.TotalMilliseconds, deltas.Count, deltas.Sum(d => d.Json.Length));
    }

    public void RestoreOwned(IReadOnlyList<EntityState> before)
    {
        int n = EntityIO.RestoreOwned(before);
        if (n > 0) log($"MP: kept {n} of this machine's own entities newer than the host's copy after the resync");
    }

    private HashSet<int> offlineSlots = new();

    public void SetPlayers(IReadOnlyList<PlayerInfo> players, int localSlot)
    {
        // The session mutates the same PlayerInfo objects, so the previous roster cannot tell who just went offline.
        var wasOffline = offlineSlots;
        offlineSlots = new HashSet<int>(players.Where(p => !p.Connected).Select(p => p.Slot));
        Ownership.SetPlayers(players, localSlot);
        Muting.Clear();
        if (!Ownership.IsHost) return;
        // D52: the host continues a disconnected player's projects from their latest carried copy.
        foreach (var p in players.Where(p => !p.Connected && p.CompanyId >= 0 && !wasOffline.Contains(p.Slot)))
        {
            var company = ProcessorTycoon.DataFinder.FindCompany(p.CompanyId);
            if (company == null) continue;
            int restored = Projects.Restore(company);
            if (restored >= 0) log($"MP: {p.Name}'s company keeps {restored} project(s) while they are away");
        }
    }

    public SessionRules Rules => new() { Difficulty = Player.Instance.DifficultyLevel, Year = DateController.Instance.CurrentDate.Year, Cheats = Player.Instance.EnableCheats };

    public int AddPlayer(int slot, CompanySetup setup) => Ghosts.CreateForPlayer(slot, setup);

    // The host's live world through the native save path, with `slot`'s company as the player (IsPlayer swap) and the
    // host's company as a ghost. Player-local data (emails, balance view, active contracts) is not shipped.
    public string BuildSnapshot(int slot)
    {
        var sw = Stopwatch.StartNew();
        string json = SaveIO.SaveNow(SaveIO.Relative(SaveIO.SessionId, "host-snapshot"));
        var save = JsonUtility.FromJson<SaveObject>(json);
        int target = Ownership.Players.First(p => p.Slot == slot).CompanyId;
        foreach (var c in save.Companies)
        {
            if (c.IsPlayer) { c.IsPlayer = false; c.UniqueID = Ownership.GhostId(Ownership.LocalSlot); }
            if (c.SaveID == target) { c.IsPlayer = true; c.UniqueID = "PLAYER"; }
        }
        foreach (var data in save.MarketData.ArchitectureMarketDatas)
            data.OwnerIDs = data.OwnerIDs.Select(id => id == "PLAYER" ? Ownership.GhostId(Ownership.LocalSlot) : id == Ownership.GhostId(slot) ? "PLAYER" : id).ToList();
        save.Emails.Clear();
        save.PlayerBalanceData = new SaveObject.PlayerBalance();
        string result = JsonUtility.ToJson(save);
        log($"MP: snapshot built in {sw.ElapsedMilliseconds} ms");
        return result;
    }

    public void LoadSnapshot(string json, int slot, Action loaded)
    {
        var prefs = CampaignLoaded ? LocalPreferences.Capture() : null;
        string relative = SaveIO.Relative(SaveIO.SessionId, $"join-s{slot}");
        SaveIO.Write(relative, json);
        IdRanges.Reset();
        SaveIO.Load(relative, () =>
        {
            try
            {
                prefs?.Restore();
                Muting.Clear();
                EntityIO.ClearCaches();
                IdRanges.Reset();
                SetSpeedButtonsInteractable(false);
            }
            catch (Exception e) { log("MP: post-load fixups failed: " + e); }
            loaded();
        });
    }

    // D41: agreed checkpoints are hashed monthly, but when writing a save stalls the game (> SlowSaveMs, late eras),
    // only every SlowSaveInterval-th month is written to disk.
    private const long SlowSaveMs = 400;
    private const int SlowSaveInterval = 3;
    private long lastSaveMs;
    private int monthsSinceSave = int.MaxValue / 2;

    public void Checkpoint(int day, string sessionId, string record)
    {
        try
        {
            SaveIO.WriteRecord(sessionId, record);
            monthsSinceSave++;
            if (lastSaveMs > SlowSaveMs && monthsSinceSave < SlowSaveInterval) return;
            var sw = Stopwatch.StartNew();
            SaveIO.WriteCheckpoint(sessionId, Ownership.LocalSlot, DateController.Instance.CurrentDate);
            lastSaveMs = sw.ElapsedMilliseconds;
            monthsSinceSave = 0;
            log($"MP: checkpoint {DateController.Instance.CurrentDate:yyyy-MM-dd} saved in {lastSaveMs} ms");
        }
        catch (Exception e) { log("MP: checkpoint save failed: " + e.Message); }
    }

    // BepInEx/mp-reports/<session>/<day>-slot<N>-<host|peer>/<Kind>-<id>.json; compare the host and peer folders.
    public void DesyncReport(int day, int slot, IReadOnlyList<EntityState> entities)
    {
        try
        {
            string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "mp-reports", SaveIO.SessionId, $"{day}-slot{slot}-{(Ownership.IsHost ? "host" : "peer")}");
            System.IO.Directory.CreateDirectory(dir);
            foreach (var e in entities.Take(40)) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"{e.Kind}-{e.Id}.json"), e.Json);
            log($"MP: desync report ({entities.Count} entities) in {dir}");
        }
        catch (Exception e) { log("MP: desync report failed: " + e.Message); }
    }

    public IReadOnlyList<(string kind, string args)> TakeCommands() => Commands.Take();

    public void ExecuteCommand(int slot, string kind, string args)
    {
        if (kind.StartsWith("contract-")) Contracts.Execute(kind, args, log);
        else if (kind == "architecture-owner") Licences.Execute(slot, args, log);
        else if (kind.StartsWith("business-")) BusinessDeals.Execute(kind, args, log);
        else log($"MP: unknown command {kind} from slot {slot}");
    }

    public void Log(string message) => log(message);
}

// Rolling timings for the status panel and the dev status file.
internal sealed class Stats
{
    public double TickMs, CaptureMs, ApplyMs;
    public int AppliedDeltas, AppliedBytes;
    public void Tick(double ms) => TickMs = TickMs * 0.9 + ms * 0.1;
    public void Capture(double ms) => CaptureMs = CaptureMs * 0.9 + ms * 0.1;
    public void Apply(double ms, int count, int bytes) { ApplyMs = ApplyMs * 0.9 + ms * 0.1; AppliedDeltas += count; AppliedBytes += bytes; }
}

// Player-local settings that must survive loading a snapshot (D11: difficulty and cheats follow the host).
internal sealed class LocalPreferences
{
    private bool automateProduction, automateContracts, automateRejection;
    private int margin, wallpaper;
    private string? importedWallpaper;

    public static LocalPreferences Capture()
    {
        var p = Player.Instance;
        return new LocalPreferences
        {
            automateProduction = p.AutomateProduction, automateContracts = p.AutomateContracts, automateRejection = p.AutomateRejection,
            margin = p.ContractProfitMargin, wallpaper = p.BuiltInWallpaperIndex, importedWallpaper = p.ImportedWallpaperName,
        };
    }

    public void Restore()
    {
        var p = Player.Instance;
        p.AutomateProduction = automateProduction; p.AutomateContracts = automateContracts; p.AutomateRejection = automateRejection;
        p.ContractProfitMargin = margin; p.BuiltInWallpaperIndex = wallpaper; p.ImportedWallpaperName = importedWallpaper;
    }
}
