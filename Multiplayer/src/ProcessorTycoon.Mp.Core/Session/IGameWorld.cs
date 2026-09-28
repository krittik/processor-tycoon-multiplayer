using System;
using System.Collections.Generic;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Protocol;

namespace ProcessorTycoonMp.Core.Session;

// What the session needs from the game (implemented by the plugin's Adapter; faked in tests).
// All calls happen on the main thread.
public interface IGameWorld
{
    VersionInfo Version { get; }

    // Index of the current in-game day (the last simulated one).
    int Day { get; }
    // True when simulating `day` starts a new month (checkpoint day, D15).
    bool IsCheckpointDay(int day);
    // Host: native speed in days per second (0 = paused).
    int Speed { get; }
    // Peers: show the host's speed on the (non-interactable) native speed buttons.
    void ShowSpeed(int speed);
    // Simulate exactly one day.
    void StepDay();

    // Entities this machine is authoritative for.
    List<EntityState> CaptureOwned();
    // Every replicated entity (owned and remote), canonical JSON, for world hashes and drift repair; `owned` (optional)
    // receives the keys of the entities this machine owns.
    List<EntityState> CaptureReplicated(ISet<long>? owned = null);
    // Fresh canonical JSON of the given entities (after a repair); missing entities are left out.
    List<EntityState> Recapture(IReadOnlyCollection<long> keys);
    // Apply another owner's changes (Upsert creates or overwrites, Patch merges, Remove deletes).
    void Apply(int fromSlot, IReadOnlyList<EntityDelta> deltas);

    // Peer after a resync snapshot: put this machine's own entities back as they were before the load (the owner is
    // authoritative; the host's copy can be a day behind). Only the members that differ are written.
    void RestoreOwned(IReadOnlyList<EntityState> before);

    // Slot and company assignments; localSlot is this machine's slot.
    void SetPlayers(IReadOnlyList<PlayerInfo> players, int localSlot);
    // Host: the rules a new player's company is set up under.
    SessionRules Rules { get; }
    // Host: create the company a new player set up; returns its company id.
    int AddPlayer(int slot, CompanySetup setup);
    // Host: full world as save JSON in which `slot`'s company is the local player.
    string BuildSnapshot(int slot);
    // Peer: load a snapshot (asynchronous: the game reloads its scene), then call `loaded`.
    void LoadSnapshot(string json, int slot, Action loaded);
    // Hash mismatch at checkpoint `day` involving `slot`: write this machine's JSON of the differing entities (D15).
    void DesyncReport(int day, int slot, IReadOnlyList<EntityState> entities);
    // Checkpoint agreed for `day` (all hashes matched): write the checkpoint save and the session record (D10).
    void Checkpoint(int day, string sessionId, string record);

    // Peer: actions on host-owned shared objects made since the last call (sent to the host as commands).
    IReadOnlyList<(string kind, string args)> TakeCommands();
    // Host: apply a command from a peer (the result replicates through the normal deltas).
    void ExecuteCommand(int slot, string kind, string args);

    void Log(string message);
}
