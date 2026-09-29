using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.TimeSystem;
using ProcessorTycoonModApi;
using ProcessorTycoonModApi.Game;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// Chat during a session (D62). Lines appear at the bottom left like the Agent mod's feed (Mod API Feed), above an input
// line that stays on screen: they fade out, hovering the chat shows the history, the mouse wheel scrolls back, Enter or a
// click on the input line writes. "@Name text" is private: only that player
// gets it, as a conversation in their Email (its Reply button opens the chat with "@Name "). With the overlay off (config
// Chat.Overlay or the Multiplayer window), messages arrive as the game's notifications and the window holds the chat.
internal sealed class Chat
{
    private const int ThreadLength = 12;
    private readonly MpRuntime runtime;
    private readonly Plugin plugin;
    private readonly Feed feed;
    private readonly Dictionary<int, List<string>> threads = new();
    private SessionBase? session;

    // The Multiplayer window's chat input, used while the overlay is off.
    public Action<string>? WriteInWindow;

    public Chat(MpRuntime runtime, Plugin plugin)
    {
        this.runtime = runtime;
        this.plugin = plugin;
        // At the left edge above the bottom bar; four idle lines stay below the desktop icons.
        feed = new Feed(Surface.Get(), rightSide: false, placeholder: "Message everyone, or @name for a private email")
        {
            Side = 12, Bottom = 46, MaxWidth = 560, IdleLines = 4, EnterOpens = true, InputAlwaysVisible = true,
            IdleHint = "Press Enter to chat · @name for a private email",
        };
        feed.Submitted += runtime.SendChat;
        runtime.ChatLine += OnLine;
    }

    public bool Overlay => plugin.ChatOverlay.Value;

    public void Tick()
    {
        var s = runtime.Session;
        if (s != session) { session = s; feed.Clear(); threads.Clear(); }
        feed.Enabled = Overlay && s?.State == SessionState.Running && GameWorld.CampaignLoaded;
        feed.Tick();
    }

    // Opens the chat input (overlay or window) with a text such as "@Bob ".
    public void Write(string prefill = "")
    {
        if (Overlay) feed.OpenInput(prefill);
        else WriteInWindow?.Invoke(prefill);
    }

    public void Scroll(int lines) => feed.ScrollBy(lines);

    private void OnLine(ChatLine line)
    {
        var s = runtime.Session;
        if (s == null) return;
        bool mine = line.From == s.LocalSlot;
        if (Overlay) feed.Add(Format(line, s));
        else if (!mine && line.From >= 0) Notify.Show(line.Private ? "Email from " + line.Name : $"{line.Name}: {line.Text}", line.Private ? Clip(line.Text) : "", 8);
        if (line.Private && line.From >= 0 && GameWorld.CampaignLoaded) Thread(line, s, mine);
    }

    private string Format(ChatLine line, SessionBase s)
    {
        const string quiet = "#9DA3AA";
        string text = Escape(line.Text);
        if (line.From < 0) return $"<color={quiet}><i>{text}</i></color>";
        if (!line.Private) return $"{Name(s, line.From)}: {text}";
        return line.From == s.LocalSlot ? $"<color={quiet}>You to</color> {Name(s, line.To)}: {text}" : $"{Name(s, line.From)} <color={quiet}>to you</color>: {text}";
    }

    // A player's name in their company's colour, lightened enough to read on the dark desktop.
    private static string Name(SessionBase s, int slot)
    {
        var player = s.Players.FirstOrDefault(p => p.Slot == slot);
        var company = player != null && player.CompanyId >= 0 ? DataFinder.FindCompany(player.CompanyId) : null;
        var color = Theme.Of(Paint.Tray);
        if (company != null)
        {
            Color.RGBToHSV(company.Color, out var h, out var sat, out var v);
            color = Color.HSVToRGB(h, Mathf.Min(sat, .7f), Mathf.Max(v, .85f));
        }
        return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}><b>{Escape(player?.Name ?? "?")}</b></color>";
    }

    // One Email conversation per player; new messages from them make it unread and bring it to the top.
    private void Thread(ChatLine line, SessionBase s, bool mine)
    {
        int other = mine ? line.To : line.From;
        var player = s.Players.FirstOrDefault(p => p.Slot == other);
        if (player == null) return;
        var company = player.CompanyId >= 0 ? DataFinder.FindCompany(player.CompanyId) : null;
        if (!threads.TryGetValue(other, out var messages)) threads[other] = messages = new List<string>();
        messages.Add($"<b>{(mine ? "You" : Escape(player.Name))}</b>  <color={Theme.Hex(Paint.TextLow)}>{StringFormatter.DateToString(DateController.Instance.CurrentDate)}</color>\n{Escape(line.Text)}");
        if (messages.Count > ThreadLength) messages.RemoveAt(0);
        string name = player.Name;
        var letter = Mail.Post("pm:" + other, "Chat with " + name, company != null ? $"{name} · {company.Name}" : name, string.Join("\n\n", messages), unread: !mine);
        if (letter == null) return;
        letter.Accept = ("Reply", () => Write("@" + name + " "));
        Mail.Update(letter);
    }

    private static string Clip(string text) => text.Length > 40 ? text.Substring(0, 39) + "…" : text;
    private static string Escape(string s) => s.Replace("<", "‹").Replace(">", "›");
}
