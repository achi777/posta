using System.Text;

namespace Posta.Mapi;

public sealed record MapiRecipient(byte Kind, string Name, string Address, uint? Flags = null, uint? TrackStatus = null);
public sealed record MapiAttachment(string Name, string ContentType, byte[] Content, string? ContentId = null);

// MS-OXCDATA tagged values and one-off SMTP recipients. Encoding completes before any HTTP write.
public static class MapiWriteWire
{
    public static void UnicodeZ(BinaryWriter w, string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL is not allowed in a MAPI string.");
        w.Write(Encoding.Unicode.GetBytes(value)); w.Write((ushort)0);
    }

    public static void Value(BinaryWriter w, uint tag, object value)
    {
        w.Write(tag);
        switch ((ushort)tag)
        {
            case 0xfd:
            case 0xfe:
                w.Write((byte[])value); break; // Restriction and rule-action structures have their own framing.
            case 2: w.Write(Convert.ToUInt16(value)); break;
            case 3: w.Write(Convert.ToUInt32(value)); break;
            case 5: w.Write(Convert.ToDouble(value)); break;
            case 11: w.Write(Convert.ToBoolean(value)); break;
            case 0x14: w.Write(Convert.ToUInt64(value)); break;
            case 0x1f: UnicodeZ(w, (string)value); break;
            case 0x40: w.Write(((DateTime)value).ToUniversalTime().ToFileTimeUtc()); break;
            case 0xfb:
            case 0x102:
                var bytes = (byte[])value; w.Write(checked((ushort)bytes.Length)); w.Write(bytes); break;
            case 0x101f:
                var strings = ((IEnumerable<string>)value).ToArray();
                w.Write(checked((uint)strings.Length)); foreach (var s in strings) UnicodeZ(w, s); break;
            default: throw new ArgumentException($"Unsupported write property type {(ushort)tag:X4}.");
        }
    }

    public static byte[] Properties(byte index, IReadOnlyDictionary<uint, object> values) => MapiWire.Write(w =>
    {
        var data = MapiWire.Write(p => { foreach (var (tag, value) in values) Value(p, tag, value); });
        w.Write(new byte[] { 0x0a, 0, index }); w.Write(checked((ushort)(2 + data.Length)));
        w.Write(checked((ushort)values.Count)); w.Write(data);
    });

    public static byte[] Recipients(byte index, IReadOnlyList<MapiRecipient> recipients) => MapiWire.Write(w =>
    {
        var meeting = recipients.Any(r => r.Flags.HasValue || r.TrackStatus.HasValue);
        w.Write(new byte[] { 0x0e, 0, index });
        w.Write((ushort)(meeting ? 2 : 0));
        if (meeting) { w.Write(0x5ffd0003u); w.Write(0x5fff0003u); }
        w.Write(checked((ushort)recipients.Count));
        for (int i = 0; i < recipients.Count; i++)
        {
            var recipient = recipients[i];
            if (recipient.Kind is < 1 or > 3) throw new ArgumentException("Invalid recipient kind.");
            if (!System.Net.Mail.MailAddress.TryCreate(recipient.Address, out var address)) throw new ArgumentException("Invalid SMTP recipient address.");
            if (!address.Address.Equals(recipient.Address, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Enter an SMTP address without a display name in the address field.");
            var row = MapiWire.Write(r =>
            {
                r.Write((ushort)0x8258); MapiWire.AsciiZ(r, "SMTP");
                UnicodeZ(r, recipient.Address); UnicodeZ(r, recipient.Name);
                r.Write((ushort)(meeting ? 2 : 0)); r.Write((byte)0); // StandardPropertyRow.
                if (meeting) { r.Write(recipient.Flags ?? 1u); r.Write(recipient.TrackStatus ?? 0u); }
            });
            w.Write((uint)i); w.Write(recipient.Kind); w.Write(checked((ushort)row.Length)); w.Write(row);
        }
    });

    public static void PropertyResult(WireReader r, byte index, byte operation = 0x0a)
    {
        MapiWire.RopHeader(r, operation, index);
        var count = r.U16();
        var failures = new List<string>();
        for (int i = 0; i < count; i++) { r.U16(); var tag = r.U32(); var error = r.U32(); failures.Add($"{tag:X8}:{error:X8}"); }
        if (failures.Count > 0) throw new MapiException("Server refused properties: " + string.Join(", ", failures));
    }

    public static void End(WireReader r)
    {
        if (r.Remaining != 0) throw new MapiException("Unexpected write response tail.");
    }
}
