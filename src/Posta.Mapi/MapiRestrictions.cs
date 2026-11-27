namespace Posta.Mapi;

public static class MapiRestrictions
{
    public static byte[] Property(uint tag, object value, bool equal = true) => MapiWire.Write(w =>
    { w.Write((byte)4); w.Write(equal ? (byte)4 : (byte)5); w.Write(tag); MapiWriteWire.Value(w, tag, value); });

    internal static byte[] AtLeast(uint tag, DateTime value) => MapiWire.Write(w =>
    { w.Write((byte)4); w.Write((byte)3); w.Write(tag); MapiWriteWire.Value(w, tag, value); });

    internal static byte[] BitsClear(uint tag, uint mask) => MapiWire.Write(w =>
    { w.Write((byte)6); w.Write((byte)0); w.Write(tag); w.Write(mask); });

    public static byte[] And(byte[] first, byte[] second) => MapiWire.Write(w =>
    { w.Write((byte)0); w.Write((ushort)2); w.Write(first); w.Write(second); });
    public static byte[] Or(byte[] first, byte[] second) => MapiWire.Write(w =>
    { w.Write((byte)1); w.Write((ushort)2); w.Write(first); w.Write(second); });

    internal static byte[] Combine(bool any, IReadOnlyList<byte[]> restrictions) => restrictions.Count == 1 ? restrictions[0] : MapiWire.Write(w =>
    {
        w.Write(any ? (byte)1 : (byte)0); w.Write(checked((ushort)restrictions.Count));
        foreach (var restriction in restrictions) w.Write(restriction);
    });

    internal static byte[] Contains(uint tag, string text) => MapiWire.Write(w =>
    { w.Write((byte)3); w.Write(0x00010001u); w.Write(tag); MapiWriteWire.Value(w, tag, text); });
    // MS-OXCDATA ContentRestriction, FL_SUBSTRING | FL_IGNORECASE; all terms are literal Unicode.
    public static byte[] MailText(string text) => MapiWire.Write(w =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        uint[] tags = [0x0037001f, 0x1000001f, 0x0c1a001f, 0x0e04001f, 0x0e03001f];
        w.Write((byte)1); w.Write((ushort)tags.Length); // OR
        foreach (var tag in tags)
        { w.Write((byte)3); w.Write(0x00010001u); w.Write(tag); MapiWriteWire.Value(w, tag, text); }
    });
}
