using System;
using System.Linq;

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
    public const string License = "MIT";
    public const string Description = "Up to 8 players, each leading their own company, in one shared world over Steam or the network.";

    public static string Short => $"v{Version} “{Release}”";

    // Credits name the author and collaborators without roles.
    public const string Author = "Critique (Sevastyanoff)";   // nickname (surname)
    public const string AuthorUrl = Discord;   // the author's closest page: their Discord server
    public const string ClaudeUrl = "https://claude.com/claude-code";
    public static readonly (string name, string url)[] Collaborators = { ("Claude Code", ClaudeUrl) };

    // "By Critique (Sevastyanoff), in collaboration with Claude Code.", each name wrapped by link(name, url).
    public static string Byline(Func<string, string, string> link) => $"By {link(Keep(Author), AuthorUrl)}, in collaboration with {string.Join(", ", Collaborators.Select(c => link(Keep(c.name), c.url)))}.";
    private static string Keep(string name) => name.Replace(' ', (char)0xA0);   // a linked name never wraps inside

    public static readonly (string name, string license)[] ThirdParty = { ("BepInEx", "LGPL-2.1"), ("HarmonyX", "MIT"), ("Steamworks.NET", "MIT") };
    public static string BuiltWith => "Built with " + string.Join(", ", ThirdParty.Take(ThirdParty.Length - 1).Select(Part)) + " and " + Part(ThirdParty.Last()) + ".";
    private static string Part((string name, string license) p) => $"{p.name} ({p.license})";

    public const string Disclaimer = "Unofficial fan mod. Processor Tycoon belongs to its developers; this mod is not affiliated with or endorsed by them and ships none of the game's files.";
}
