using ProcessorTycoonMp.Core;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;
using ProcessorTycoonMp.Core.Transport;
using ProcessorTycoonMp.Tests;

var failures = 0;
var total = 0;
bool verbose = args.Contains("-v");

CompanySetup Auto(SessionRules rules) => new() { Name = "Test Co", CompanyType = 1, StartingFunds = 2_500_000, FactorySize = 0 };

void Check(string name, bool ok, string detail = "")
{
    total++;
    if (ok) return;
    failures++;
    Console.WriteLine($"FAIL {name}{(detail.Length > 0 ? ": " + detail : "")}");
}

Check("protocol version is positive", MpProtocol.Version > 0);
Check("plugin guid is fixed", MpProtocol.PluginGuid == "processortycoon.multiplayer");

// --- Json ---
{
    string a = "{\"<Name>k__BackingField\":\"A \\\"quoted\\\" {x}\",\"N\":1.5,\"Obj\":{\"a\":[1,2,{\"b\":\"]\"}],\"c\":true},\"L\":[1,2],\"E\":{}}";
    var m = Json.Members(a);
    Check("json members count", m.Count == 5, string.Join(",", m.Select(x => x.Key)));
    Check("json string with braces and escaped quotes", m[0].Value == "\"A \\\"quoted\\\" {x}\"");
    Check("json nested value raw", m[2].Value == "{\"a\":[1,2,{\"b\":\"]\"}],\"c\":true}");
    Check("json empty object", Json.Members("{ }").Count == 0 && m[4].Value == "{}");
    string b = "{\"<Name>k__BackingField\":\"A \\\"quoted\\\" {x}\",\"N\":2,\"Obj\":{\"a\":[1,2,{\"b\":\"]\"}],\"c\":false},\"L\":[1,2],\"E\":{}}";
    string? d = Json.Diff(a, b);
    Check("json diff nested", d == "{\"N\":2,\"Obj\":{\"c\":false}}", d ?? "null");
    Check("json diff equal is null", Json.Diff(a, a) == null);
    Check("json merge reproduces", Json.Merge(a, d!) == b, Json.Merge(a, d!));
    Check("json merge replaces arrays", Json.Merge("{\"L\":[1,2,3]}", "{\"L\":[9]}") == "{\"L\":[9]}");
    Check("json without", Json.Without(a, new[] { "N", "L" }) == "{\"<Name>k__BackingField\":\"A \\\"quoted\\\" {x}\",\"Obj\":{\"a\":[1,2,{\"b\":\"]\"}],\"c\":true},\"E\":{}}");
    Check("json get", Json.Get(a, "N") == "1.5");
    Check("json quote", Json.Quote("a\"b\\c\n") == "\"a\\\"b\\\\c\\n\"");
}

// --- Delta tracker + hashing ---
{
    var t = new DeltaTracker();
    var first = t.Produce(new[] { new EntityState(EntityKind.Cpu, 5, "{\"P\":1,\"M\":{\"S\":1}}") });
    Check("tracker new entity upsert", first.Count == 1 && first[0].Op == DeltaOp.Upsert);
    var second = t.Produce(new[] { new EntityState(EntityKind.Cpu, 5, "{\"P\":1,\"M\":{\"S\":2}}") });
    Check("tracker patch", second.Count == 1 && second[0].Op == DeltaOp.Patch && second[0].Json == "{\"M\":{\"S\":2}}", second.FirstOrDefault()?.Json ?? "");
    Check("tracker unchanged", t.Produce(new[] { new EntityState(EntityKind.Cpu, 5, "{\"P\":1,\"M\":{\"S\":2}}") }).Count == 0);
    var third = t.Produce(Array.Empty<EntityState>());
    Check("tracker remove", third.Count == 1 && third[0].Op == DeltaOp.Remove);
    var t2 = new DeltaTracker();
    t2.Produce(new[] { new EntityState(EntityKind.Contract, 1, "{}"), new EntityState(EntityKind.Contract, 2, "{}") });
    var fullAfterRemoval = t2.Produce(new[] { new EntityState(EntityKind.Contract, 1, "{}") }, full: true);
    Check("full resend keeps removals", fullAfterRemoval.Count == 2 && fullAfterRemoval.Any(d => d.Id == 1 && d.Op == DeltaOp.Upsert) && fullAfterRemoval.Any(d => d.Id == 2 && d.Op == DeltaOp.Remove));
    var h1 = Hashing.World(new[] { new EntityState(EntityKind.Cpu, 1, "{}"), new EntityState(EntityKind.Company, 2, "{\"a\":1}") });
    var h2 = Hashing.World(new[] { new EntityState(EntityKind.Company, 2, "{\"a\":1}"), new EntityState(EntityKind.Cpu, 1, "{}") });
    Check("world hash order independent", h1 == h2);
    Check("world hash detects change", h1 != Hashing.World(new[] { new EntityState(EntityKind.Company, 2, "{\"a\":2}"), new EntityState(EntityKind.Cpu, 1, "{}") }));
    Check("fnv known value", Hashing.Fnv("") == Hashing.Offset);
}

// --- Codec ---
{
    var bundle = new DayBundle { Day = 42, Speed = 2, Checkpoint = true, Seq = 7 };
    bundle.Slots.Add(new SlotDeltas { Slot = 1, Deltas = { new EntityDelta(EntityKind.Company, 10_000_000, DeltaOp.Patch, "{\"Money\":5}") } });
    var back = (DayBundle)Message.Decode(bundle.Encode());
    Check("codec day bundle", back.Day == 42 && back.Speed == 2 && back.Checkpoint && back.Seq == 7 && back.Slots[0].Deltas[0].Json == "{\"Money\":5}" && back.Slots[0].Deltas[0].Id == 10_000_000);
    var hello = new Hello { PlayerName = "Bob", ClientId = "c1", Version = new VersionInfo { ModVersion = "1", GameVersion = "g", AssemblyHash = "h", Mods = "x@1" } };
    var hb = (Hello)Message.Decode(hello.Encode());
    Check("codec hello", hb.PlayerName == "Bob" && hb.Version.Mismatch(hello.Version) == null);
    Check("version mismatch reported", new VersionInfo { ModVersion = "1" }.Mismatch(new VersionInfo { ModVersion = "2" }) != null);
    var ch = new CheckpointHash { Day = 3, Hash = 99, Entities = { [5] = 6 } };
    var chb = (CheckpointHash)Message.Decode(ch.Encode());
    Check("codec checkpoint hash", chb.Hash == 99 && chb.Entities[5] == 6);
    string big = new string('x', 200_000);
    var snap = (Snapshot)Message.Decode(new Snapshot { Day = 1, World = Compression.DeflateText(big) }.Encode());
    Check("codec snapshot compressed", Compression.InflateText(snap.World) == big && snap.World.Length < 10_000);
}

// --- Lag window: a slow peer (every third frame) trails by at most K+1 days and still agrees at checkpoints ---
{
    var hub = new LoopbackHub();
    var hw = new FakeWorld(isHost: true) { Speed = 4 };
    var host = new HostSession(new LoopbackTransport(hub), hw, "Host") { LagWindow = 2 };
    host.Start("loop", 0);
    var pw = new FakeWorld(isHost: false);
    var peer = new PeerSession(new LoopbackTransport(hub), pw, "Slow", "slow") { AutoCompany = Auto };
    peer.Connect("loop");
    int maxLead = 0;
    for (int frame = 0; frame < 900; frame++)
    {
        host.Update(1f);
        if (frame % 3 == 0) peer.Update(1f);
        if (peer.State == SessionState.Running) maxLead = Math.Max(maxLead, hw.Day - pw.Day);
    }
    Check("lag window bounds the lead", maxLead <= 3 && maxLead >= 2, maxLead.ToString());
    Check("lag window keeps checkpoints", host.Resyncs == 0 && hw.Checkpoints.Count >= 10 && pw.Checkpoints.Count >= 10, $"{host.Resyncs} {hw.Checkpoints.Count} {pw.Checkpoints.Count}");
}

// --- Keepalive: a silent peer is dropped after the timeout and its company goes offline ---
{
    var hub = new LoopbackHub();
    var hostWorldForCommands = new FakeWorld(isHost: true) { Speed = 0 };
    var host = new HostSession(new LoopbackTransport(hub), hostWorldForCommands, "Host");
    host.Start("loop", 0);
    var peerWorldForCommands = new FakeWorld(isHost: false);
    var peer = new PeerSession(new LoopbackTransport(hub), peerWorldForCommands, "Quiet", "quiet") { AutoCompany = Auto };
    peer.Connect("loop");
    for (int i = 0; i < 20; i++) { host.Update(1f); peer.Update(1f); }
    Check("idle session stays connected", host.Players.First(p => p.Slot == 1).Connected && peer.State == SessionState.Running);
    peerWorldForCommands.Outgoing.Add(("contract-offer", "7|42"));
    for (int i = 0; i < 3; i++) { host.Update(1f); peer.Update(1f); }
    Check("peer command reaches the host", hostWorldForCommands.Executed.Contains("1:contract-offer:7|42"), string.Join(";", hostWorldForCommands.Executed));
    for (int i = 0; i < 60; i++) host.Update(1f);
    Check("silent peer dropped", !host.Players.First(p => p.Slot == 1).Connected);
    peer.Update(1f);
    Check("dropped peer notices", peer.State == SessionState.Closed);
}

// --- A player's action between the last sent delta and a checkpoint does not cause a mismatch ---
{
    var hub = new LoopbackHub();
    var hw = new FakeWorld(isHost: true);
    var host = new HostSession(new LoopbackTransport(hub), hw, "Host") { LagWindow = 0 };
    host.Start("loop", 0);
    var pw = new FakeWorld(isHost: false);
    var peer = new PeerSession(new LoopbackTransport(hub), pw, "Actor", "actor") { AutoCompany = Auto };
    peer.Connect("loop");
    for (int i = 0; i < 5; i++) { host.Update(1f); peer.Update(1f); }
    for (int guard = 0; guard < 100 && !hw.IsCheckpointDay(hw.Day + 1); guard++) { host.Update(1f); peer.Update(1f); }
    pw.Corrupt(10_000_000, "{\"Name\":\"Renamed between frames\"}");   // like a UI action after the delta was sent
    for (int i = 0; i < 4; i++) { host.Update(1f); peer.Update(1f); }
    Check("action before a checkpoint is not a mismatch", host.Resyncs == 0 && hw.Checkpoints.Count >= 1, $"resyncs {host.Resyncs}, checkpoints {hw.Checkpoints.Count}");
}

// --- Mod data channels: a peer's message reaches the host and the other peers, tagged with the sender's slot ---
{
    var hub = new LoopbackHub();
    var host = new HostSession(new LoopbackTransport(hub), new FakeWorld(isHost: true) { Speed = 0 }, "Host");
    host.Start("loop", 0);
    var a = new PeerSession(new LoopbackTransport(hub), new FakeWorld(isHost: false), "A", "a") { AutoCompany = Auto };
    var b = new PeerSession(new LoopbackTransport(hub), new FakeWorld(isHost: false), "B", "b") { AutoCompany = Auto };
    a.Connect("loop");
    for (int i = 0; i < 3; i++) { host.Update(1f); a.Update(1f); }
    b.Connect("loop");
    for (int i = 0; i < 3; i++) { host.Update(1f); a.Update(1f); b.Update(1f); }
    var got = new List<string>();
    host.ChannelReceived += (n, s, d) => got.Add($"host:{n}:{s}:{d.Length}");
    b.ChannelReceived += (n, s, d) => got.Add($"b:{n}:{s}:{d.Length}");
    a.ChannelReceived += (n, s, d) => got.Add($"a:{n}:{s}:{d.Length}");
    a.SendChannel("mod.x", new byte[] { 1, 2, 3 });
    for (int i = 0; i < 3; i++) { host.Update(1f); a.Update(1f); b.Update(1f); }
    Check("channel reaches host and other peer only", got.Contains("host:mod.x:1:3") && got.Contains("b:mod.x:1:3") && !got.Any(g => g.StartsWith("a:")), string.Join(";", got));
}

// --- Session record and resume with a host change ---
{
    var record = new SessionRecord { SessionId = "abc", Players = { new PlayerInfo { Slot = 0, Name = "Hosty", CompanyId = 0, ClientId = "h" }, new PlayerInfo { Slot = 1, Name = "Al|ice", CompanyId = 10_000_000, ClientId = "a" } } };
    var parsed = SessionRecord.Parse(record.Serialize());
    Check("record round trip", parsed.SessionId == "abc" && parsed.Players.Count == 2 && parsed.Players[1].Name == "Al|ice" && parsed.Players[1].CompanyId == 10_000_000);
    var resumed = new HostSession(new LoopbackTransport(new LoopbackHub()), new FakeWorld(isHost: true), "Alice", "a", parsed);
    var alice = resumed.Players.First(p => p.ClientId == "a");
    var oldHost = resumed.Players.First(p => p.ClientId == "h");
    Check("resuming player becomes slot 0", alice.Slot == 0 && alice.CompanyId == 10_000_000 && alice.Connected);
    Check("previous host takes the freed slot", oldHost.Slot == 1 && oldHost.CompanyId == 0 && !oldHost.Connected);
    Check("resumed session keeps its id", resumed.SessionId == "abc");
}

// --- TCP transport echo ---
{
    int port = 27990 + Environment.ProcessId % 7;
    using var server = new TcpTransport();
    using var client = new TcpTransport();
    server.Host($"127.0.0.1:{port}");
    client.Connect($"127.0.0.1:{port}");
    var deadline = DateTime.UtcNow.AddSeconds(5);
    bool serverConnected = false, clientConnected = false, echoed = false;
    byte[] payload = new byte[300_000];
    new Random(1).NextBytes(payload);
    while (DateTime.UtcNow < deadline && !echoed)
    {
        while (server.Poll(out var e))
        {
            if (e.Kind == NetEventKind.Connected) serverConnected = true;
            if (e.Kind == NetEventKind.Data) server.Send(e.Peer, e.Data!);
        }
        while (client.Poll(out var e))
        {
            if (e.Kind == NetEventKind.Connected) { clientConnected = true; client.Send(0, payload); }
            if (e.Kind == NetEventKind.Data) echoed = e.Data!.SequenceEqual(payload);
        }
        Thread.Sleep(5);
    }
    Check("tcp connect", serverConnected && clientConnected);
    Check("tcp echo 300 KB", echoed);
}

// --- Sessions over loopback: 2 years, 2 peers (one joins late), drift repair, forced mismatch and resync ---
{
    var hub = new LoopbackHub();
    var hostWorld = new FakeWorld(isHost: true) { Verbose = verbose };
    var host = new HostSession(new LoopbackTransport(hub), hostWorld, "Host");
    host.Start("loop", 0);
    var w1 = new FakeWorld(isHost: false) { Verbose = verbose };
    var p1 = new PeerSession(new LoopbackTransport(hub), w1, "Alice", "alice") { AutoCompany = Auto };
    p1.Connect("loop");
    var w2 = new FakeWorld(isHost: false) { Verbose = verbose };
    PeerSession? p2 = null;
    var chats = new List<string>();
    host.ChatReceived += (s, t, to) => chats.Add($"{s}:{t}:{to}");
    var bobHeard = new List<string>();

    void Pump(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            host.Update(1f);
            p1.Update(1f);
            p2?.Update(1f);
        }
    }

    Pump(3);
    Check("peer 1 running", p1.State == SessionState.Running && p1.LocalSlot == 1, p1.State + " " + p1.CloseReason);
    Pump(200);
    p2 = new PeerSession(new LoopbackTransport(hub), w2, "Bob", "bob") { AutoCompany = Auto };
    p2.Connect("loop");
    Pump(3);
    Check("peer 2 running", p2.State == SessionState.Running && p2.LocalSlot == 2);
    p2.ChatReceived += (s, t, to) => bobHeard.Add($"{s}:{t}:{to}");
    Check("roster has 3 players", host.Players.Count == 3 && p1.Players.Count == 3);
    p1.SendChat("hi");
    Pump(2);
    Check("chat reaches host", chats.Contains("1:hi:-1") && bobHeard.Contains("1:hi:-1"));
    p1.SendChat("psst", 2);
    host.SendChat("to alice", 1);
    Pump(2);
    Check("private message reaches only its recipient", bobHeard.Contains("1:psst:2") && !chats.Any(c => c.Contains("psst")) && !bobHeard.Any(c => c.Contains("to alice")), string.Join(",", bobHeard));

    // Forced mismatch: the host's applier "forgets" a field Alice's company changes.
    hostWorld.BugEntity = 10_000_000;
    w1.Corrupt(10_000_000, "{\"Bug\":7}");
    Pump(40);
    Check("mismatch detected, both peers resynced", host.Resyncs >= 2 && p1.Resyncs >= 1 && p2.Resyncs >= 1, $"host {host.Resyncs} p1 {p1.Resyncs} p2 {p2.Resyncs}");
    Check("owner's state survives the resync", Json.Get(w1.JsonOf(EntityKind.Company, 10_000_000), "Bug") == "7" && w1.Restored > 0, w1.JsonOf(EntityKind.Company, 10_000_000));
    hostWorld.BugEntity = -1;
    Pump(70);
    int settled = host.Resyncs;
    Check("host converges to the owner's state", Json.Get(hostWorld.JsonOf(EntityKind.Company, 10_000_000), "Bug") == "7", hostWorld.JsonOf(EntityKind.Company, 10_000_000));
    Pump(730 - 313);

    Check("host simulated ~2 years", hostWorld.Day >= 700, hostWorld.Day.ToString());
    Check("peers on the host's day", Math.Abs(w1.Day - hostWorld.Day) <= 1 && Math.Abs(w2.Day - hostWorld.Day) <= 1, $"{hostWorld.Day} {w1.Day} {w2.Day}");
    Check("drift repaired at checkpoints", host.DriftRepairs > 0 && p1.DriftRepairs > 0);
    Check("checkpoints agreed on all machines", hostWorld.Checkpoints.Count >= 20 && w1.Checkpoints.Count >= 20 && w2.Checkpoints.Count >= 15,
        $"{hostWorld.Checkpoints.Count} {w1.Checkpoints.Count} {w2.Checkpoints.Count}");
    Check("last checkpoint hashes equal", host.LastCheckpointHash == p1.LastCheckpointHash && host.LastCheckpointHash == p2.LastCheckpointHash);
    Check("no further resyncs", host.Resyncs == settled, $"{host.Resyncs} vs {settled}");
    Check("peer cpu created and replicated", w2.CaptureReplicated().Any(e => e.Id == 10_000_000 + 700) && hostWorld.CaptureReplicated().Any(e => e.Id == 20_000_000 + 700));
    Check("alice sees bob's company", w1.CaptureReplicated().Any(e => e.Kind == EntityKind.Company && e.Id == 20_000_000));
    // Remote companies are one day stale (K + 1 with K = 0): Bob sees Alice's money as of the end of the previous day.
    string bobView = Json.Get(w2.JsonOf(EntityKind.Company, 10_000_000), "Money")!;
    string expected = unchecked(1000 + (w2.Day - 1) * 10_000_001).ToString();
    Check("bob sees alice's money one day late", bobView == expected, $"{bobView} vs {expected}");

    // Leave and rejoin with the same client id keeps the slot.
    p2.Leave("bye");
    Pump(5);
    Check("host marks bob offline", host.Players.First(p => p.Slot == 2).Connected == false);
    var bobAway = host.Players.First(p => p.Slot == 2);
    Check("caretaker starts at the disconnect", bobAway.OfflineSince > 0 && !bobAway.AiControl && p1.Players.First(p => p.Slot == 2).OfflineSince == bobAway.OfflineSince, bobAway.ToString());
    host.CaretakerDays = 20;
    Pump(25);
    Check("AI takes over after the caretaker period", host.Players.First(p => p.Slot == 2).AiControl && p1.Players.First(p => p.Slot == 2).AiControl);
    Check("clock continues without bob", hostWorld.Day > w2.Day + 3);
    var p2b = new PeerSession(new LoopbackTransport(hub), w2, "Bob", "bob") { AutoCompany = Auto };
    p2 = p2b;
    p2b.Connect("loop");
    Pump(5);
    Check("bob rejoins same slot", p2b.State == SessionState.Running && p2b.LocalSlot == 2 && host.Players.First(p => p.Slot == 2).Connected);
    Check("rejoin clears caretaker state", host.Players.First(p => p.Slot == 2) is { OfflineSince: -1, AiControl: false });

    // Version mismatch is rejected.
    var w3 = new FakeWorld(isHost: false) { Version = new VersionInfo { ModVersion = "other", GameVersion = "g", AssemblyHash = "h" } };
    var p3 = new PeerSession(new LoopbackTransport(hub), w3, "Eve", "eve") { AutoCompany = Auto };
    p3.Connect("loop");
    for (int i = 0; i < 3; i++) { host.Update(1f); p3.Update(1f); }
    Check("version mismatch rejected", p3.State == SessionState.Closed && p3.CloseReason.Contains("Version mismatch"), p3.State + " " + p3.CloseReason);
}

// --- Company setup (D58): the company exists only after the player confirms; leaving during setup frees the slot ---
{
    var hub = new LoopbackHub();
    var hw = new FakeWorld(isHost: true);
    var host = new HostSession(new LoopbackTransport(hub), hw, "Host");
    host.Start("loop", 0);
    var pw = new FakeWorld(isHost: false);
    var peer = new PeerSession(new LoopbackTransport(hub), pw, "Cara", "cara");
    SessionRules? asked = null;
    peer.CompanySetupRequested += r => asked = r;
    peer.Connect("loop");
    for (int i = 0; i < 3; i++) { host.Update(1f); peer.Update(1f); }
    Check("new player is asked to set up a company", asked is { Difficulty: 3, Year: 1980 } && peer.State == SessionState.Handshake && hw.Founded.Count == 0);
    Check("slot held during setup", host.Players.Any(x => x.Slot == 1 && x.CompanyId == -1));
    peer.SubmitCompany(new CompanySetup { Name = "Cara Chips", CompanyType = 2 });
    for (int i = 0; i < 4; i++) { host.Update(1f); peer.Update(1f); }
    Check("company founded on confirm", hw.Founded.SequenceEqual(new[] { "1:Cara Chips" }) && peer.State == SessionState.Running && host.Players.First(x => x.Slot == 1).CompanyId == 10_000_000);
    var pw2 = new FakeWorld(isHost: false);
    var quitter = new PeerSession(new LoopbackTransport(hub), pw2, "Dan", "dan");
    quitter.Connect("loop");
    for (int i = 0; i < 3; i++) { host.Update(1f); peer.Update(1f); quitter.Update(1f); }
    bool held = host.Players.Any(x => x.Slot == 2);
    quitter.Leave("changed my mind");
    for (int i = 0; i < 3; i++) { host.Update(1f); peer.Update(1f); }
    Check("leaving during setup frees the slot", held && host.Players.All(x => x.Slot != 2) && hw.Founded.Count == 1);
    var codec = (Welcome)Message.Decode(new Welcome { SessionId = "s", Slot = 1, NeedsCompany = true, Rules = new SessionRules { Difficulty = 5, Year = 2001, Cheats = true } }.Encode());
    var setupBack = (CompanySetup)Message.Decode(new CompanySetup { Name = "N", Founder = "F", Color = "#123456", CompanyType = 1, StartingFunds = 12_000_000_000, FactorySize = 3, StartingTechnology = 4 }.Encode());
    Check("codec welcome rules and company setup", codec.NeedsCompany && codec.Rules.Year == 2001 && codec.Rules.Cheats && setupBack.StartingFunds == 12_000_000_000 && setupBack.Color == "#123456" && setupBack.StartingTechnology == 4);
}

// --- Ownership moves: forgotten entities are not reported removed ---
{
    var t = new DeltaTracker();
    t.Produce(new[] { new EntityState(EntityKind.Company, 1, "{}"), new EntityState(EntityKind.Company, 2, "{}") });
    t.Forget(k => (int)k == 2);
    var after = t.Produce(new[] { new EntityState(EntityKind.Company, 1, "{}") });
    Check("forgotten entity not removed", after.Count == 0);
    var codec = (Roster)Message.Decode(new Roster { Players = { new PlayerInfo { Slot = 3, Name = "X", OfflineSince = 77, AiControl = true } } }.Encode());
    Check("codec roster caretaker fields", codec.Players[0].OfflineSince == 77 && codec.Players[0].AiControl);
}

// --- Carried entities (D52): always whole, nothing when unchanged ---
{
    var t = new DeltaTracker();
    t.Produce(new[] { new EntityState(EntityKind.Projects, 7, "{\"Cpu\":[{\"Progress\":0.1}]}") });
    var changed = t.Produce(new[] { new EntityState(EntityKind.Projects, 7, "{\"Cpu\":[{\"Progress\":0.2}]}") });
    var same = t.Produce(new[] { new EntityState(EntityKind.Projects, 7, "{\"Cpu\":[{\"Progress\":0.2}]}") });
    Check("carried entity sent whole", changed.Count == 1 && changed[0].Op == DeltaOp.Upsert && changed[0].Json.Contains("0.2") && same.Count == 0);
}

// --- Fragments (Steam message limit, D50) ---
{
    var assembler = new FragmentAssembler(1 << 20);
    var sizes = new[] { 0, 1, 7, 8, 9, 100 };
    bool ok = true;
    foreach (int size in sizes)
    {
        var message = Enumerable.Range(0, size).Select(i => (byte)(i * 31)).ToArray();
        var parts = Fragments.Split(message, 8);
        ok &= parts.Count == Math.Max(1, (size + 7) / 8) && parts.All(p => p.Length <= 9);
        byte[]? back = null;
        for (int i = 0; i < parts.Count; i++)
        {
            back = assembler.Add(parts[i]);
            ok &= (back == null) == (i < parts.Count - 1);
        }
        ok &= back != null && back.SequenceEqual(message);
    }
    Check("fragments round trip", ok);
    bool rejected = false;
    try { var small = new FragmentAssembler(10); foreach (var p in Fragments.Split(new byte[40], 8)) small.Add(p); }
    catch (InvalidDataException) { rejected = true; }
    Check("fragments reject oversized frames", rejected);
}

Console.WriteLine($"{total - failures}/{total} checks passed");
return failures == 0 ? 0 : 1;
