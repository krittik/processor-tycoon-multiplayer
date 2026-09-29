using ProcessorTycoonModApi;

namespace ProcessorTycoonMp;

// Shown in the About window and next to the version in the Multiplayer window's footer.
internal static class ModInfo
{
    public const string Name = "Processor Tycoon Multiplayer";
    public const string Version = MyPluginInfo.PLUGIN_VERSION;
    public const string Release = "Lockstep";
    public const string Repository = "https://github.com/krittik/processor-tycoon-multiplayer";
    public const string Releases = Repository + "/releases/latest";
    public const string Issues = Repository + "/issues";
    public const string Discord = "https://discord.gg/YKdTjge7J2";

    // Credits name the author and collaborators without roles; each name links to its closest page.
    public static readonly Credits Credits = new()
    {
        Name = Name, Version = Version, Release = Release, License = "MIT",
        Description = "Up to 8 players, each leading their own company, in one shared world over Steam or the network.",
        Repository = Repository, Issues = Issues, Discord = Discord,
        Author = "Critique (Sevastyanoff)", AuthorUrl = Discord,
        Collaborators = new[] { ("Claude Code", Marks.ClaudeUrl) },
        BuiltWith = new[] { ("BepInEx", "LGPL-2.1"), ("HarmonyX", "MIT"), ("Steamworks.NET", "MIT"), ("Processor Tycoon Mod API", "MIT") },
        Disclaimer = "Unofficial fan mod. Processor Tycoon belongs to its developers; this mod is not affiliated with or endorsed by them and ships none of the game's files.",
    };

    public static string Short => Credits.Short;
}
