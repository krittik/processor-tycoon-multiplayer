using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.Save;

namespace ProcessorTycoonMp.Adapter;

// D20: peers allocate SaveIDs from slot × 10,000,000 upwards; the host keeps the native counter.
internal static class IdRanges
{
    private static int next = -1;

    public static void Reset() => next = -1;

    public static int Next()
    {
        if (next < 0) next = Highest(Ownership.LocalSlot) + 1;
        return next++;
    }

    // After loading a checkpoint written by another machine (host change), continue the native counter above every
    // existing host-range ID so new IDs never collide with ones the previous host handed out.
    public static void RestoreHostCounter()
    {
        int max = Highest(0);
        if (SaveIDHandler.Instance.GeneratedIDs <= max) SaveIDHandler.Instance.LoadID(max);
    }

    public static int Highest(int slot)
    {
        int lo = slot * Ownership.SlotRange, hi = lo + Ownership.SlotRange, max = lo;
        bool In(int id) => id >= lo && id < hi;
        foreach (var c in EntityIO.Companies()) if (In(c.SaveID) && c.SaveID > max) max = c.SaveID;
        foreach (var cpu in DataFinder.FindAllCpus()) if (In(cpu.SaveID) && cpu.SaveID > max) max = cpu.SaveID;
        foreach (var h in CustomHardwareHelper.Instance.GetAllCustomHardwares().Where(h => In(h.SaveID))) if (h.SaveID > max) max = h.SaveID;
        return max;
    }
}
