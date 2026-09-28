using System;
using System.Linq;

namespace ProcessorTycoonMp;

// Shown in the credits popup and next to the version (non-invasive: the multiplayer window's footer and the tray tooltip).
internal static class ModInfo
{
    public const string Name = "Processor Tycoon Multiplayer";
    public const string Version = MyPluginInfo.PLUGIN_VERSION;
    public const string Release = "Lockstep";
    public const string Repository = "https://github.com/krittik/processor-tycoon-multiplayer";
    public const string Releases = Repository + "/releases/latest";
    public const string Issues = Repository + "/issues";
    public const string Discord = "https://discord.gg/YKdTjge7J2";
    public const string License = "MIT";

    public static string Short => $"v{Version} “{Release}”";

    // Credits name the author and collaborators without roles.
    public const string Author = "Critique (Sevastyanoff)";   // nickname (surname)
    public const string AuthorUrl = Discord;   // the author's closest page: their Discord server
    public static readonly (string name, string url)[] Collaborators = { ("Claude Code (Anthropic)", "https://claude.com/claude-code") };

    // "By Critique, in collaboration with Claude Code (Anthropic)", each name wrapped by link(name, url).
    public static string Byline(Func<string, string, string> link) => $"By {link(Keep(Author), AuthorUrl)}, in collaboration with {string.Join(", ", Collaborators.Select(c => link(Keep(c.name), c.url)))}.";
    private static string Keep(string name) => name.Replace(' ', (char)0xA0);   // a linked name never wraps inside

    public static readonly (string name, string note)[] ThirdParty =
    {
        ("BepInEx", "plugin loader, LGPL-2.1"),
        ("HarmonyX", "runtime patching, MIT"),
        ("Steamworks.NET", "Steam networking bindings, MIT"),
    };

    public const string Disclaimer = "Unofficial fan mod. Processor Tycoon belongs to its developers; this mod is not affiliated with or endorsed by them and ships none of the game's files.";
}
