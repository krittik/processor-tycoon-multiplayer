using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProcessorTycoon.ContractSystem;
using ProcessorTycoon.Save;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// MP saves (D10) live in Saves/Multiplayer/<session>/, which the vanilla load list never shows (top-level *.txt only).
// They are the game's own save JSON, written and loaded through SaveHandler, so everything the game saves is covered.
internal static class SaveIO
{
    public const string Folder = "Multiplayer";
    public static string SessionId = "local";

    public static string SavesRoot => Path.Combine(Application.persistentDataPath, "Saves");

    public static string Relative(string sessionId, string name) => $"{Folder}/{sessionId}/{name}";

    public static string FullPath(string relative) => Path.Combine(SavesRoot, relative.Replace('/', Path.DirectorySeparatorChar)) + ".txt";

    public static string ManualSaveName(string name) => Relative(SessionId, "manual-" + (string.IsNullOrWhiteSpace(name) ? "save" : name));

    // Saves the live game through the native path and returns the JSON.
    public static string SaveNow(string relative)
    {
        string full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        SaveHandler.Instance.Save(relative);
        return File.ReadAllText(full);
    }

    public static void Write(string relative, string json)
    {
        string full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json);
    }

    // Loads through the native path (scene reload with its transition); `done` runs after the last load step.
    public static void Load(string relative, Action done)
    {
        SaveHandler.Instance.Load(relative + ".txt");
        SaveHandler.Instance.OnLastLoadedTick += done;
    }

    public static void WriteCheckpoint(string sessionId, int slot, DateTime date)
    {
        SaveNow(Relative(sessionId, $"checkpoint-s{slot}-{date:yyyy-MM-dd}"));
        var dir = new DirectoryInfo(Path.Combine(SavesRoot, Folder, sessionId));
        foreach (var old in dir.GetFiles($"checkpoint-s{slot}-*.txt").OrderByDescending(f => f.Name).Skip(3)) old.Delete();
    }

    // Several instances on one PC share the save folder: skip identical content, retry briefly on a sharing violation.
    public static void WriteRecord(string sessionId, string record)
    {
        string path = Path.Combine(SavesRoot, Folder, sessionId, "session.txt");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == record) return;
                File.WriteAllText(path, record);
                return;
            }
            catch (IOException) { System.Threading.Thread.Sleep(20); }
        }
    }

    // Saved sessions this installation can resume: (session id, record text, relative path of our latest checkpoint).
    public static List<(string sessionId, string record, string checkpoint, DateTime written)> Resumable(string clientId)
    {
        var result = new List<(string, string, string, DateTime)>();
        var root = new DirectoryInfo(Path.Combine(SavesRoot, Folder));
        if (!root.Exists) return result;
        foreach (var dir in root.GetDirectories())
        {
            string recordPath = Path.Combine(dir.FullName, "session.txt");
            if (!File.Exists(recordPath)) continue;
            string text = File.ReadAllText(recordPath);
            ProcessorTycoonMp.Core.Session.SessionRecord record;
            try { record = ProcessorTycoonMp.Core.Session.SessionRecord.Parse(text); } catch { continue; }
            var me = record.Players.FirstOrDefault(p => p.ClientId == clientId);
            if (me == null) continue;
            var latest = dir.GetFiles($"checkpoint-s{me.Slot}-*.txt").OrderByDescending(f => f.Name).FirstOrDefault();
            if (latest == null) continue;
            result.Add((record.SessionId, text, Relative(dir.Name, Path.GetFileNameWithoutExtension(latest.Name)), latest.LastWriteTime));
        }
        return result.OrderByDescending(r => r.Item4).ToList();
    }
}
