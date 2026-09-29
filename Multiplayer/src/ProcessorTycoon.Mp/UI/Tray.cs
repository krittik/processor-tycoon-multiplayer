using System;
using System.Linq;
using ProcessorTycoonModApi;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// The entry in the game's bottom bar during a game (Mod API BarItem, left of the Agent mod's): "Multiplayer · hosting ·
// 3 online". A click opens the Multiplayer window above it. In the main menu the menu's own Multiplayer entry takes its
// place.
internal sealed class Tray
{
    private readonly MpRuntime runtime;
    private readonly BarItem item;

    public RectTransform Item => item.Rect;

    public Tray(MpRuntime runtime, Action toggle)
    {
        this.runtime = runtime;
        item = new BarItem(Surface.Get(), "multiplayer", 10, Theme.Sprite("employees"), "Multiplayer") { ShowInMenu = false };
        item.Clicked += toggle;
    }

    public void Refresh()
    {
        item.Icon = Theme.Sprite("employees");
        var s = runtime.Session;
        item.Text = s == null ? (runtime.Busy ? "Multiplayer · resuming…" : "Multiplayer")
            : s.State != SessionState.Running ? (s is PeerSession p && p.PendingSetup != null ? "Multiplayer · founding your company" : "Multiplayer · connecting…")
            : $"Multiplayer · {(s.IsHost ? "hosting" : "joined")} · {s.Players.Count(x => x.Connected)} online";
        item.Tick();
    }
}
