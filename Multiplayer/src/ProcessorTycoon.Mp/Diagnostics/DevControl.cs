using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.Diagnostics;

// Scripted control for tests (D14, TESTING): active only when <game>/mp-dev/ exists.
// Commands in mp-dev/cmd.txt (consumed): host [address] [name] | join address [name] [type 0|1|2] [company name] | resume [sessionId] [address] | kick slot | deal accept|decline | leave | chat text | speed 0..3 |
// load <save file name> | panel on|off | credits | write [text] (opens the chat input) | preview-bankrupt | preview-deal | close-ui | debug-money <cash> | native-pause | uidump <name filter> | quit. Status is written to mp-dev/status.json every second.
internal sealed class DevControl
{
    private readonly MpRuntime runtime;
    private readonly string dir;
    private float nextPoll, nextStatus;

    public DevControl(MpRuntime runtime, string gameRoot)
    {
        this.runtime = runtime;
        dir = Path.Combine(gameRoot, "mp-dev");
    }

    public bool Enabled => Directory.Exists(dir);
    public Action<bool>? ShowPanel;
    public Action? ShowCredits;
    public Action? CloseUi;
    public Action<string>? WriteChat;

    public void Update(string defaultName)
    {
        float now = Time.unscaledTime;
        if (now < nextPoll) return;
        nextPoll = now + 0.25f;
        if (!Enabled) return;
        string cmdFile = Path.Combine(dir, "cmd.txt");
        if (File.Exists(cmdFile))
        {
            string[] lines;
            try { lines = File.ReadAllLines(cmdFile); File.Delete(cmdFile); }
            catch (IOException) { return; }
            foreach (var line in lines.Where(l => l.Trim().Length > 0)) Execute(line.Trim(), defaultName);
        }
        if (now >= nextStatus)
        {
            nextStatus = now + 1f;
            try { File.WriteAllText(Path.Combine(dir, "status.json"), Status()); } catch (IOException) { }
        }
    }

    private void Execute(string line, string defaultName)
    {
        runtime.Log("MP dev: " + line);
        var parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        string Arg(int i, string fallback) => parts.Length > i ? parts[i] : fallback;
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "host": runtime.Host(Arg(1, ""), Arg(2, defaultName)); break;
                case "join":
                {
                    // join <address> [name] [company type 0 CPU|1 fabless|2 foundry] [company name...]
                    var rest = Arg(2, defaultName).Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                    string name = rest.Length > 0 ? rest[0] : defaultName;
                    int type = rest.Length > 1 && int.TryParse(rest[1], out int t) ? t : 0;
                    string company = rest.Length > 2 ? rest[2] : name;
                    runtime.Join(Arg(1, "127.0.0.1"), name, rules => UI.JoinSetup.Auto(rules, company, name, type));
                    break;
                }
                case "join-ui": runtime.Join(Arg(1, "127.0.0.1"), Arg(2, defaultName)); break;   // company set up on the native screen
                case "credits": ShowCredits?.Invoke(); break;
                case "native-pause": DateController.Instance.ResumeAndOverride(0); break;   // what closing the Default warning does (D54 test)
                case "debug-money": ProcessorTycoon.Player.Instance.Company.MoneyAmount = float.Parse(Arg(1, "0"), CultureInfo.InvariantCulture); break;   // tests only
                case "close-ui": CloseUi?.Invoke(); break;
                case "preview-bankrupt": Adapter.Bankruptcy.OnLocalBankrupt(); break;
                case "preview-deal":
                {
                    var other = runtime.Session?.Players.FirstOrDefault(p => p.Slot != runtime.Session.LocalSlot && p.CompanyId >= 0);
                    if (other != null) Adapter.BusinessDeals.Preview(other.Slot, other.CompanyId);
                    break;
                }
                case "leave": runtime.Leave(); break;
                case "steam": Steam.SteamGate.TryInit(force: true); break;
                case "relay": Steam.SteamGate.SetForceRelay(Arg(1, "on") == "on"); break;
                case "route": runtime.Log("MP dev: Steam connection details\n" + Steam.SteamGate.DetailedRoutes(runtime.Transport)); break;
                case "resume": runtime.Resume(Arg(1, ""), Arg(2, ""), defaultName); break;
                case "kick": if (runtime.Session is HostSession host) host.Kick(int.Parse(Arg(1, "-1")), "kicked by the host"); break;
                case "deal": if (Adapter.BusinessDeals.Pending.Count > 0) Adapter.BusinessDeals.Answer(Adapter.BusinessDeals.Pending[0], Arg(1, "") == "accept"); break;
                case "chat": runtime.SendChat(line.Substring(4)); break;
                case "write": WriteChat?.Invoke(line.Length > 6 ? line.Substring(6) : ""); break;
                case "speed": DateController.Instance.ManualSetTimeSpeed(int.Parse(Arg(1, "1"), CultureInfo.InvariantCulture), playsound: false); break;
                case "load": SaveHandler.Instance.Load(line.Substring(5).Trim()); break;
                case "diagnostics": runtime.Log("MP dev: diagnostics saved to " + DiagnosticsBundle.Create()); break;
                case "panel": ShowPanel?.Invoke(Arg(1, "on") == "on"); break;
                case "uidump": runtime.Log("MP dev: UI dump written to " + UiDump.Write(dir, Arg(1, "Window"))); break;
                case "quit": Application.Quit(); break;
                default: runtime.Log("MP dev: unknown command"); break;
            }
        }
        catch (Exception e) { runtime.Log("MP dev: command failed: " + e.Message); }
    }

    private string Status()
    {
        var s = runtime.Session;
        var sb = new StringBuilder("{");
        void Field(string name, string rawValue) => sb.Append(sb.Length > 1 ? "," : "").Append(Json.Quote(name)).Append(':').Append(rawValue);
        Field("time", Json.Quote(DateTime.Now.ToString("HH:mm:ss")));
        Field("campaign", GameWorld.CampaignLoaded ? "true" : "false");
        if (GameWorld.CampaignLoaded)
        {
            Field("date", Json.Quote(DateController.Instance.CurrentDate.ToString("yyyy-MM-dd")));
            Field("day", runtime.World.Day.ToString());
            Field("speed", DateController.Instance.currentTimeSpeed.ToString());
        }
        Field("session", s == null ? "null" : Json.Quote(s.State.ToString()));
        if (s != null)
        {
            Field("host", s.IsHost ? "true" : "false");
            Field("slot", s.LocalSlot.ToString());
            Field("sessionId", Json.Quote(s.SessionId));
            Field("waiting", s is HostSession h && h.WaitingForPeers ? "true" : "false");
            if (s is HostSession hw) Field("waitingFor", Json.Quote(hw.WaitingFor));
            Field("lastCheckpointDay", s.LastCheckpointDay.ToString());
            Field("lastCheckpointHash", Json.Quote(s.LastCheckpointHash.ToString("x16")));
            Field("resyncs", s.Resyncs.ToString());
            Field("driftRepairs", s.DriftRepairs.ToString());
            if (s is PeerSession p) Field("lastCheckpointResult", Json.Quote(p.LastCheckpointResult));
            Field("players", "[" + string.Join(",", s.Players.Select(x => Json.Quote(x.ToString()))) + "]");
        }
        var st = runtime.World.Stats;
        Field("tickMs", st.TickMs.ToString("0.0", CultureInfo.InvariantCulture));
        Field("captureMs", st.CaptureMs.ToString("0.0", CultureInfo.InvariantCulture));
        Field("applyMs", st.ApplyMs.ToString("0.0", CultureInfo.InvariantCulture));
        Field("appliedDeltas", st.AppliedDeltas.ToString());
        Field("appliedKiB", (st.AppliedBytes / 1024).ToString());
        Field("profile", "{" + string.Join(",", EntityIO.Profile.OrderBy(kv => kv.Key).Select(kv => Json.Quote(kv.Key) + ":" + kv.Value.ToString("0", CultureInfo.InvariantCulture))) + "}");
        if (EntityIO.SalesOwnerTotal > 0) Field("salesDriftPercent", (100 * EntityIO.SalesAbsDiff / EntityIO.SalesOwnerTotal).ToString("0.0", CultureInfo.InvariantCulture));
        Field("dealProposals", Adapter.BusinessDeals.Pending.Count.ToString());
        Field("steam", Steam.SteamGate.Ready
            ? "{\"ready\":true,\"forceRelay\":" + (Steam.SteamGate.ForceRelay ? "true" : "false") + ",\"routes\":[" + string.Join(",", Steam.SteamGate.Routes(runtime.Transport).Select(Json.Quote)) + "],\"id\":" + Json.Quote(Steam.SteamGate.LocalId.ToString()) + ",\"name\":" + Json.Quote(Steam.SteamGate.LocalName) + ",\"lobby\":" + (Steam.SteamGate.LobbyOpen ? "true" : "false")
              + ",\"friendsHosting\":[" + string.Join(",", Steam.SteamGate.FriendsHosting().Select(f => Json.Quote(f.Name + " " + Steam.SteamGate.AddressOf(f.SteamId) + " " + f.Version))) + "]}"
            : "{\"ready\":false,\"error\":" + Json.Quote(Steam.SteamGate.Error) + "}");
        Field("error", Json.Quote(runtime.LastError));
        Field("recent", "[" + string.Join(",", runtime.Recent.Skip(Math.Max(0, runtime.Recent.Count - 12)).Select(Json.Quote)) + "]");
        return sb.Append('}').ToString();
    }
}
