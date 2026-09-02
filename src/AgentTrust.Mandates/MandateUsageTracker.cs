namespace AgentTrust.Mandates;

public enum SpendReservationStatus { Reserved, Committed, Released }
public sealed record MandateSpendReservation(string ReservationId, string MandateId, string ExecutionId,
    decimal Amount, DateTimeOffset ReservedAt, SpendReservationStatus Status);

public interface IMandateUsageTracker
{
    void RecordSpend(string mandateId, decimal amount, DateTimeOffset when);
    decimal AmountSpentSince(string mandateId, DateTimeOffset since);
    bool TryReserve(FinancialMandate mandate, string executionId, decimal amount, DateTimeOffset now,
        out MandateSpendReservation? reservation, out IReadOnlyList<string> reasons, bool oneOffLimitOverride = false);
    bool Commit(string reservationId);
    bool Release(string reservationId);
}

public sealed class InMemoryMandateUsageTracker : IMandateUsageTracker
{
    private readonly object _gate = new();
    private readonly List<(string MandateId, decimal Amount, DateTimeOffset When)> _committed = new();
    private readonly Dictionary<string, MandateSpendReservation> _reservations = new();

    public void RecordSpend(string mandateId, decimal amount, DateTimeOffset when)
    { if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount)); lock (_gate) _committed.Add((mandateId, amount, when)); }
    public decimal AmountSpentSince(string mandateId, DateTimeOffset since)
    { lock (_gate) return _committed.Where(r => r.MandateId == mandateId && r.When >= since).Sum(r => r.Amount); }

    public bool TryReserve(FinancialMandate mandate, string executionId, decimal amount, DateTimeOffset now,
        out MandateSpendReservation? reservation, out IReadOnlyList<string> reasons, bool oneOffLimitOverride = false)
    {
        lock (_gate)
        {
            var failures = new List<string>();
            if (amount <= 0) failures.Add("AMOUNT_MUST_BE_POSITIVE");
            if (_reservations.Values.Any(r => r.ExecutionId == executionId && r.Status != SpendReservationStatus.Released))
                failures.Add("EXECUTION_ALREADY_RESERVED");
            if (!oneOffLimitOverride)
            {
                CheckLimit(mandate.DailyLimit, new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset), "DAILY_LIMIT_EXCEEDED");
                CheckLimit(mandate.WeeklyLimit, now.AddDays(-7), "WEEKLY_LIMIT_EXCEEDED");
                CheckLimit(mandate.MonthlyLimit, now.AddMonths(-1), "MONTHLY_LIMIT_EXCEEDED");
            }
            if (failures.Count > 0) { reservation = null; reasons = failures; return false; }
            reservation = new MandateSpendReservation($"res_{Guid.NewGuid():N}", mandate.MandateId, executionId,
                amount, now, SpendReservationStatus.Reserved);
            _reservations[reservation.ReservationId] = reservation;
            reasons = Array.Empty<string>();
            return true;

            void CheckLimit(decimal? limit, DateTimeOffset since, string code)
            {
                if (limit is null) return;
                var committed = _committed.Where(r => r.MandateId == mandate.MandateId && r.When >= since).Sum(r => r.Amount);
                var reserved = _reservations.Values.Where(r => r.MandateId == mandate.MandateId
                    && r.Status == SpendReservationStatus.Reserved && r.ReservedAt >= since).Sum(r => r.Amount);
                if (committed + reserved + amount > limit.Value) failures.Add(code);
            }
        }
    }

    public bool Commit(string reservationId)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservationId, out var r) || r.Status != SpendReservationStatus.Reserved) return false;
            _reservations[reservationId] = r with { Status = SpendReservationStatus.Committed };
            _committed.Add((r.MandateId, r.Amount, r.ReservedAt)); return true;
        }
    }
    public bool Release(string reservationId)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservationId, out var r) || r.Status != SpendReservationStatus.Reserved) return false;
            _reservations[reservationId] = r with { Status = SpendReservationStatus.Released }; return true;
        }
    }
}
