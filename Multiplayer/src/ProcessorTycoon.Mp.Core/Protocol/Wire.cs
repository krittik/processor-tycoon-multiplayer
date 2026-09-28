using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ProcessorTycoonMp.Core.Delta;

namespace ProcessorTycoonMp.Core.Protocol;

public sealed class WireWriter
{
    private readonly MemoryStream stream = new();
    private readonly BinaryWriter w;

    public WireWriter() { w = new BinaryWriter(stream, Encoding.UTF8); }

    public WireWriter U8(byte v) { w.Write(v); return this; }
    public WireWriter Bool(bool v) { w.Write(v); return this; }
    public WireWriter I32(int v) { w.Write(v); return this; }
    public WireWriter I64(long v) { w.Write(v); return this; }
    public WireWriter U64(ulong v) { w.Write(v); return this; }
    public WireWriter Str(string? v) { w.Write(v ?? ""); return this; }
    public WireWriter Bytes(byte[] v) { w.Write(v.Length); w.Write(v); return this; }

    public WireWriter Deltas(IReadOnlyList<EntityDelta> deltas)
    {
        var inner = new WireWriter();
        inner.I32(deltas.Count);
        foreach (var d in deltas) inner.U8((byte)d.Kind).I32(d.Id).U8((byte)d.Op).Str(d.Json);
        return Bytes(Compression.Deflate(inner.ToArray()));
    }

    public byte[] ToArray() { w.Flush(); return stream.ToArray(); }
}

public sealed class WireReader
{
    private readonly BinaryReader r;

    public WireReader(byte[] data, int offset = 0) { r = new BinaryReader(new MemoryStream(data, offset, data.Length - offset), Encoding.UTF8); }

    public byte U8() => r.ReadByte();
    public bool Bool() => r.ReadBoolean();
    public int I32() => r.ReadInt32();
    public long I64() => r.ReadInt64();
    public ulong U64() => r.ReadUInt64();
    public string Str() => r.ReadString();
    public byte[] Bytes() { int n = r.ReadInt32(); return r.ReadBytes(n); }

    public List<EntityDelta> Deltas()
    {
        var inner = new WireReader(Compression.Inflate(Bytes()));
        int n = inner.I32();
        var list = new List<EntityDelta>(n);
        for (int i = 0; i < n; i++)
            list.Add(new EntityDelta((EntityKind)inner.U8(), inner.I32(), (DeltaOp)inner.U8(), inner.Str()));
        return list;
    }
}

public static class Compression
{
    public static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(data, 0, data.Length);
        return output.ToArray();
    }

    public static byte[] Inflate(byte[] data)
    {
        using var input = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    public static byte[] DeflateText(string text) => Deflate(Encoding.UTF8.GetBytes(text));
    public static string InflateText(byte[] data) => Encoding.UTF8.GetString(Inflate(data));
}
