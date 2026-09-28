using System;
using System.Collections.Generic;
using ProcessorTycoonMp.Core.Delta;

namespace ProcessorTycoonMp.Core.Protocol;

// Wire messages (docs/PROTOCOL.md). Envelope: u8 type, u32 seq, body.
public enum MsgType : byte
{
    Hello = 1, Welcome = 2, Reject = 3, Snapshot = 4, Ready = 5,
    DayBundle = 6, PeerDelta = 7, CheckpointHash = 8, CheckpointResult = 9, ResyncRequest = 10,
    Roster = 11, Chat = 12, Ping = 13, Pong = 14, Leave = 15, Command = 16, Channel = 17, CompanySetup = 18,
}

public abstract class Message
{
    public abstract MsgType Type { get; }
    public uint Seq { get; set; }
    protected abstract void WriteBody(WireWriter w);

    public byte[] Encode()
    {
        var w = new WireWriter();
        w.U8((byte)Type).I32((int)Seq);
        WriteBody(w);
        return w.ToArray();
    }

    public static Message Decode(byte[] data)
    {
        var r = new WireReader(data);
        var type = (MsgType)r.U8();
        uint seq = (uint)r.I32();
        Message m = type switch
        {
            MsgType.Hello => Hello.Read(r),
            MsgType.Welcome => Welcome.Read(r),
            MsgType.CompanySetup => CompanySetup.Read(r),
            MsgType.Reject => new Reject { Reason = r.Str() },
            MsgType.Snapshot => Snapshot.Read(r),
            MsgType.Ready => new Ready { Day = r.I32() },
            MsgType.DayBundle => DayBundle.Read(r),
            MsgType.PeerDelta => new PeerDelta { Day = r.I32(), Deltas = r.Deltas() },
            MsgType.CheckpointHash => CheckpointHash.Read(r),
            MsgType.CheckpointResult => CheckpointResult.Read(r),
            MsgType.ResyncRequest => new ResyncRequest { Reason = r.Str() },
            MsgType.Roster => new Roster { Players = PlayerInfo.ReadList(r) },
            MsgType.Chat => new Chat { Slot = r.I32(), Text = r.Str() },
            MsgType.Ping => new Ping { Ticks = r.I64() },
            MsgType.Pong => new Pong { Ticks = r.I64() },
            MsgType.Leave => new Leave { Reason = r.Str() },
            MsgType.Command => new Command { Kind = r.Str(), Args = r.Str() },
            MsgType.Channel => new Channel { Name = r.Str(), Slot = r.I32(), Data = r.Bytes() },
            _ => throw new FormatException($"unknown message type {(byte)type}"),
        };
        m.Seq = seq;
        return m;
    }
}

public sealed class PlayerInfo
{
    public int Slot;
    public string Name = "";
    public int CompanyId;
    public bool Connected;
    public string ClientId = "";   // stable per installation; lets a player reclaim their slot (also after a resume)
    public int OfflineSince = -1;   // day the player disconnected (-1 while connected); a caretaker keeps the company (D55)
    public bool AiControl;          // the caretaker period ended: the AI plays the company until the player returns

    public PlayerInfo Copy() => (PlayerInfo)MemberwiseClone();

    public void Write(WireWriter w) => w.I32(Slot).Str(Name).I32(CompanyId).Bool(Connected).Str(ClientId).I32(OfflineSince).Bool(AiControl);
    public static PlayerInfo Read(WireReader r) => new() { Slot = r.I32(), Name = r.Str(), CompanyId = r.I32(), Connected = r.Bool(), ClientId = r.Str(), OfflineSince = r.I32(), AiControl = r.Bool() };

    public static void WriteList(WireWriter w, IReadOnlyList<PlayerInfo> list)
    {
        w.I32(list.Count);
        foreach (var p in list) p.Write(w);
    }

    public static List<PlayerInfo> ReadList(WireReader r)
    {
        int n = r.I32();
        var list = new List<PlayerInfo>(n);
        for (int i = 0; i < n; i++) list.Add(Read(r));
        return list;
    }

    public override string ToString() => $"{Slot}:{Name}#{CompanyId}{(Connected ? "" : AiControl ? " (offline, AI)" : " (offline)")}";
}

// Everything that must be identical between machines (D21).
public sealed class VersionInfo
{
    public int Protocol = MpProtocol.Version;
    public string ModVersion = "";
    public string GameVersion = "";
    public string AssemblyHash = "";
    public string Mods = "";    // sorted "guid@version" list of simulation-affecting mods, comma separated

    public void Write(WireWriter w) => w.I32(Protocol).Str(ModVersion).Str(GameVersion).Str(AssemblyHash).Str(Mods);
    public static VersionInfo Read(WireReader r) => new() { Protocol = r.I32(), ModVersion = r.Str(), GameVersion = r.Str(), AssemblyHash = r.Str(), Mods = r.Str() };

    // Null when compatible, otherwise a readable reason.
    public string? Mismatch(VersionInfo other)
    {
        if (Protocol != other.Protocol) return $"protocol {other.Protocol} vs host {Protocol}";
        if (ModVersion != other.ModVersion) return $"multiplayer mod {other.ModVersion} vs host {ModVersion}";
        if (GameVersion != other.GameVersion) return $"game {other.GameVersion} vs host {GameVersion}";
        if (AssemblyHash != other.AssemblyHash) return "game code differs from the host's (Assembly-CSharp hash)";
        if (Mods != other.Mods) return $"mods [{other.Mods}] vs host [{Mods}]";
        return null;
    }
}

public sealed class Hello : Message
{
    public override MsgType Type => MsgType.Hello;
    public VersionInfo Version = new();
    public string PlayerName = "";
    public string ClientId = "";      // stable per installation; recognizes a returning player
    protected override void WriteBody(WireWriter w) { Version.Write(w); w.Str(PlayerName).Str(ClientId); }
    public static Hello Read(WireReader r) => new() { Version = VersionInfo.Read(r), PlayerName = r.Str(), ClientId = r.Str() };
}

public sealed class Welcome : Message
{
    public override MsgType Type => MsgType.Welcome;
    public string SessionId = "";
    public int Slot;
    public List<PlayerInfo> Players = new();
    // A new player sets up their company first (D58), under the host's rules; a returning player gets the world at once.
    public bool NeedsCompany;
    public SessionRules Rules = new();
    protected override void WriteBody(WireWriter w) { w.Str(SessionId).I32(Slot); PlayerInfo.WriteList(w, Players); w.Bool(NeedsCompany); Rules.Write(w); }
    public static Welcome Read(WireReader r) => new() { SessionId = r.Str(), Slot = r.I32(), Players = PlayerInfo.ReadList(r), NeedsCompany = r.Bool(), Rules = SessionRules.Read(r) };
}

// What the host's game fixes for everyone (D11): difficulty, cheats, and the current year (a new company starts now).
public sealed class SessionRules
{
    public int Difficulty = 2;
    public int Year = 1975;
    public bool Cheats;
    public int CaretakerDays = 182;   // D55, shown to everyone as the countdown until the AI takes over
    public void Write(WireWriter w) => w.I32(Difficulty).I32(Year).Bool(Cheats).I32(CaretakerDays);
    public static SessionRules Read(WireReader r) => new() { Difficulty = r.I32(), Year = r.I32(), Cheats = r.Bool(), CaretakerDays = r.I32() };
}

// A new player's company from the native game setup (D58). Funds, factory size and starting technology come from the
// setup screen's difficulty table on the player's machine; the host creates the company only when this arrives.
public sealed class CompanySetup : Message
{
    public override MsgType Type => MsgType.CompanySetup;
    public string Name = "";
    public string Founder = "";
    public string Color = "#3FA7FF";
    public int CompanyType;          // native CompanyType: 0 CPU, 1 CPU fabless, 2 foundry
    public long StartingFunds;
    public int FactorySize;
    public int StartingTechnology;   // native StartingTechnology
    protected override void WriteBody(WireWriter w) => w.Str(Name).Str(Founder).Str(Color).I32(CompanyType).I64(StartingFunds).I32(FactorySize).I32(StartingTechnology);
    public static CompanySetup Read(WireReader r) => new() { Name = r.Str(), Founder = r.Str(), Color = r.Str(), CompanyType = r.I32(), StartingFunds = r.I64(), FactorySize = r.I32(), StartingTechnology = r.I32() };
}

public sealed class Reject : Message
{
    public override MsgType Type => MsgType.Reject;
    public string Reason = "";
    protected override void WriteBody(WireWriter w) => w.Str(Reason);
}

// Full world for one peer: the game's save JSON with that peer's company as the player (deflated).
public sealed class Snapshot : Message
{
    public override MsgType Type => MsgType.Snapshot;
    public int Day;
    public byte[] World = Array.Empty<byte>();
    protected override void WriteBody(WireWriter w) => w.I32(Day).Bytes(World);
    public static Snapshot Read(WireReader r) => new() { Day = r.I32(), World = r.Bytes() };
}

public sealed class Ready : Message
{
    public override MsgType Type => MsgType.Ready;
    public int Day;
    protected override void WriteBody(WireWriter w) => w.I32(Day);
}

public sealed class SlotDeltas
{
    public int Slot;
    public List<EntityDelta> Deltas = new();
}

// Host → peers: start day `Day`. Carries the state everyone must apply before simulating it.
public sealed class DayBundle : Message
{
    public override MsgType Type => MsgType.DayBundle;
    public int Day;
    public int Speed;
    public bool Checkpoint;
    public bool ResendAll;   // after a snapshot load: every owner sends its full state with its next delta
    public List<SlotDeltas> Slots = new();
    protected override void WriteBody(WireWriter w)
    {
        w.I32(Day).I32(Speed).Bool(Checkpoint).Bool(ResendAll).I32(Slots.Count);
        foreach (var s in Slots) { w.I32(s.Slot); w.Deltas(s.Deltas); }
    }
    public static DayBundle Read(WireReader r)
    {
        var b = new DayBundle { Day = r.I32(), Speed = r.I32(), Checkpoint = r.Bool(), ResendAll = r.Bool() };
        int n = r.I32();
        for (int i = 0; i < n; i++) b.Slots.Add(new SlotDeltas { Slot = r.I32(), Deltas = r.Deltas() });
        return b;
    }
}

// Peer → host: own-company changes after simulating `Day`; acknowledges the day.
public sealed class PeerDelta : Message
{
    public override MsgType Type => MsgType.PeerDelta;
    public int Day;
    public List<EntityDelta> Deltas = new();
    protected override void WriteBody(WireWriter w) { w.I32(Day); w.Deltas(Deltas); }
}

public sealed class CheckpointHash : Message
{
    public override MsgType Type => MsgType.CheckpointHash;
    public int Day;
    public ulong Hash;
    public Dictionary<long, ulong> Entities = new();   // per-entity hashes for the desync report
    protected override void WriteBody(WireWriter w)
    {
        w.I32(Day).U64(Hash).I32(Entities.Count);
        foreach (var kv in Entities) w.I64(kv.Key).U64(kv.Value);
    }
    public static CheckpointHash Read(WireReader r)
    {
        var m = new CheckpointHash { Day = r.I32(), Hash = r.U64() };
        int n = r.I32();
        for (int i = 0; i < n; i++) m.Entities[r.I64()] = r.U64();
        return m;
    }
}

public sealed class CheckpointResult : Message
{
    public override MsgType Type => MsgType.CheckpointResult;
    public int Day;
    public bool Ok;
    public string Detail = "";
    public List<long> Differing = new();   // entity keys whose hashes differ from the host's
    protected override void WriteBody(WireWriter w)
    {
        w.I32(Day).Bool(Ok).Str(Detail).I32(Differing.Count);
        foreach (var k in Differing) w.I64(k);
    }
    public static CheckpointResult Read(WireReader r)
    {
        var m = new CheckpointResult { Day = r.I32(), Ok = r.Bool(), Detail = r.Str() };
        int n = r.I32();
        for (int i = 0; i < n; i++) m.Differing.Add(r.I64());
        return m;
    }
}

public sealed class ResyncRequest : Message
{
    public override MsgType Type => MsgType.ResyncRequest;
    public string Reason = "";
    protected override void WriteBody(WireWriter w) => w.Str(Reason);
}

public sealed class Roster : Message
{
    public override MsgType Type => MsgType.Roster;
    public List<PlayerInfo> Players = new();
    protected override void WriteBody(WireWriter w) => PlayerInfo.WriteList(w, Players);
}

public sealed class Chat : Message
{
    public override MsgType Type => MsgType.Chat;
    public int Slot;
    public string Text = "";
    protected override void WriteBody(WireWriter w) => w.I32(Slot).Str(Text);
}

public sealed class Ping : Message
{
    public override MsgType Type => MsgType.Ping;
    public long Ticks;
    protected override void WriteBody(WireWriter w) => w.I64(Ticks);
}

public sealed class Pong : Message
{
    public override MsgType Type => MsgType.Pong;
    public long Ticks;
    protected override void WriteBody(WireWriter w) => w.I64(Ticks);
}

// Peer → host: an action on a host-owned shared object (ARCHITECTURE "Ownership"), e.g. a contract offer (D43).
public sealed class Command : Message
{
    public override MsgType Type => MsgType.Command;
    public string Kind = "";
    public string Args = "";
    protected override void WriteBody(WireWriter w) => w.Str(Kind).Str(Args);
}

// Any direction: data of another mod on a named channel (interop API, D14); the host rebroadcasts to all others.
public sealed class Channel : Message
{
    public override MsgType Type => MsgType.Channel;
    public string Name = "";
    public int Slot;
    public byte[] Data = Array.Empty<byte>();
    protected override void WriteBody(WireWriter w) { w.Str(Name).I32(Slot); w.Bytes(Data); }
}

public sealed class Leave : Message
{
    public override MsgType Type => MsgType.Leave;
    public string Reason = "";
    protected override void WriteBody(WireWriter w) => w.Str(Reason);
}
