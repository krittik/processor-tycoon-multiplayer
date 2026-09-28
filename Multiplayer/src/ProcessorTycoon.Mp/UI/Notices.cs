using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.PopupSystem;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// Tells every player what happens to the others through the game's own notification popups (D55): joins, disconnects,
// returns, the caretaker handing over to the AI, bankruptcies, chat, and the end of the session.
internal sealed class Notices
{
    private sealed class Seen { public bool Connected, Ai, Bankrupt, Founded; }

    private readonly MpRuntime runtime;
    private readonly Dictionary<int, Seen> seen = new();
    private SessionBase? session;
    private int chatSeen;
    private string lastError = "";
    private float next;

    public Notices(MpRuntime runtime) => this.runtime = runtime;

    public void Tick()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + .5f;
        if (runtime.LastError != lastError)
        {
            lastError = runtime.LastError;
            if (lastError.StartsWith("Session ended")) Show("Multiplayer session ended", lastError.Substring("Session ended".Length).TrimStart(':', ' '));
        }
        var s = runtime.Session;
        if (s != session) { session = s; seen.Clear(); chatSeen = runtime.ChatLines.Count; }
        if (s == null || s.State != SessionState.Running || !GameWorld.CampaignLoaded) return;
        int caretakerDays = s is HostSession h ? h.CaretakerDays : s is PeerSession p ? p.Rules.CaretakerDays : 182;
        string period = caretakerDays >= 45 ? $"{caretakerDays / 30} months" : $"{caretakerDays} days";
        bool first = seen.Count == 0;
        foreach (var player in s.Players)
        {
            var company = player.CompanyId >= 0 ? DataFinder.FindCompany(player.CompanyId) : null;
            var now = new Seen { Connected = player.Connected, Ai = player.AiControl, Bankrupt = company?.IsBankrupt == true, Founded = company != null };
            if (!seen.TryGetValue(player.Slot, out var was))
            {
                seen[player.Slot] = now;
                if (!first && player.Slot != s.LocalSlot && now.Founded) Show($"{player.Name} joined", $"They lead {company!.Name}.");
                continue;
            }
            seen[player.Slot] = now;
            if (player.Slot == s.LocalSlot) continue;
            string name = company?.Name ?? player.Name + "'s company";
            // The game's two-line popup fits about 40 characters per line.
            if (!was.Founded && now.Founded) Show($"{player.Name} joined", $"They founded {name}.");
            if (was.Connected && !now.Connected) Show($"{player.Name} disconnected", $"Kept as is; AI takes over in {period}.");
            if (!was.Connected && now.Connected) Show($"{player.Name} is back", $"{name} is theirs again.");
            if (!was.Ai && now.Ai) Show($"The AI now runs {name}", $"{player.Name} was away {period}.");
            if (!was.Bankrupt && now.Bankrupt) Show($"{name} went bankrupt", $"{player.Name} stays and watches.");
        }
        var lines = runtime.ChatLines;
        if (chatSeen > lines.Count) chatSeen = 0;
        for (; chatSeen < lines.Count; chatSeen++)
        {
            string line = lines[chatSeen];
            string me = s.Players.FirstOrDefault(x => x.Slot == s.LocalSlot)?.Name ?? "";
            if (!line.StartsWith(me + ": ")) Show(line, "", 8);
        }
    }

    private static void Show(string message, string secondary, int duration = -1)
    {
        if (PopupManager.Instance == null || !GameWorld.CampaignLoaded) return;
        try { PopupManager.Instance.InstantiateGenericNotification(message, secondary, duration); }
        catch (System.Exception e) { Debug.LogWarning("MP: notification failed: " + e.Message); }
    }
}
