namespace AgentTrust.Scheduling;

/// <summary>A weekly recurrence — "every Monday at 07:30" from the doc's example. Kept
/// deliberately simple (no cron expressions) since that's all the worked example needs.</summary>
public sealed record RecurringSchedule(DayOfWeek DayOfWeek, TimeOnly TimeOfDay, TimeSpan Tolerance)
{
    public RecurringSchedule(DayOfWeek dayOfWeek, TimeOnly timeOfDay) : this(dayOfWeek, timeOfDay, TimeSpan.FromMinutes(10)) { }

    public bool IsDue(DateTimeOffset now) =>
        now.DayOfWeek == DayOfWeek &&
        Math.Abs((TimeOnly.FromDateTime(now.UtcDateTime) - TimeOfDay).TotalMinutes) <= Tolerance.TotalMinutes;

    public DateTimeOffset ScheduledOccurrence(DateTimeOffset now) =>
        new(now.Year, now.Month, now.Day, TimeOfDay.Hour, TimeOfDay.Minute, TimeOfDay.Second, now.Offset);

    public DateTimeOffset NextOccurrenceAfter(DateTimeOffset from)
    {
        var daysUntil = ((int)DayOfWeek - (int)from.DayOfWeek + 7) % 7;
        var candidate = new DateTimeOffset(from.Date.AddDays(daysUntil), from.Offset) + TimeOfDay.ToTimeSpan();
        return candidate > from ? candidate : candidate.AddDays(7);
    }
}
