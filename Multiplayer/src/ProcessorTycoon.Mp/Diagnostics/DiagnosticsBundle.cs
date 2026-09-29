using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace ProcessorTycoonMp.Diagnostics;

// One zip for bug reports (Mod API BugReport): the BepInEx log, the multiplayer config and all desync reports.
internal static class DiagnosticsBundle
{
    public const string Contents = "The game's log (BepInEx/LogOutput.log), the multiplayer settings and any desync reports.";

    public static string Create() => ProcessorTycoonModApi.BugReport.Save("mp-reports", Files());

    private static IEnumerable<string> Files()
    {
        yield return Path.Combine(Paths.ConfigPath, "processortycoon.multiplayer.cfg");
        var reports = Path.Combine(Paths.BepInExRootPath, "mp-reports");
        if (!Directory.Exists(reports)) yield break;
        foreach (var file in Directory.GetFiles(reports, "*.json", SearchOption.AllDirectories)) yield return file;
    }
}
