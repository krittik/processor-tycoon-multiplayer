using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.CompanySystem.Business;
using ProcessorTycoon.Save;
using ProcessorTycoonMp.Core.Delta;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// D45: business contracts (foundry services and other deals) are host-owned and replicated. A deal the local player
// signs or breaks on a peer is sent to the host as a command. Production across companies: a provider's production
// handlers also run on the machine of each of its human clients (Muting), so the client's own CPUs get produced there;
// every other effect of that local run lands on remote entities and is overwritten by their owners.
internal static class BusinessDeals
{
    private static bool applying;
    private static bool approved;   // adding a deal both players agreed to

    // Deals between two players need the other player's consent: the signer's deal becomes a proposal on this
    // channel; the other player answers in a prompt; acceptance is recorded by the host like any deal.
    public const string ProposalChannel = "mp.deal";
    public const string DeclineChannel = "mp.deal-declined";

    public sealed class Proposal
    {
        public int FromSlot;
        public string Json = "";
        public string Text = "";
    }

    public static readonly List<Proposal> Incoming = new();
    private static HashSet<ICompany>? providersForLocalPlayer;

    public static int Key(BusinessContract c) => (int)(Hashing.Fnv(FormattableString.Invariant(
        $"{c.ContractID}|{c.ProviderID}|{c.ClientID}|{c.StartDate?.Year}-{c.StartDate?.Month}")) & 0x7fffffff);

    private static List<BusinessContract> All() => BusinessContractManager.Instance == null ? new List<BusinessContract>() : BusinessContractManager.Instance.contracts;

    public static void Capture(List<EntityState> into)
    {
        var seen = new HashSet<int>();
        foreach (var c in All())
            if (seen.Add(Key(c))) into.Add(new EntityState(EntityKind.BusinessContract, Key(c), JsonUtility.ToJson(DataConverter.BusinessContractToSaveObject(c))));
    }

    // Companies whose production must also run here: providers of an active foundry deal with the local player.
    public static bool ProvidesForLocalPlayer(ICompany company)
    {
        if (!Ownership.Active || Player.Instance?.Company == null) return false;
        providersForLocalPlayer ??= new HashSet<ICompany>(All().Where(c => c.ContractID == 0 && c.Client == Player.Instance.Company && c.Provider != null).Select(c => c.Provider));
        return providersForLocalPlayer.Contains(company);
    }

    public static void Invalidate()
    {
        providersForLocalPlayer = null;
        Muting.Clear();
    }

    private static bool Loading => ProcessorTycoon.GameManager.Instance != null && ProcessorTycoon.GameManager.Instance.IsLoading;

    private static bool LocalParty(BusinessContract c) => Player.Instance?.Company != null && (c.Provider == Player.Instance.Company || c.Client == Player.Instance.Company);

    // Peer: the local player signed a deal (native negotiation) — the host records it for everyone.
    public static void OnAdd(BusinessContract contract)
    {
        Invalidate();
        if (!Ownership.Active || Ownership.IsHost || applying || Loading || !LocalParty(contract)) return;
        Commands.Enqueue("business-add", JsonUtility.ToJson(DataConverter.BusinessContractToSaveObject(contract)));
        EntityIO.Log?.Invoke($"MP: business deal {contract.ContractType()} with {(contract.Provider == Player.Instance.Company ? contract.Client?.Name : contract.Provider?.Name)} sent to the host");
    }

    public static void OnBreak(ICompany breaker, BusinessContract contract)
    {
        Invalidate();
        if (!Ownership.Active || Ownership.IsHost || applying || !LocalParty(contract)) return;
        Commands.Enqueue("business-break", $"{Key(contract)}|{breaker.SaveID}");
    }

    // AddContract prefix for a deal whose both parties are players.
    public static bool AllowPlayerDeal(BusinessContract contract)
    {
        if (approved || applying || Loading) return true;   // agreed, host-replicated and saved deals
        var local = Player.Instance?.Company;
        if (local == null || (contract.Provider != local && contract.Client != local)) return false;
        var other = contract.Provider == local ? contract.Client : contract.Provider;
        Api.MpApi.Send(ProposalChannel, System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(DataConverter.BusinessContractToSaveObject(contract))));
        Notify($"Deal proposed to {other.Name}; it starts when they accept.");
        EntityIO.Log?.Invoke($"MP: deal proposed to {other.Name}");
        return false;
    }

    public static void OnProposal(int fromSlot, byte[] data)
    {
        string json = System.Text.Encoding.UTF8.GetString(data);
        var dto = JsonUtility.FromJson<SaveObject.BusinessContract>(json);
        int local = Player.Instance?.Company?.SaveID ?? -1;
        if (dto.ProviderID != local && dto.ClientID != local) return;
        Incoming.Add(new Proposal { FromSlot = fromSlot, Json = json, Text = Describe(dto) });
    }

    // Dev preview (mp-dev "preview-deal"): a foundry proposal from another player's company to this one.
    public static void Preview(int fromSlot, int providerId)
    {
        var now = ProcessorTycoon.TimeSystem.DateController.Instance.CurrentDate;
        var dto = new SaveObject.BusinessContract
        {
            ContractID = 0, ProviderID = providerId, ClientID = Player.Instance.Company.SaveID,
            StartDate = new ProcessorTycoon.TimeSystem.Date(now.Year, now.Month), TerminationDate = new ProcessorTycoon.TimeSystem.Date(now.Year + 3, now.Month),
            ContractValues = new List<int> { 40, 25, 2 }, MonetaryValues = new List<float> { 1_500_000f, 750_000f }, Conditions = new List<bool> { false },
        };
        Incoming.Add(new Proposal { FromSlot = fromSlot, Json = JsonUtility.ToJson(dto), Text = Describe(dto) });
    }

    public static void OnDeclined(int fromSlot, byte[] data)
    {
        var dto = JsonUtility.FromJson<SaveObject.BusinessContract>(System.Text.Encoding.UTF8.GetString(data));
        int local = Player.Instance?.Company?.SaveID ?? -1;
        if (dto.ProviderID != local && dto.ClientID != local) return;
        var other = DataFinder.FindCompany(dto.ProviderID == local ? dto.ClientID : dto.ProviderID);
        Notify($"{other?.Name ?? "The other player"} declined the deal.");
    }

    public static void Answer(Proposal proposal, bool accept)
    {
        Incoming.Remove(proposal);
        if (!accept) { Api.MpApi.Send(DeclineChannel, System.Text.Encoding.UTF8.GetBytes(proposal.Json)); return; }
        if (Ownership.IsHost) AddApproved(proposal.Json, m => EntityIO.Log?.Invoke(m));
        else Commands.Enqueue("business-add-approved", proposal.Json);
    }

    private static void AddApproved(string json, Action<string> log)
    {
        var contract = SaveObjectInstantiator.InstantiateFromSave(JsonUtility.FromJson<SaveObject.BusinessContract>(json));
        if (contract.Provider == null || contract.Client == null || All().Any(c => Key(c) == Key(contract))) return;
        approved = true;
        try { BusinessContractManager.Instance.AddContract(contract); }
        finally { approved = false; }
        log($"MP: deal between players {contract.Provider.Name} → {contract.Client.Name} recorded");
    }

    // Every term of the proposal, read from the save values the game uses (BusinessContract.SetupFoundryServices):
    // ContractValues = [factory %, markup %, lines], MonetaryValues = [provider fines, client fines], Conditions = [exclusive].
    public static string Describe(SaveObject.BusinessContract dto)
    {
        int local = Player.Instance?.Company?.SaveID ?? -1;
        string Name(int id) => (DataFinder.FindCompany(id)?.Name ?? "?") + (id == local ? " (you)" : "");
        string Month(ProcessorTycoon.TimeSystem.Date? d) => d == null ? "?" : new DateTime(d.Year, Math.Max(1, d.Month), 1).ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        string Money(float v) => StringFormatter.FloatToMoney(v);
        var v = dto.ContractValues;
        var m = dto.MonetaryValues;
        bool exclusive = dto.Conditions.Count > 0 && dto.Conditions[0];
        var lines = new List<string> { $"<b>{(dto.ContractID == 0 ? "Foundry services" : $"Deal type {dto.ContractID}")}</b>: {Name(dto.ProviderID)} manufactures CPUs for {Name(dto.ClientID)}." };
        if (v.Count >= 2)
        {
            lines.Add($"Capacity: {(v.Count > 2 ? $"{v[2]} lines, " : "")}{v[0]}% of the factory{(exclusive ? ", exclusive" : "")}");
            lines.Add($"Price: production cost +{v[1]}%");
        }
        lines.Add($"Term: {Month(dto.StartDate)} to {Month(dto.TerminationDate)}{(dto.RenewalEnabled ? ", renews automatically" : "")}");
        if (m.Count >= 2) lines.Add($"Fines: provider {Money(m[0])}, client {Money(m[1])}; ending it early costs {Money(exclusive ? m[1] : m[0] * 6f)}");
        return string.Join("\n", lines);
    }

    private static void Notify(string text)
    {
        try { ProcessorTycoon.PopupSystem.PopupManager.Instance.InstantiateGenericNotification(text); } catch { }
    }

    // Host: execute a peer's deal command (trusted peers, D12).
    public static void Execute(string kind, string args, Action<string> log)
    {
        var manager = BusinessContractManager.Instance;
        if (kind == "business-add-approved") AddApproved(args, log);
        else if (kind == "business-add")
        {
            var dto = JsonUtility.FromJson<SaveObject.BusinessContract>(args);
            var contract = SaveObjectInstantiator.InstantiateFromSave(dto);
            if (contract.Provider == null || contract.Client == null) { log("MP: business deal between unknown companies ignored"); return; }
            if (All().Any(c => Key(c) == Key(contract))) return;
            manager.AddContract(contract);
            log($"MP: business deal {contract.ContractType()} {contract.Provider.Name} → {contract.Client.Name} recorded");
        }
        else if (kind == "business-break")
        {
            var parts = args.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int key) || !int.TryParse(parts[1], out int breakerId)) return;
            var contract = All().FirstOrDefault(c => Key(c) == key);
            var breaker = DataFinder.FindCompany(breakerId);
            if (contract == null || breaker == null) return;
            manager.BreakContract(breaker, contract.Provider == breaker ? contract.Client : contract.Provider, contract);
            log($"MP: business deal broken by {breaker.Name}");
        }
        Invalidate();
    }

    // Peers: host deltas (after companies and CPUs). New deals use the native AddContract (popups, UI events).
    public static void Apply(IEnumerable<EntityDelta> deltas, Action<string> log)
    {
        var manager = BusinessContractManager.Instance;
        if (Ownership.IsHost || manager == null) return;
        bool changed = false;
        applying = true;
        try
        {
            foreach (var d in deltas)
            {
                var existing = All().FirstOrDefault(c => Key(c) == d.Id);
                if (d.Op == DeltaOp.Remove)
                {
                    if (existing == null) continue;
                    manager.contracts.Remove(existing);
                    if (LocalParty(existing)) PopupManagerTerminated(existing);
                    changed = true;
                    continue;
                }
                if (existing != null) { JsonUtility.FromJsonOverwrite(d.Json, existing); changed = true; continue; }
                if (d.Op != DeltaOp.Upsert) continue;
                var contract = SaveObjectInstantiator.InstantiateFromSave(JsonUtility.FromJson<SaveObject.BusinessContract>(d.Json));
                if (contract.Provider == null || contract.Client == null) continue;
                manager.AddContract(contract);
                changed = true;
            }
        }
        catch (Exception e) { log("MP: applying business deals failed: " + e); }
        finally { applying = false; }
        if (changed)
        {
            Invalidate();
            try { (typeof(BusinessContractManager).GetField("OnContractTerminated", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(manager) as Action)?.Invoke(); }
            catch (Exception e) { log("MP: business UI refresh failed: " + e.Message); }
        }
    }

    private static void PopupManagerTerminated(BusinessContract contract)
    {
        try { ProcessorTycoon.PopupSystem.PopupManager.Instance.InstantiateBusinessContractTerminated(contract); } catch { }
    }
}
