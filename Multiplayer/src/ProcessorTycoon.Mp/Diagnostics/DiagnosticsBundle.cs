using System;
using System.IO;
using System.IO.Compression;
using BepInEx;

namespace ProcessorTycoonMp.Diagnostics;

// One zip for bug reports: the BepInEx log, the multiplayer config and all desync reports.
internal static class DiagnosticsBundle
{
    public static string Create()
    {
        string dir = Path.Combine(Paths.BepInExRootPath, "mp-reports");
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create);
        AddFile(zip, Path.Combine(Paths.BepInExRootPath, "LogOutput.log"), "LogOutput.log");
        AddFile(zip, Path.Combine(Paths.ConfigPath, "processortycoon.multiplayer.cfg"), "processortycoon.multiplayer.cfg");
        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
            AddFile(zip, file, "mp-reports/" + file.Substring(dir.Length + 1).Replace('\\', '/'));
        return zipPath;
    }

    // The log is open for writing by BepInEx: read it with shared access.
    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        if (!File.Exists(path)) return;
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var target = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        source.CopyTo(target);
    }
}
