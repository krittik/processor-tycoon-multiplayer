using System;
using HarmonyLib;
using ProcessorTycoon;
using ProcessorTycoon.AISystem;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.CompanySystem.Business;
using ProcessorTycoon.ContractSystem;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.ProjectSystem;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// Harmony patches, applied only while a session runs (D18). Prefix/postfix only; `return false` only for clock and
// muting. Every target is compile-checked against the publicized game assembly (D23).
[HarmonyPatch]
internal static class Hooks
{
    public const string HarmonyId = "processortycoon.multiplayer";
    private static Harmony? harmony;

    public static bool Applied => harmony != null;

    public static void Apply()
    {
        if (harmony != null) return;
        harmony = new Harmony(HarmonyId);
        harmony.CreateClassProcessor(typeof(Hooks)).Patch();
    }

    public static void Remove()
    {
        harmony?.UnpatchSelf();
        harmony = null;
    }

    // D58: the UI answers a pending company setup instead of starting a local game.
    public static Func<ProcessorTycoon.InitialData.PlayerInitialData, bool>? NewGame;

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartNewGame)), HarmonyPrefix]
    private static bool StartNewGame(ProcessorTycoon.InitialData.PlayerInitialData playerInitialData) => NewGame == null || !NewGame(playerInitialData);

    // --- Clock (D6, D7) ---

    // The MP clock calls TriggerTicks itself; native time accumulation is off during a session.
    [HarmonyPatch(typeof(DateController), "Update"), HarmonyPrefix]
    private static bool DateControllerUpdate() => !Ownership.Active;

    // Native dialog pause is disabled during a session (D7).
    [HarmonyPatch(typeof(PauseHandler), "Update"), HarmonyPrefix]
    private static bool PauseHandlerUpdate() => !Ownership.Active;

    // Closing the Default warning or pressing Select New Research pauses the game directly (ResumeAndOverride(0)); on the
    // host that stopped every player (D54). Only the host's speed controls change the shared clock.
    [HarmonyPatch(typeof(DateController), nameof(DateController.ResumeAndOverride)), HarmonyPrefix]
    private static bool ResumeAndOverride() => !Ownership.Active;

    // --- Muting (D19) ---

    [HarmonyPatch(typeof(DateController), "TriggerTicks"), HarmonyPrefix]
    private static void TriggerTicksPrefix(DateController __instance) => Muting.Before(__instance);

    [HarmonyPatch(typeof(DateController), "TriggerTicks"), HarmonyFinalizer]
    private static Exception? TriggerTicksFinalizer(DateController __instance, Exception? __exception)
    {
        Muting.After(__instance);
        return __exception;
    }

    // Shared iterator: ticks every project of every company; skip projects of companies simulated elsewhere.
    [HarmonyPatch(typeof(TimerScheduler), nameof(TimerScheduler.PassDay)), HarmonyPrefix]
    private static bool PassDay(TimerScheduler __instance)
    {
        if (!Ownership.Active) return true;
        var timers = __instance.timers;
        for (int i = timers.Count - 1; i >= 0; i--)
        {
            if (i >= timers.Count) continue;
            var timer = timers[i];
            if (timer.IsPaused) continue;
            var company = timer switch { Project p => p.Company, ProjectContinuous c => c.Company, _ => null };
            if (company != null && !Ownership.OwnsLocally(company)) continue;
            timer.Tick();
        }
        return false;
    }

    // AI decisions called outside tick events (price updates on AddCpu, bankruptcy handling).
    // Muted on machines that do not simulate the company and, on the host, while a caretaker holds a player's company (D55).
    [HarmonyPatch(typeof(AIBehaviourController), nameof(AIBehaviourController.TriggerPriceUpdate)), HarmonyPrefix]
    private static bool TriggerPriceUpdate(AIBehaviourController __instance) => Ownership.AiDecides(__instance.GetComponent<ICompany>());

    [HarmonyPatch(typeof(AIBehaviourController), nameof(AIBehaviourController.SellProductionLinesToAvoidBankruptcy)), HarmonyPrefix]
    private static bool SellLines(AIBehaviourController __instance) => Ownership.AiDecides(__instance.GetComponent<ICompany>());

    [HarmonyPatch(typeof(AIBehaviourController), nameof(AIBehaviourController.CancelAllProjects)), HarmonyPrefix]
    private static bool CancelProjects(AIBehaviourController __instance) => Ownership.AiDecides(__instance.GetComponent<ICompany>());

    [HarmonyPatch(typeof(AICompany), nameof(AICompany.BankruptcyWarning)), HarmonyPrefix]
    private static bool BankruptcyWarning(AICompany __instance) => Ownership.AiDecides(__instance);

    [HarmonyPatch(typeof(AICompany), nameof(AICompany.ConsiderGoingFabless)), HarmonyPrefix]
    private static bool ConsiderGoingFabless(AICompany __instance, ref bool __result)
    {
        if (Ownership.AiDecides(__instance)) return true;
        __result = false;
        return false;
    }

    [HarmonyPatch(typeof(AICompany), nameof(AICompany.TriggerBankruptcy)), HarmonyPrefix]
    private static bool TriggerBankruptcy(AICompany __instance) => Ownership.OwnsLocally(__instance);

    // --- Bankrupt players (D57): frozen like a bankrupt AI company; the player stays in the session and watches ---

    [HarmonyPatch(typeof(AICompany), nameof(AICompany.TriggerBankruptcy)), HarmonyPostfix]
    private static void AiBankrupt(AICompany __instance) { if (Ownership.IsGhost(__instance) && __instance.IsBankrupt) Bankruptcy.Freeze(__instance); }

    [HarmonyPatch(typeof(Company), nameof(Company.TriggerBankruptcy)), HarmonyPostfix]
    private static void PlayerBankrupt(Company __instance) => Bankruptcy.Freeze(__instance);

    // The native Game Over offers only "Return To Menu", which would end the session (for everyone on the host).
    [HarmonyPatch(typeof(ProcessorTycoon.Bank.BankruptcyWindow), nameof(ProcessorTycoon.Bank.BankruptcyWindow.Open)), HarmonyPrefix]
    private static bool GameOver() { Bankruptcy.OnLocalBankrupt(); return false; }

    // A frozen company starts nothing new (CPU, research, factory, hardware projects).
    [HarmonyPatch(typeof(TimerScheduler), nameof(TimerScheduler.Schedule)), HarmonyPrefix]
    private static bool Schedule(ITimer timer) => !Bankruptcy.Refuses(timer);

    // AI business deals exclude the player; ghosts are human players too, so AI never signs contracts with them behind
    // their backs (the deal would exist only on the host).
    [HarmonyPatch(typeof(AIBehaviourBusiness), "ContractIsInvalid"), HarmonyPostfix]
    private static void ContractIsInvalid(ICompany provider, ICompany client, ref bool __result)
    {
        if (Ownership.Active && (Ownership.IsGhost(provider) || Ownership.IsGhost(client))) __result = true;
    }

    // --- Shared contract pool (D43): peers forward their offers to the host ---

    // Only real changes are forwarded (contract automation re-offers every day).
    [HarmonyPatch(typeof(Contract), nameof(Contract.AddOffer)), HarmonyPrefix]
    private static void AddOfferBefore(Contract __instance, int cpuID, out bool __state) => __state = __instance.OffersIDs.Contains(cpuID);

    [HarmonyPatch(typeof(Contract), nameof(Contract.AddOffer)), HarmonyPostfix]
    private static void AddOfferAfter(Contract __instance, int cpuID, bool __state)
    {
        if (!__state && __instance.OffersIDs.Contains(cpuID)) Contracts.OnOffer(__instance, cpuID, add: true);
    }

    [HarmonyPatch(typeof(Contract), nameof(Contract.RemoveOffer)), HarmonyPrefix]
    private static void RemoveOfferBefore(Contract __instance, int cpuID, out bool __state) => __state = __instance.OffersIDs.Contains(cpuID);

    [HarmonyPatch(typeof(Contract), nameof(Contract.RemoveOffer)), HarmonyPostfix]
    private static void RemoveOfferAfter(Contract __instance, int cpuID, bool __state)
    {
        if (__state && !__instance.OffersIDs.Contains(cpuID)) Contracts.OnOffer(__instance, cpuID, add: false);
    }

    // --- Business deals (D45) ---

    // The game would let AI logic accept on behalf of another human player: deals between players need consent.
    [HarmonyPatch(typeof(BusinessContractManager), nameof(BusinessContractManager.AddContract)), HarmonyPrefix]
    private static bool BusinessAddAllowed(BusinessContract contract)
    {
        if (!Ownership.Active || contract.Provider == null || contract.Client == null) return true;
        bool human(ICompany c) => c.IsPlayer || Ownership.IsGhost(c);
        if (!(human(contract.Provider) && human(contract.Client))) return true;
        return BusinessDeals.AllowPlayerDeal(contract);
    }

    // With another player as counterparty the AI acceptance score is meaningless: they decide in a prompt.
    [HarmonyPatch(typeof(ContractNegotiationWindow), "UpdateScores"), HarmonyPostfix]
    private static void NegotiationScores(ContractNegotiationWindow __instance)
    {
        if (!Ownership.Active || __instance.currentProvider == null || __instance.currentClient == null) return;
        var other = __instance.currentClient.IsPlayer ? __instance.currentProvider : __instance.currentClient;
        if (!Ownership.IsGhost(other)) return;
        __instance.acceptanceScore = 1;
        // One short line: the label sits right above the Sign button (the proposal arrives as an email, BusinessDeals).
        var label = __instance.acceptanceText;
        if (label == null) return;
        var text = $"{other.Name} decides by email";
        label.text = label.GetPreferredValues(text).x <= label.rectTransform.rect.width ? text : "Player decides by email";
    }

    [HarmonyPatch(typeof(BusinessContractManager), nameof(BusinessContractManager.AddContract)), HarmonyPostfix]
    private static void BusinessAdd(BusinessContract contract) { if (BusinessContractManager.Instance.contracts.Contains(contract)) BusinessDeals.OnAdd(contract); }

    [HarmonyPatch(typeof(BusinessContractManager), nameof(BusinessContractManager.BreakContract)), HarmonyPrefix]
    private static void BusinessBreak(ICompany breaker, BusinessContract contract) => BusinessDeals.OnBreak(breaker, contract);

    [HarmonyPatch(typeof(ArchitectureManager), "HandlePlayerInCrossLicensingAgreement"), HarmonyPostfix]
    private static void CrossLicensing() => Licences.AfterOwnershipCheck();

    // --- IDs (D20): peers allocate from their slot range ---

    [HarmonyPatch(typeof(SaveIDHandler), nameof(SaveIDHandler.NewID)), HarmonyPrefix]
    private static bool NewID(ref int __result)
    {
        if (!Ownership.Active || Ownership.IsHost) return true;
        __result = IdRanges.Next();
        return false;
    }

    // --- Saves (D10): no vanilla saves of a session (they would contain ghosts) ---

    // D52: every save written during a session (snapshots, checkpoints, manual saves) carries the latest projects of the
    // human companies simulated elsewhere. SaveHandler.Save serializes its finished SaveObject with this call.
    [HarmonyPatch(typeof(JsonUtility), nameof(JsonUtility.ToJson), typeof(object), typeof(bool)), HarmonyPrefix]
    private static void SaveProjects(object obj)
    {
        if (obj is SaveObject save && Ownership.Active) Projects.Inject(save);
    }

    [HarmonyPatch(typeof(SaveHandler), nameof(SaveHandler.Autosave)), HarmonyPrefix]
    private static bool Autosave() => !Ownership.Active;

    [HarmonyPatch(typeof(SaveHandler), nameof(SaveHandler.Save)), HarmonyPrefix]
    private static void Save(ref string saveName)
    {
        if (Ownership.Active && !saveName.StartsWith(SaveIO.Folder + "/")) saveName = SaveIO.ManualSaveName(saveName);
    }
}
