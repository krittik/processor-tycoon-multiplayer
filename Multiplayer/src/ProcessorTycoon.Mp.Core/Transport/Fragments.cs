using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessorTycoonMp.Core.Transport;

// For transports with a per-message size limit (Steam: 512 KiB per reliable message, D50): a frame travels as one or
// more fragments, each starting with one byte, 1 = more fragments follow, 0 = last fragment of the frame.
public static class Fragments
{
    public static List<byte[]> Split(byte[] message, int maxPayload)
    {
        var result = new List<byte[]>();
        int offset = 0;
        do
        {
            int length = Math.Min(maxPayload, message.Length - offset);
            var fragment = new byte[length + 1];
            fragment[0] = offset + length < message.Length ? (byte)1 : (byte)0;
            Buffer.BlockCopy(message, offset, fragment, 1, length);
            result.Add(fragment);
            offset += length;
        } while (offset < message.Length);
        return result;
    }
}

// One per connection: fragments of a frame arrive in order on a reliable ordered channel.
public sealed class FragmentAssembler
{
    private readonly int maxMessage;
    private MemoryStream? partial;

    public FragmentAssembler(int maxMessage) { this.maxMessage = maxMessage; }

    // Returns the complete frame after its last fragment, otherwise null.
    public byte[]? Add(byte[] fragment)
    {
        if (fragment.Length == 0) throw new InvalidDataException("empty fragment");
        bool more = fragment[0] != 0;
        if (!more && partial == null)
        {
            var whole = new byte[fragment.Length - 1];
            Buffer.BlockCopy(fragment, 1, whole, 0, whole.Length);
            return whole;
        }
        partial ??= new MemoryStream();
        partial.Write(fragment, 1, fragment.Length - 1);
        if (partial.Length > maxMessage) throw new InvalidDataException($"message larger than {maxMessage} bytes");
        if (more) return null;
        var message = partial.ToArray();
        partial = null;
        return message;
    }
}
