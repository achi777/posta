using System.Text;
using Posta.Core.Models;

namespace Posta.Mapi;

// MS-OXOCAL 2.2.1.44. Grammar cross-checked against Microsoft's MFCMAPI SmartView.
public sealed class CalendarRecurrence
{
    public static readonly DateTime Epoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    public ushort Frequency { get; private set; }
    public ushort Pattern { get; private set; }
    public uint FirstDateTime { get; private set; }
    public uint Period { get; private set; }
    public uint MaskOrDay { get; private set; }
    public uint Nth { get; private set; }
    public uint EndType { get; private set; }
    public uint Count { get; private set; }
    public uint FirstDow { get; private set; }
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public uint StartOffset { get; private set; }
    public uint EndOffset { get; private set; }
    public List<DateTime> Deleted { get; } = [];
    public List<DateTime> Modified { get; } = [];
    public List<CalendarException> Exceptions { get; } = [];
    private byte[] _extension = [];

    public static CalendarRecurrence Parse(byte[] bytes)
    {
        var r = new WireReader(bytes); var result = new CalendarRecurrence();
        if (r.U16() != 0x3004 || r.U16() != 0x3004) throw new MapiException("Unsupported recurrence version.");
        result.Frequency = r.U16(); result.Pattern = r.U16();
        if (result.Frequency is < 0x200a or > 0x200d || r.U16() is not (0 or 1)) throw new MapiException("Unsupported non-Gregorian recurrence.");
        result.FirstDateTime = r.U32(); result.Period = r.U32();
        if (result.Period == 0 || r.U32() != 0) throw new MapiException("Invalid/sliding calendar recurrence.");
        if (result.Pattern is 1 or 2 or 3 or 4) result.MaskOrDay = r.U32();
        else if (result.Pattern != 0) throw new MapiException("Unsupported recurrence pattern.");
        if (result.Pattern == 3) result.Nth = r.U32();
        result.EndType = r.U32(); result.Count = r.U32(); result.FirstDow = r.U32();
        if (result.FirstDow > 6 || result.EndType is < 0x2021 or > 0x2023) throw new MapiException("Invalid recurrence range.");
        ReadDates(r, result.Deleted); ReadDates(r, result.Modified);
        result.StartDate = FromMinutes(r.U32()); result.EndDate = FromMinutes(r.U32());
        var extensionOffset = r.Position;
        if (r.U32() != 0x3006) throw new MapiException("Unsupported appointment recurrence version.");
        var writer = r.U32(); if (writer is not (0x3008 or 0x3009)) throw new MapiException("Unsupported recurrence writer version.");
        result.StartOffset = r.U32(); result.EndOffset = r.U32();
        if (result.StartOffset >= 1440 || result.EndOffset < result.StartOffset || result.EndOffset > 1440 * 366u)
            throw new MapiException("Invalid recurrence time offsets.");
        var count = r.U16(); if (count != result.Modified.Count) throw new MapiException("Modified recurrence/exception count mismatch.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string Narrow()
        {
            var length = r.U16(); var actual = r.U16();
            if (length != actual + 1) throw new MapiException("Invalid exception text length.");
            return Encoding.GetEncoding(1252).GetString(r.Read(actual));
        }
        for (int i = 0; i < count; i++)
        {
            var e = new CalendarException { Start = FromMinutes(r.U32()), End = FromMinutes(r.U32()), Original = FromMinutes(r.U32()), Flags = r.U16() };
            if ((e.Flags & ~0x3ff) != 0) throw new MapiException("Unknown exception override flags.");
            if ((e.Flags & 1) != 0) e.Subject = Narrow();
            if ((e.Flags & 2) != 0) e.MeetingFlags = r.U32();
            if ((e.Flags & 4) != 0) e.ReminderMinutes = checked((int)r.U32());
            if ((e.Flags & 8) != 0) e.ReminderSet = r.U32() != 0;
            if ((e.Flags & 16) != 0) e.Location = Narrow();
            if ((e.Flags & 32) != 0) e.ShowAs = (ShowAs)r.U32();
            if ((e.Flags & 64) != 0) r.U32();
            if ((e.Flags & 128) != 0) e.AllDay = r.U32() != 0;
            if ((e.Flags & 256) != 0) r.U32();
            result.Exceptions.Add(e);
        }
        r.Read(checked((int)r.U32()));
        foreach (var e in result.Exceptions)
        {
            if (writer >= 0x3009) { var size = r.U32(); if (size < 4) throw new MapiException("Invalid change highlight size."); r.Read(checked((int)size)); }
            r.Read(checked((int)r.U32()));
            if ((e.Flags & 17) != 0)
            {
                e.Start = FromMinutes(r.U32()); e.End = FromMinutes(r.U32()); e.Original = FromMinutes(r.U32());
                if ((e.Flags & 1) != 0) e.Subject = Encoding.Unicode.GetString(r.Read(checked(r.U16() * 2)));
                if ((e.Flags & 16) != 0) e.Location = Encoding.Unicode.GetString(r.Read(checked(r.U16() * 2)));
                r.Read(checked((int)r.U32()));
            }
        }
        r.Read(checked((int)r.U32())); MapiWriteWire.End(r);
        result._extension = bytes[extensionOffset..];
        result.ValidatePattern();
        return result;
    }

    private void ValidatePattern()
    {
        if (Pattern == 0 && (Period % 1440 != 0 || Frequency != 0x200a)) throw new MapiException("Invalid daily recurrence period.");
        if (Pattern == 1 && (MaskOrDay is 0 or > 127)) throw new MapiException("Invalid weekly day mask.");
        if (Pattern is 2 or 4 && (MaskOrDay is < 1 or > 31)) throw new MapiException("Invalid recurrence month day.");
        if (Pattern == 3 && (MaskOrDay is 0 or > 127 || Nth is < 1 or > 5)) throw new MapiException("Invalid relative monthly recurrence.");
        if (StartDate.TimeOfDay != TimeSpan.Zero || (EndType != 0x2023 && EndDate.TimeOfDay != TimeSpan.Zero)) throw new MapiException("Recurrence range is not midnight-aligned.");
    }

    private static void ReadDates(WireReader r, List<DateTime> list)
    {
        var count = r.U32(); if (count > 10000) throw new MapiException("Recurrence exception limit exceeded.");
        for (int i = 0; i < count; i++) list.Add(FromMinutes(r.U32()));
    }
    public static DateTime FromMinutes(uint minutes) => Epoch.AddMinutes(minutes);
    public static uint Minutes(DateTime value) => checked((uint)(DateTime.SpecifyKind(value, DateTimeKind.Unspecified) - Epoch).TotalMinutes);
    private static long Mod(long value, long period) => (value % period + period) % period;
    public bool Matches(DateTime day)
    {
        if (day.Date < StartDate.Date) return false;
        if (Pattern == 0) return Mod(Minutes(day.Date) - (long)FirstDateTime, Period) == 0;
        if (Pattern == 1)
        {
            var week = day.Date.AddDays(-(((int)day.DayOfWeek - (int)FirstDow + 7) % 7));
            return Mod(Minutes(week) - (long)FirstDateTime, Period * 10080L) == 0 && (MaskOrDay & (1u << (int)day.DayOfWeek)) != 0;
        }
        var first = FromMinutes(FirstDateTime);
        var months = (day.Year - 1601) * 12L + day.Month - 1;
        var firstMonth = (first.Year - 1601) * 12L + first.Month - 1;
        if (Mod(months - firstMonth, Period) != 0) return false;
        if (Pattern is 2 or 4) return day.Day == Math.Min((int)MaskOrDay, DateTime.DaysInMonth(day.Year, day.Month));
        var eligible = Enumerable.Range(1, DateTime.DaysInMonth(day.Year, day.Month))
            .Where(n => (MaskOrDay & (1u << (int)new DateTime(day.Year, day.Month, n).DayOfWeek)) != 0).ToArray();
        return eligible.Length > 0 && day.Day == (Nth == 5 ? eligible[^1] : eligible.ElementAtOrDefault((int)Nth - 1));
    }

    public IEnumerable<DateTime> OriginalDates(DateTime until)
    {
        uint found = 0; int examined = 0;
        for (var day = StartDate.Date; day <= until.Date && (EndType == 0x2023 || day <= EndDate.Date); day = day.AddDays(1))
        {
            if (++examined > 1500000) throw new MapiException("Recurrence date range is too large.");
            if (!Matches(day)) continue;
            if (EndType == 0x2022 && found >= Count) yield break;
            found++; yield return day;
        }
    }

    public IReadOnlyList<CalendarEvent> Expand(CalendarEvent master, TimeZoneInfo zone, DateTime from, DateTime to)
    {
        var result = new List<CalendarEvent>();
        var excluded = Deleted.Select(d => d.Date).Concat(Exceptions.Select(e => e.Original.Date)).ToHashSet();
        var horizon = TimeZoneInfo.ConvertTime(to.AddDays(2), zone).Date;
        DateTime Local(DateTime value) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), zone).ToLocalTime();
        void Add(DateTime original, DateTime begin, DateTime finish, CalendarException? e)
        {
            var s = Local(begin); var end = Local(finish); if (s >= to || end <= from) return;
            var item = master.CloneShallow(); item.SeriesMasterId = master.Id;
            item.Id = master.Id + ":occ:" + Minutes(original).ToString("X8"); item.Start = s; item.End = end;
            item.IsOccurrence = true; item.IsException = e != null;
            if (e != null)
            {
                if (e.Subject != null) item.Subject = e.Subject;
                if (e.Location != null) item.Location = e.Location;
                if (e.ShowAs.HasValue) item.ShowAs = e.ShowAs.Value;
                if (e.AllDay.HasValue) item.IsAllDay = e.AllDay.Value;
                if (e.ReminderSet.HasValue) item.ReminderSet = e.ReminderSet.Value;
                if (e.ReminderMinutes.HasValue) item.ReminderMinutes = e.ReminderMinutes.Value;
            }
            result.Add(item);
        }
        foreach (var date in OriginalDates(horizon))
            if (!excluded.Contains(date.Date)) Add(date.AddMinutes(StartOffset), date.AddMinutes(StartOffset), date.AddMinutes(EndOffset), null);
        foreach (var e in Exceptions) Add(e.Original, e.Start, e.End, e);
        return result.OrderBy(e => e.Start).ToList();
    }

    public byte[] WithDeletedInstance(DateTime original)
    {
        // Exception attachments must be handled separately before deleting an already modified instance.
        if (Exceptions.Any(e => e.Original.Date == original.Date)) throw new MapiException("Deleting a modified recurrence exception requires its embedded attachment update.");
        if (!Matches(original.Date)) throw new MapiException("The supplied date is not part of this recurrence.");
        var dates = Deleted.Append(original.Date).Distinct().Order().ToList();
        return EncodeBase(dates).Concat(_extension).ToArray();
    }

    private byte[] EncodeBase(IReadOnlyList<DateTime> deleted) => MapiWire.Write(w =>
    {
        w.Write((ushort)0x3004); w.Write((ushort)0x3004); w.Write(Frequency); w.Write(Pattern); w.Write((ushort)0);
        w.Write(FirstDateTime); w.Write(Period); w.Write(0u);
        if (Pattern != 0) w.Write(MaskOrDay); if (Pattern == 3) w.Write(Nth);
        w.Write(EndType); w.Write(Count); w.Write(FirstDow);
        w.Write((uint)deleted.Count); foreach (var d in deleted) w.Write(Minutes(d.Date));
        w.Write((uint)Modified.Count); foreach (var d in Modified) w.Write(Minutes(d.Date));
        w.Write(Minutes(StartDate.Date)); w.Write(Minutes(EndDate.Date));
    });

    public RecurrenceInfo ToModel() => new()
    {
        Type = Pattern switch { 0 => RecurrenceType.Daily, 1 => RecurrenceType.Weekly, 3 => Frequency == 0x200d ? RecurrenceType.YearlyRelative : RecurrenceType.MonthlyRelative,
            _ => Frequency == 0x200d ? RecurrenceType.YearlyAbsolute : RecurrenceType.MonthlyAbsolute },
        Interval = checked((int)(Pattern == 0 ? Period / 1440 : Frequency == 0x200d ? Period / 12 : Period)),
        DaysOfWeek = Pattern == 1 ? Enumerable.Range(0, 7).Where(d => (MaskOrDay & (1u << d)) != 0).Select(d => (DayOfWeek)d).ToList() : [],
        RelativeDay = Enumerable.Range(0, 7).Where(d => (MaskOrDay & (1u << d)) != 0).Select(d => (DayOfWeek)d).FirstOrDefault(),
        WeekIndex = (WeekIndex)(Nth > 0 ? Nth - 1 : 0), DayOfMonth = Pattern is 2 or 4 ? (int)MaskOrDay : StartDate.Day,
        Month = StartDate.Month, StartDate = StartDate, EndDate = EndDate,
        EndType = EndType switch { 0x2021 => RecurrenceEnd.ByDate, 0x2022 => RecurrenceEnd.AfterOccurrences, _ => RecurrenceEnd.NoEnd }, Occurrences = checked((int)Count)
    };

    public static CalendarRecurrence Create(RecurrenceInfo model, DateTime localStart, DateTime localEnd)
    {
        if (model.Type == RecurrenceType.None || model.Interval < 1 || localEnd <= localStart) throw new MapiException("Invalid recurrence interval or duration.");
        var p = new CalendarRecurrence { StartDate = model.StartDate.Date, FirstDow = 1, Count = checked((uint)model.Occurrences),
            StartOffset = checked((uint)localStart.TimeOfDay.TotalMinutes),
            EndOffset = checked((uint)(localEnd - localStart.Date).TotalMinutes),
            EndType = model.EndType switch { RecurrenceEnd.ByDate => 0x2021u, RecurrenceEnd.AfterOccurrences => 0x2022u, _ => 0x2023u },
            EndDate = model.EndType == RecurrenceEnd.NoEnd ? FromMinutes(0x5ae980df) : model.EndType == RecurrenceEnd.ByDate ? model.EndDate.Date : new DateTime(4500, 12, 31) };
        p.Frequency = model.Type switch { RecurrenceType.Daily => 0x200a, RecurrenceType.Weekly => 0x200b,
            RecurrenceType.MonthlyAbsolute or RecurrenceType.MonthlyRelative => 0x200c, _ => 0x200d };
        p.Pattern = model.Type switch { RecurrenceType.Daily => model.EveryWeekday ? (ushort)1 : (ushort)0, RecurrenceType.Weekly => 1,
            RecurrenceType.MonthlyRelative or RecurrenceType.YearlyRelative => 3, _ => 2 };
        if (model.Type == RecurrenceType.Daily && model.EveryWeekday) { p.Frequency = 0x200b; p.Period = 1; p.MaskOrDay = 0x3e; }
        else
        {
            p.Period = checked((uint)model.Interval * (p.Pattern == 0 ? 1440u : p.Frequency == 0x200d ? 12u : 1u));
            p.MaskOrDay = p.Pattern == 1 ? model.DaysOfWeek.Aggregate(0u, (mask, day) => mask | 1u << (int)day)
                : p.Pattern == 3 ? 1u << (int)model.RelativeDay : checked((uint)model.DayOfMonth);
        }
        p.Nth = p.Pattern == 3 ? (uint)model.WeekIndex + 1 : 0;
        if (p.Pattern == 0) p.FirstDateTime = Minutes(p.StartDate) % p.Period;
        else if (p.Pattern == 1)
        {
            var week = p.StartDate.AddDays(-(((int)p.StartDate.DayOfWeek - (int)p.FirstDow + 7) % 7));
            p.FirstDateTime = Minutes(week) % checked(p.Period * 10080);
        }
        else
        {
            var anchorMonth = p.Frequency == 0x200d ? model.Month : p.StartDate.Month;
            var monthIndex = checked((uint)((p.StartDate.Year - 1601) * 12 + anchorMonth - 1));
            p.FirstDateTime = Minutes(Epoch.AddMonths((int)(monthIndex % p.Period)));
        }
        p.ValidatePattern();
        if (p.EndType == 0x2022)
        {
            if (p.Count is 0 or > 100000) throw new MapiException("Invalid recurrence occurrence count.");
            var last = p.OriginalDates(p.EndDate).LastOrDefault();
            if (last == default) throw new MapiException("The recurrence has no valid occurrence.");
            p.EndDate = last;
        }
        if (p.EndDate < p.StartDate) throw new MapiException("Recurrence ends before it starts.");
        p._extension = MapiWire.Write(w =>
        { w.Write(0x3006u); w.Write(0x3009u); w.Write(p.StartOffset); w.Write(p.EndOffset); w.Write((ushort)0); w.Write(0u); w.Write(0u); });
        return p;
    }

    public byte[] ToBytes() => EncodeBase(Deleted).Concat(_extension).ToArray();
}

public sealed class CalendarException
{
    public DateTime Start, End, Original;
    public ushort Flags;
    public string? Subject, Location;
    public uint? MeetingFlags;
    public bool? ReminderSet, AllDay;
    public int? ReminderMinutes;
    public ShowAs? ShowAs;
}
