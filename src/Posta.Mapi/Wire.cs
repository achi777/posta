using System.Buffers.Binary;
using System.Text;

namespace Posta.Mapi;

public class MapiException(string message) : IOException(message)
{
    internal uint? OperationCode { get; init; }
}
public sealed class MapiSubmissionException(ulong savedId, bool uncertain, string message) : MapiException(message)
{
    public ulong SavedMessageId { get; } = savedId;
    public bool OutcomeUnknown { get; } = uncertain;
}

// MS-OXCMAPIHTTP, MS-OXCROPS, MS-OXCDATA. No Office DLLs or COM dependencies.
// See THIRD-PARTY-NOTICES.md for the independently captured verification fixtures.
public sealed class WireReader(byte[] bytes)
{
    public int Position { get; private set; }
    public int Remaining => bytes.Length - Position;
    public byte[] Read(int count)
    {
        if (count < 0 || count > Remaining) throw new MapiException("Truncated MAPI response.");
        var value = bytes.AsSpan(Position, count).ToArray();
        Position += count;
        return value;
    }
    public byte U8() => Read(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Read(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Read(8));
    public string Text(bool unicode, bool reducedUnicode = false)
    {
        using var result = new MemoryStream();
        while (true)
        {
            var unit = Read(unicode ? 2 : 1);
            if (unit.All(b => b == 0)) break;
            result.Write(unit);
        }
        // Connect requests negotiate CP1252 for String8. User-visible properties use UTF-16.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return (unicode ? Encoding.Unicode : reducedUnicode ? Encoding.Latin1 : Encoding.GetEncoding(1252)).GetString(result.ToArray());
    }
}

public sealed record RopPacket(byte[] Operations, uint[] Handles);
public sealed record MapiPayload(byte[] Body, IReadOnlyDictionary<string, string> Headers);
public sealed record MapiCellError(uint Code);
public sealed record MapiRecipients(ushort Count, IReadOnlyList<MapiRecipient> Values, bool Complete);

public static class MapiWire
{
    public static MapiRecipients ReadOpenMessageMetadata(WireReader r)
    {
        r.U8(); // HasNamedProperties
        for (var i = 0; i < 2; i++)
        {
            var type = r.U8();
            if (type is 0 or 1) continue;
            if (type is not (2 or 3 or 4)) throw new MapiException("Unknown TypedString encoding.");
            r.Text(type == 4, type == 3);
        }
        var recipients = r.U16(); var columnCount = r.U16();
        var columns = Enumerable.Range(0, columnCount).Select(_ => r.U32()).ToArray();
        var rows = r.U8();
        if (rows > recipients) throw new MapiException("Invalid recipient row count.");
        var values = new List<MapiRecipient>();
        bool complete = rows == recipients;
        for (var i = 0; i < rows; i++)
        {
            var kind = r.U8(); r.U16(); r.U16(); var data = r.Read(r.U16());
            try
            {
                var row = new WireReader(data); var flags = row.U16(); var type = flags & 7;
                string addressType = type == 3 ? "SMTP" : "";
                if (type == 1) { row.U8(); row.U8(); row.Text(false); addressType = "EX"; }
                else if (type is 6 or 7) { row.Read(row.U16()); row.Read(row.U16()); }
                else if (type != 0 && type != 3) throw new MapiException("Unsupported recipient address type.");
                if (type == 0 && (flags & 0x8000) != 0) addressType = row.Text(false);
                bool unicode = (flags & 0x200) != 0;
                var address = (flags & 8) != 0 ? row.Text(unicode) : "";
                var name = (flags & 0x10) != 0 ? row.Text(unicode) : "";
                if ((flags & 0x400) != 0) row.Text(unicode);
                if ((flags & 0x20) != 0) row.Text(unicode);
                var n = row.U16();
                if (n > columns.Length) throw new MapiException("Invalid recipient column count.");
                var props = ReadRow(row, columns.Take(n).ToArray());
                if (props.GetValueOrDefault(0x39fe001fu) is string smtp && smtp.Length > 0) address = smtp;
                else if (addressType != "SMTP") address = "";
                if (row.Remaining != 0 || string.IsNullOrWhiteSpace(address)) complete = false;
                values.Add(new((byte)(kind & 0xf), name, address,
                    props.GetValueOrDefault(0x5ffd0003u) as uint?, props.GetValueOrDefault(0x5fff0003u) as uint?));
            }
            catch (Exception ex) when (ex is MapiException or ArgumentException or OverflowException)
            { complete = false; }
        }
        return new(recipients, values, complete);
    }
    public static byte[] Write(Action<BinaryWriter> action)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        action(writer);
        return stream.ToArray();
    }

    public static void AsciiZ(BinaryWriter w, string text)
    {
        if (text.Any(c => c > 127 || c == '\0')) throw new ArgumentException("Expected a NUL-free ASCII distinguished name.");
        w.Write(Encoding.ASCII.GetBytes(text)); w.Write((byte)0);
    }

    public static byte[] Connect(string userDn) => Write(w =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDn);
        AsciiZ(w, userDn);
        w.Write(0u); w.Write(1252u); w.Write(0x409u); w.Write(0x409u); w.Write(0u);
    });

    public static byte[] Frame(byte[] rops, params uint[] handles) => Write(w =>
    {
        var size = checked((ushort)(2 + rops.Length + 4 * handles.Length));
        w.Write((ushort)0); w.Write((ushort)4); w.Write(size); w.Write(size);
        w.Write(checked((ushort)(2 + rops.Length))); w.Write(rops);
        foreach (var handle in handles) w.Write(handle);
    });

    public static byte[] Execute(byte[] framed) => Write(w =>
    {
        w.Write(3u); // NoCompression | NoXorMagic
        w.Write((uint)framed.Length); w.Write(framed); w.Write(65536u); w.Write(0u);
    });

    public static RopPacket ReadPacket(byte[] bytes)
    {
        var r = new WireReader(bytes);
        var version = r.U16(); var flags = r.U16(); var size = r.U16(); var actual = r.U16();
        if (version != 0 || flags != 4 || actual != size)
            throw new MapiException("Unsupported compressed, chained or obfuscated ROP buffer.");
        var p = new WireReader(r.Read(size));
        if (r.Remaining != 0) throw new MapiException("Unexpected trailing ROP blocks.");
        var ropSize = p.U16();
        if (ropSize < 2) throw new MapiException("Invalid RopSize.");
        var rops = p.Read(ropSize - 2);
        if (p.Remaining % 4 != 0) throw new MapiException("Invalid handle table length.");
        var handles = new List<uint>();
        while (p.Remaining > 0) handles.Add(p.U32());
        return new(rops, handles.ToArray());
    }

    public static RopPacket ReadExecute(byte[] body)
    {
        var r = new WireReader(body);
        Success(r.U32(), "Execute transport"); Success(r.U32(), "Execute");
        r.U32();
        var framed = r.Read(checked((int)r.U32()));
        var auxiliarySize = checked((int)r.U32()); r.Read(auxiliarySize);
        if (r.Remaining != 0) throw new MapiException("Unexpected Execute response tail.");
        return ReadPacket(framed);
    }

    public static void Success(uint code, string operation)
    {
        if (code != 0) throw new MapiException($"{operation} failed: 0x{code:X8}.") { OperationCode = code };
    }

    public static void RopHeader(WireReader reader, byte expected, byte index)
    {
        if (reader.U8() != expected || reader.U8() != index) throw new MapiException("Unexpected ROP response or handle index.");
        Success(reader.U32(), $"ROP 0x{expected:X2}");
    }

    // HTTP stacks remove chunk framing, but not the MAPI PROCESSING/PENDING/DONE preamble.
    public static MapiPayload Unwrap(byte[] raw)
    {
        var offset = 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool done = false;
        while (offset < raw.Length && offset < 65536)
        {
            int end = raw.AsSpan(offset).IndexOf("\r\n"u8);
            if (end < 0) throw new MapiException("Incomplete MAPI response preamble.");
            var line = Encoding.ASCII.GetString(raw, offset, end); offset += end + 2;
            if (!done)
            {
                if (line == "DONE") done = true;
                else if (line is not ("PROCESSING" or "PENDING")) throw new MapiException("Expected MAPI response meta-tag.");
                continue;
            }
            if (line.Length == 0) return new(raw[offset..], headers);
            var colon = line.IndexOf(':');
            if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                throw new MapiException("Invalid or duplicate MAPI response header.");
        }
        throw new MapiException("Missing MAPI response body separator.");
    }

    public static Dictionary<uint, object?> ReadRow(WireReader r, uint[] columns)
    {
        var form = r.U8();
        if (form > 1) throw new MapiException("Unknown property row encoding.");
        var values = new Dictionary<uint, object?>();
        foreach (var tag in columns)
        {
            var flag = form == 1 ? r.U8() : (byte)0;
            values[tag] = flag switch
            {
                1 => null,
                0x0a => new MapiCellError(r.U32()),
                0 => ReadValue(r, (ushort)tag),
                _ => throw new MapiException("Unknown property value flag.")
            };
        }
        return values;
    }

    public static object? ReadValue(WireReader r, ushort type) => type switch
    {
        0x0002 => r.U16(), 0x0003 => r.U32(), 0x0005 => BitConverter.Int64BitsToDouble((long)r.U64()), 0x000b => r.U8() != 0,
        0x0014 => r.U64(), 0x001e => r.Text(false), 0x001f => r.Text(true),
        0x0040 => DateTime.FromFileTimeUtc(checked((long)r.U64())),
        0x0048 => new Guid(r.Read(16)), 0x0102 => r.Read(r.U16()),
        0x000a => new MapiCellError(r.U32()),
        0x101f => ReadStrings(r),
        0x1102 => ReadBinaries(r),
        _ => throw new MapiException($"Unsupported property type 0x{type:X4}.")
    };

    private static string[] ReadStrings(WireReader r)
    {
        var count = r.U32();
        if (count > 10000) throw new MapiException("Too many string values.");
        return Enumerable.Range(0, (int)count).Select(_ => r.Text(true)).ToArray();
    }

    private static byte[][] ReadBinaries(WireReader r)
    {
        var count = r.U32();
        if (count > 10000) throw new MapiException("Too many binary values.");
        return Enumerable.Range(0, (int)count).Select(_ => r.Read(r.U16())).ToArray();
    }
}
