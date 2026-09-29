using ProcessorTycoonModApi;

namespace ProcessorTycoonMp.UI;

// The mod's overlay canvas (Mod API): above the game's windows, below the Agent mod's overlay (32760).
internal static class Surface
{
    private static Overlay? overlay;
    public static Overlay Get() => overlay ??= new Overlay("MultiplayerUI", 32600);
    public static bool InMenu => Overlay.InMenu;
}
