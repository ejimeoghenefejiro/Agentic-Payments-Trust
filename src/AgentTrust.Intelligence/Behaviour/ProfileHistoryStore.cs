namespace AgentTrust.Intelligence.Behaviour;

public sealed record ProfileSnapshot(string EntityId, CustomerBehaviourProfile Profile, DateTimeOffset TakenAt);

/// <summary>
/// "Long-term memory": periodic behaviour-profile snapshots for an entity over time, rather than
/// only ever holding the single most recent profile. This is what makes behavioural-change
/// detection (BehaviourDeviationService.CompareCustomerProfiles) meaningful — it needs an older
/// snapshot to compare the current one against, not just "the profile" as a single fixed fact.
/// In-memory here, but shaped so a real deployment could back it with the same kind of
/// EF-Core store already used for the trust layer's persistence (AgentTrust.Data).
/// </summary>
public interface IProfileHistoryStore
{
    void RecordSnapshot(string entityId, CustomerBehaviourProfile profile, DateTimeOffset takenAt);
    IReadOnlyList<ProfileSnapshot> GetHistory(string entityId);
    CustomerBehaviourProfile? GetSnapshotClosestTo(string entityId, DateTimeOffset asOf);
}

public sealed class InMemoryProfileHistoryStore : IProfileHistoryStore
{
    private readonly List<ProfileSnapshot> _snapshots = new();

    public void RecordSnapshot(string entityId, CustomerBehaviourProfile profile, DateTimeOffset takenAt) =>
        _snapshots.Add(new ProfileSnapshot(entityId, profile, takenAt));

    public IReadOnlyList<ProfileSnapshot> GetHistory(string entityId) =>
        _snapshots.Where(s => s.EntityId == entityId).OrderBy(s => s.TakenAt).ToList();

    public CustomerBehaviourProfile? GetSnapshotClosestTo(string entityId, DateTimeOffset asOf) =>
        GetHistory(entityId)
            .OrderBy(s => Math.Abs((s.TakenAt - asOf).Ticks))
            .FirstOrDefault()?.Profile;
}
