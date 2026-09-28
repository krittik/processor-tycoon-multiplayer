using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace ProcessorTycoonMp.Spikes;

// One M0 experiment. Runs on the Unity main thread when triggered; reports through the log.
public interface ISpike
{
    string Name { get; }
    KeyCode Key { get; }
    void Run(ManualLogSource log);
}

// Host for M0 spikes (docs/ROADMAP.md). Results go to BepInEx/LogOutput.log and are recorded in docs/GAME_INTERNALS.md.
// Trigger: Ctrl+Shift+Key, or write the spike name into <game>/mp-dev/spike.txt (used by scripted runs; the file is consumed).
[BepInPlugin("processortycoon.multiplayer.spikes", "Processor Tycoon Multiplayer Spikes", "0.0.1")]
[BepInProcess("Processor Tycoon Beta.exe")]
public sealed class SpikesPlugin : BaseUnityPlugin
{
    private readonly List<ISpike> spikes = new() { new S3Inventory(), new S1Overwrite(), new S5Cost() };
    private string triggerFile = "";
    private float nextPoll;

    private void Awake()
    {
        triggerFile = Path.Combine(Paths.GameRootPath, "mp-dev", "spike.txt");
        Logger.LogInfo($"Spikes loaded: " + string.Join(", ", spikes.ConvertAll(s => $"{s.Name} (Ctrl+Shift+{s.Key})")) + $"; trigger file {triggerFile}");
    }

    private void Update()
    {
        if (Time.unscaledTime >= nextPoll)
        {
            nextPoll = Time.unscaledTime + 0.5f;
            if (File.Exists(triggerFile))
            {
                string[] names = File.ReadAllLines(triggerFile);
                File.Delete(triggerFile);
                foreach (var name in names)
                    foreach (var spike in spikes)
                        if (string.Equals(spike.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) Run(spike);
            }
        }
        if (!Input.GetKey(KeyCode.LeftControl) || !Input.GetKey(KeyCode.LeftShift)) return;
        foreach (var spike in spikes)
            if (Input.GetKeyDown(spike.Key)) Run(spike);
    }

    private void Run(ISpike spike)
    {
        Logger.LogInfo($"[{spike.Name}] start");
        try { spike.Run(Logger); Logger.LogInfo($"[{spike.Name}] done"); }
        catch (Exception e) { Logger.LogError($"[{spike.Name}] failed: {e}"); }
    }
}
