using System.Text;

namespace Nexus.Providers;

/// <summary>
/// Reader for Steam's binary KeyValues files (appcache\stats). Each entry is a type byte, a
/// null-terminated key, then a value: 0 = nested block (ends with 8), 1 = string, 2 = int32,
/// 3 = float32, 4 = pointer, 6 = colour, 7 = uint64, 10 = int64.
/// </summary>
public sealed class BinaryKv
{
    public Dictionary<string, BinaryKv> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public BinaryKv? this[string key] => Children.TryGetValue(key, out var c) ? c : null;
    public string? Str(string key) => Values.TryGetValue(key, out var v) ? v as string : null;
    public int? Int(string key) => Values.TryGetValue(key, out var v) && v is int i ? i : null;

    public static BinaryKv? Load(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            int pos = 0;
            var root = new BinaryKv();
            root.Read(data, ref pos);
            return root;
        }
        catch { return null; }
    }

    void Read(byte[] d, ref int pos)
    {
        while (pos < d.Length)
        {
            byte type = d[pos++];
            if (type == 0x08) return;
            string key = CString(d, ref pos);
            switch (type)
            {
                case 0x00:
                    var child = new BinaryKv();
                    child.Read(d, ref pos);
                    Children[key] = child;
                    break;
                case 0x01: Values[key] = CString(d, ref pos); break;
                case 0x02: Values[key] = BitConverter.ToInt32(d, pos); pos += 4; break;
                case 0x03: Values[key] = BitConverter.ToSingle(d, pos); pos += 4; break;
                case 0x07: Values[key] = BitConverter.ToUInt64(d, pos); pos += 8; break;
                case 0x04: case 0x06: Values[key] = BitConverter.ToInt32(d, pos); pos += 4; break; // pointer, colour
                case 0x0A: Values[key] = BitConverter.ToInt64(d, pos); pos += 8; break;
                default: pos = d.Length; return; // unknown type: stop rather than misread
            }
        }
    }

    static string CString(byte[] d, ref int pos)
    {
        int end = Array.IndexOf(d, (byte)0, pos);
        if (end < 0) end = d.Length;
        var s = Encoding.UTF8.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }
}
