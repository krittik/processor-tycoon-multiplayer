using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoonMp.Core.Protocol;

namespace ProcessorTycoonMp.Core.Session;

// Roster of a session, stored next to every machine's checkpoints so any player can resume it later (D10).
// Text format: first line "session <id>", then one "slot|companyId|clientId|name" line per player.
public sealed class SessionRecord
{
    public string SessionId = "";
    public List<PlayerInfo> Players = new();

    public string Serialize() =>
        $"session {SessionId}\n" + string.Join("\n", Players.OrderBy(p => p.Slot).Select(p => $"{p.Slot}|{p.CompanyId}|{p.ClientId}|{p.Name}")) + "\n";

    public static SessionRecord Parse(string text)
    {
        var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("session ")) throw new FormatException("not a session record");
        var record = new SessionRecord { SessionId = lines[0].Substring(8).Trim() };
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(new[] { '|' }, 4);
            if (parts.Length < 4) continue;
            record.Players.Add(new PlayerInfo { Slot = int.Parse(parts[0]), CompanyId = int.Parse(parts[1]), ClientId = parts[2], Name = parts[3] });
        }
        return record;
    }
}
