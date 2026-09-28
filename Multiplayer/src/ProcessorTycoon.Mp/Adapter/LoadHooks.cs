using HarmonyLib;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.Save;

namespace ProcessorTycoonMp.Adapter;

// D42: the only patches installed for the whole game run. They act only on multiplayer data (ghost company ids, a player
// company whose SaveID is not the scene's), so vanilla saves are unaffected, and any save containing ghost companies —
// an MP checkpoint or a save made after leaving a session — always loads; ghosts are then ordinary AI rivals.
[HarmonyPatch]
internal static class LoadHooks
{
    private static Harmony? harmony;

    public static void Install()
    {
        if (harmony != null) return;
        harmony = new Harmony(Hooks.HarmonyId + ".load");
        harmony.CreateClassProcessor(typeof(LoadHooks)).Patch();
    }

    [HarmonyPatch(typeof(CompanySpawner), nameof(CompanySpawner.SpawnCompanyFromSave)), HarmonyPrefix]
    private static bool SpawnCompanyFromSave(CompanySpawner __instance, string uniqueID, int saveID)
    {
        if (Ownership.GhostSlot(uniqueID) < 0) return true;
        Ghosts.Spawn(__instance, uniqueID, saveID);
        return false;
    }

    // The player company is a static scene object with SaveID 0; an MP save may make another company the player.
    [HarmonyPatch(typeof(SaveObjectInstantiator), nameof(SaveObjectInstantiator.UnpackFromSave)), HarmonyPrefix]
    private static void UnpackFromSave(SaveObject.Company savedCompany)
    {
        if (savedCompany.IsPlayer && Player.Instance != null) Player.Instance.Company.SaveID = savedCompany.SaveID;
    }

    // D53: after every load, ghosts keep only technologies they researched (Load clears its events first, so subscribe after).
    [HarmonyPatch(typeof(SaveHandler), nameof(SaveHandler.Load)), HarmonyPostfix]
    private static void Loading(SaveHandler __instance) => __instance.OnLastLoadedTick += Ghosts.ResetAllTechnologies;

    [HarmonyPatch(typeof(AICompany), "Initialize"), HarmonyPostfix]
    private static void GhostInitialized(AICompany __instance)
    {
        if (Ownership.GhostSlot(__instance.UniqueID) >= 0) __instance.gameObject.name = "MP " + __instance.UniqueID;
    }
}
