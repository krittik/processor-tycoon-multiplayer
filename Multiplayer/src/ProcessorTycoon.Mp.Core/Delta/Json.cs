using System;
using System.Collections.Generic;
using System.Text;

namespace ProcessorTycoonMp.Core.Delta;

// Minimal JSON scanner for JsonUtility output (D22): splits objects into raw members and builds partial objects.
// Values are kept as raw text, so numbers/strings are compared exactly as the game serialized them.
public static class Json
{
    public readonly struct Member
    {
        public Member(string rawKey, string value) { RawKey = rawKey; Value = value; }
        public string RawKey { get; }   // key including quotes, as written
        public string Value { get; }    // raw value text
        public string Key => RawKey.Substring(1, RawKey.Length - 2);
    }

    public static bool IsObject(string value) => value.Length > 1 && value[0] == '{';

    public static List<Member> Members(string json)
    {
        var result = new List<Member>();
        int i = SkipWs(json, 0);
        if (i >= json.Length || json[i] != '{') throw new FormatException("JSON object expected");
        i = SkipWs(json, i + 1);
        if (i < json.Length && json[i] == '}') return result;
        while (i < json.Length)
        {
            int keyStart = i;
            int keyEnd = SkipString(json, i);
            string key = json.Substring(keyStart, keyEnd - keyStart);
            i = SkipWs(json, keyEnd);
            if (json[i] != ':') throw new FormatException($"':' expected at {i}");
            i = SkipWs(json, i + 1);
            int valueStart = i;
            int valueEnd = SkipValue(json, i);
            result.Add(new Member(key, json.Substring(valueStart, valueEnd - valueStart)));
            i = SkipWs(json, valueEnd);
            if (json[i] == ',') { i = SkipWs(json, i + 1); continue; }
            if (json[i] == '}') return result;
            throw new FormatException($"',' or '}}' expected at {i}");
        }
        throw new FormatException("unterminated object");
    }

    public static string? Get(string json, string key)
    {
        foreach (var m in Members(json))
            if (m.Key == key) return m.Value;
        return null;
    }

    public static string Build(IEnumerable<Member> members)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var m in members)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(m.RawKey).Append(':').Append(m.Value);
        }
        return sb.Append('}').ToString();
    }

    public static string Without(string json, ICollection<string> keys)
    {
        var members = Members(json);
        members.RemoveAll(m => keys.Contains(m.Key));
        return Build(members);
    }

    // Partial object with the members of `next` that differ from `previous`; nested objects are diffed recursively
    // (FromJsonOverwrite merges nested objects in place), arrays and scalars are sent whole. Null when equal.
    // Members that disappeared cannot be expressed; JsonUtility output always has a fixed member set.
    public static string? Diff(string previous, string next)
    {
        if (previous == next) return null;
        var old = new Dictionary<string, string>();
        foreach (var m in Members(previous)) old[m.RawKey] = m.Value;
        var changed = new List<Member>();
        foreach (var m in Members(next))
        {
            if (!old.TryGetValue(m.RawKey, out var before)) { changed.Add(m); continue; }
            if (before == m.Value) continue;
            if (IsObject(before) && IsObject(m.Value))
            {
                var inner = Diff(before, m.Value);
                if (inner != null) changed.Add(new Member(m.RawKey, inner));
            }
            else changed.Add(m);
        }
        return changed.Count == 0 ? null : Build(changed);
    }

    // Applies a partial object onto a full one with FromJsonOverwrite semantics (nested objects merge, the rest replaces).
    public static string Merge(string full, string patch)
    {
        var members = Members(full);
        var index = new Dictionary<string, int>();
        for (int k = 0; k < members.Count; k++) index[members[k].RawKey] = k;
        foreach (var p in Members(patch))
        {
            if (index.TryGetValue(p.RawKey, out int at))
            {
                var current = members[at].Value;
                members[at] = new Member(p.RawKey, IsObject(current) && IsObject(p.Value) ? Merge(current, p.Value) : p.Value);
            }
            else
            {
                index[p.RawKey] = members.Count;
                members.Add(p);
            }
        }
        return Build(members);
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int SkipString(string s, int i)
    {
        if (s[i] != '"') throw new FormatException($"string expected at {i}");
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '"') return i + 1;
            i++;
        }
        throw new FormatException("unterminated string");
    }

    private static int SkipValue(string s, int i)
    {
        char c = s[i];
        if (c == '"') return SkipString(s, i);
        if (c == '{' || c == '[')
        {
            int depth = 0;
            while (i < s.Length)
            {
                c = s[i];
                if (c == '"') { i = SkipString(s, i); continue; }
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { depth--; if (depth == 0) return i + 1; }
                i++;
            }
            throw new FormatException("unterminated container");
        }
        while (i < s.Length && s[i] != ',' && s[i] != '}' && s[i] != ']' && !char.IsWhiteSpace(s[i])) i++;
        return i;
    }
}
