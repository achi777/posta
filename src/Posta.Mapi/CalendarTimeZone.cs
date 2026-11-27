namespace Posta.Mapi;

public static class CalendarTimeZone
{
    private sealed record Transition(ushort Year, ushort Month, ushort Dow, ushort Day, ushort Hour, ushort Minute, ushort Second, ushort Millisecond);
    public static TimeZoneInfo Parse(byte[] data)
    {
        if (data.Length != 48) throw new MapiException("Invalid legacy calendar time-zone structure length.");
        var r = new WireReader(data); var bias = unchecked((int)r.U32()); var standardBias = unchecked((int)r.U32()); var daylightBias = unchecked((int)r.U32());
        Transition Read()
        {
            var year = r.U16(); var t = new Transition(r.U16(), r.U16(), r.U16(), r.U16(), r.U16(), r.U16(), r.U16(), r.U16());
            if (year != t.Year) throw new MapiException("Inconsistent calendar time-zone year."); return t;
        }
        var standard = Read(); var daylight = Read();
        var offset = TimeSpan.FromMinutes(-(bias + standardBias));
        if (standard.Month == 0 && daylight.Month == 0) return TimeZoneInfo.CreateCustomTimeZone("MAPI:" + offset, offset, "MAPI calendar zone", "MAPI standard");
        TimeZoneInfo.TransitionTime Convert(Transition t)
        {
            var time = new DateTime(1, 1, 1, t.Hour, t.Minute, t.Second, t.Millisecond);
            return t.Year == 0 ? TimeZoneInfo.TransitionTime.CreateFloatingDateRule(time, t.Month, t.Day, (DayOfWeek)t.Dow)
                : TimeZoneInfo.TransitionTime.CreateFixedDateRule(time, t.Month, t.Day);
        }
        var firstYear = standard.Year == 0 ? 1601 : standard.Year;
        var lastYear = standard.Year == 0 ? 4500 : standard.Year;
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(firstYear, 1, 1), new(lastYear, 12, 31),
            TimeSpan.FromMinutes(standardBias - daylightBias), Convert(daylight), Convert(standard));
        return TimeZoneInfo.CreateCustomTimeZone("MAPI:" + offset, offset, "MAPI calendar zone", "MAPI standard", "MAPI daylight", [rule]);
    }

    public static byte[] Encode(TimeZoneInfo zone, int year) => MapiWire.Write(w =>
    {
        var rule = zone.GetAdjustmentRules().FirstOrDefault(r => r.DateStart.Year <= year && r.DateEnd.Year >= year);
        var baseOffset = zone.BaseUtcOffset + (rule?.BaseUtcOffsetDelta ?? TimeSpan.Zero);
        w.Write(checked(-(int)baseOffset.TotalMinutes)); w.Write(0); w.Write(checked(-(int)(rule?.DaylightDelta.TotalMinutes ?? 0)));
        void Write(TimeZoneInfo.TransitionTime? transition)
        {
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)(transition?.Month ?? 0)); w.Write((ushort)(transition?.DayOfWeek ?? 0));
            if (transition is { IsFixedDateRule: true }) throw new MapiException("Fixed-date historical daylight rules need a modern time-zone definition before calendar writing.");
            w.Write((ushort)(transition?.Week ?? 0)); w.Write((ushort)(transition?.TimeOfDay.Hour ?? 0));
            w.Write((ushort)(transition?.TimeOfDay.Minute ?? 0)); w.Write((ushort)(transition?.TimeOfDay.Second ?? 0));
            w.Write((ushort)(transition?.TimeOfDay.Millisecond ?? 0));
        }
        Write(rule?.DaylightDelta != TimeSpan.Zero ? rule?.DaylightTransitionEnd : null);
        Write(rule?.DaylightDelta != TimeSpan.Zero ? rule?.DaylightTransitionStart : null);
    });
}
