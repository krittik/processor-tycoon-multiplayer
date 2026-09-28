using System.Collections.Generic;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoonMp.Core.Protocol;

namespace ProcessorTycoonMp.Adapter;

// Who owns what on this machine (ARCHITECTURE "Topology and ownership").
// Human companies are the local player's `Company` or ghost `AICompany`s with UniqueID "MPGHOST-<slot>" (D4).
internal static class Ownership
{
    public const string GhostPrefix = "MPGHOST-";
    public const int SlotRange = 10_000_000;   // D20

    private static readonly Dictionary<int, int> slotByCompany = new();
    private static readonly HashSet<int> offlineSlots = new();
    private static readonly HashSet<int> caretakerSlots = new();

    public static bool Active { get; set; }
    public static bool IsHost { get; set; }
    public static int LocalSlot { get; private set; }
    public static IReadOnlyList<PlayerInfo> Players { get; private set; } = new List<PlayerInfo>();
    public static int Version { get; private set; }   // bumped on every roster change (muting cache key)

    public static void SetPlayers(IReadOnlyList<PlayerInfo> players, int localSlot)
    {
        Players = new List<PlayerInfo>(players);
        LocalSlot = localSlot;
        slotByCompany.Clear();
        offlineSlots.Clear();
        caretakerSlots.Clear();
        foreach (var p in players)
        {
            if (p.CompanyId >= 0) slotByCompany[p.CompanyId] = p.Slot;
            if (!p.Connected) offlineSlots.Add(p.Slot);
            if (!p.Connected && !p.AiControl) caretakerSlots.Add(p.Slot);
        }
        Version++;
    }

    public static void Reset()
    {
        Active = false;
        IsHost = false;
        LocalSlot = 0;
        slotByCompany.Clear();
        offlineSlots.Clear();
        caretakerSlots.Clear();
        Players = new List<PlayerInfo>();
        Version++;
    }

    public static string GhostId(int slot) => GhostPrefix + slot;

    public static int GhostSlot(string? uniqueId) =>
        uniqueId != null && uniqueId.StartsWith(GhostPrefix) && int.TryParse(uniqueId.Substring(GhostPrefix.Length), out int s) ? s : -1;

    // Slot of a human company, -1 for AI companies. Once the roster is known it alone decides (D51): a save made after
    // a session keeps the other players' companies as "MPGHOST-<slot>" AI rivals, and a later session must not mistake
    // them for its own players.
    public static int SlotOf(ICompany c)
    {
        if (c.IsPlayer) return LocalSlot;
        if (slotByCompany.TryGetValue(c.SaveID, out int slot)) return slot;
        return Players.Count == 0 ? GhostSlot(c.UniqueID) : -1;
    }

    public static bool IsGhost(ICompany c) => !c.IsPlayer && SlotOf(c) >= 0;

    // The machine that simulates this company: its human owner, or the host for AI companies and for the companies
    // of disconnected players, which the AI plays until they return (D25).
    public static bool OwnsLocally(ICompany? c)
    {
        if (!Active || c == null) return true;
        if (c.IsPlayer) return true;
        if (!IsHost) return false;
        int slot = SlotOf(c);
        return slot < 0 || offlineSlots.Contains(slot);
    }

    // D55: a disconnected player's company in its caretaker period: simulated on the host, but no AI decisions.
    public static bool IsCaretaker(ICompany c) => IsHost && IsGhost(c) && caretakerSlots.Contains(SlotOf(c));

    // AI decisions for this company run on this machine: it simulates the company and no caretaker holds it.
    public static bool AiDecides(ICompany? c) => OwnsLocally(c) && !(Active && c != null && IsCaretaker(c));

    public static PlayerInfo? PlayerOf(ICompany c)
    {
        int slot = SlotOf(c);
        if (slot < 0) return null;
        foreach (var p in Players) if (p.Slot == slot) return p;
        return null;
    }

    public static ICompany LocalCompany => Player.Instance.Company;
}
