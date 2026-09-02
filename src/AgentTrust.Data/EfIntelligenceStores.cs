using System.Text.Json;
using AgentTrust.Intelligence.Behaviour;
using AgentTrust.Intelligence.Investigation;
using AgentTrust.Intelligence.Learning;
using AgentTrust.Intelligence.Risk;
using Microsoft.EntityFrameworkCore;

namespace AgentTrust.Data;

/// <summary>
/// EF-Core-backed persistence for AgentTrust.Intelligence's source data: raw transaction events
/// (FinancialGraph and CustomerBehaviourProfile are both rebuilt on demand from these rows —
/// neither the graph nor a profile is itself a stored shape) and periodic profile snapshots
/// (long-term memory for behavioural-change detection). Ordering/date-range work happens after
/// materialising rows into memory, not inside the LINQ query — see EfTransactionLedger for why:
/// SQL Server and SQLite each reject a different DateTimeOffset expression shape in-query.
/// </summary>
public sealed class EfTransactionEventStore : ITransactionEventStore
{
    private readonly AgentTrustDbContext _db;
    public EfTransactionEventStore(AgentTrustDbContext db) => _db = db;

    public void Record(TransactionEvent transactionEvent)
    {
        var existing = _db.TransactionEvents.Find(transactionEvent.TransactionId);
        var entity = ToEntity(transactionEvent);
        if (existing is null) _db.TransactionEvents.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public IReadOnlyList<TransactionEvent> GetCustomerHistory(string customerId) =>
        _db.TransactionEvents.AsNoTracking().Where(e => e.CustomerId == customerId).ToList()
            .OrderBy(e => e.Timestamp).Select(ToDomain).ToList();

    public IReadOnlyList<TransactionEvent> GetMerchantHistory(string merchantId) =>
        _db.TransactionEvents.AsNoTracking().Where(e => e.MerchantId == merchantId).ToList()
            .OrderBy(e => e.Timestamp).Select(ToDomain).ToList();

    public IReadOnlyList<TransactionEvent> GetDeviceHistory(string deviceId) =>
        _db.TransactionEvents.AsNoTracking().Where(e => e.DeviceId == deviceId).ToList()
            .OrderBy(e => e.Timestamp).Select(ToDomain).ToList();

    public IReadOnlyList<TransactionEvent> GetBeneficiaryHistory(string beneficiaryId) =>
        _db.TransactionEvents.AsNoTracking().Where(e => e.BeneficiaryId == beneficiaryId).ToList()
            .OrderBy(e => e.Timestamp).Select(ToDomain).ToList();

    private static TransactionEventEntity ToEntity(TransactionEvent e) => new()
    {
        TransactionId = e.TransactionId,
        CustomerId = e.CustomerId,
        MerchantId = e.MerchantId,
        Amount = e.Amount,
        Currency = e.Currency,
        Timestamp = e.Timestamp,
        DeviceId = e.DeviceId,
        IpAddress = e.IpAddress,
        Location = e.Location,
        BeneficiaryId = e.BeneficiaryId,
        BeneficiaryCreatedAt = e.BeneficiaryCreatedAt,
        WasRefunded = e.WasRefunded,
        PriorFailedAttempts = e.PriorFailedAttempts
    };

    private static TransactionEvent ToDomain(TransactionEventEntity e) => new(
        e.TransactionId, e.CustomerId, e.MerchantId, e.Amount, e.Currency, e.Timestamp,
        e.DeviceId, e.IpAddress, e.Location, e.BeneficiaryId, e.BeneficiaryCreatedAt,
        e.WasRefunded, e.PriorFailedAttempts);
}

public sealed class EfInvestigationStateStore : IInvestigationStateStore
{
    private readonly AgentTrustDbContext _db;
    private static readonly JsonSerializerOptions JsonOptions = new();
    public EfInvestigationStateStore(AgentTrustDbContext db) => _db = db;

    public void Save(InvestigationState state)
    {
        var existing = _db.InvestigationStates.Find(state.InvestigationId);
        var entity = new InvestigationStateEntity
        {
            InvestigationId = state.InvestigationId,
            TransactionId = state.TransactionId,
            Status = state.Status.ToString(),
            StateJson = JsonSerializer.Serialize(state, JsonOptions),
            CreatedAt = state.CreatedAt,
            UpdatedAt = state.UpdatedAt
        };
        if (existing is null) _db.InvestigationStates.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public InvestigationState? Find(string investigationId)
    {
        var entity = _db.InvestigationStates.AsNoTracking().FirstOrDefault(e => e.InvestigationId == investigationId);
        return entity is null ? null : JsonSerializer.Deserialize<InvestigationState>(entity.StateJson, JsonOptions);
    }
}

public sealed class EfProfileHistoryStore : IProfileHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly AgentTrustDbContext _db;
    public EfProfileHistoryStore(AgentTrustDbContext db) => _db = db;

    public void RecordSnapshot(string entityId, CustomerBehaviourProfile profile, DateTimeOffset takenAt)
    {
        _db.ProfileSnapshots.Add(new ProfileSnapshotEntity
        {
            EntityId = entityId,
            TakenAt = takenAt,
            ProfileJson = JsonSerializer.Serialize(profile, JsonOptions)
        });
        _db.SaveChanges();
    }

    public IReadOnlyList<ProfileSnapshot> GetHistory(string entityId) =>
        _db.ProfileSnapshots.AsNoTracking().Where(s => s.EntityId == entityId).ToList()
            .OrderBy(s => s.TakenAt)
            .Select(s => new ProfileSnapshot(entityId, JsonSerializer.Deserialize<CustomerBehaviourProfile>(s.ProfileJson, JsonOptions)!, s.TakenAt))
            .ToList();

    public CustomerBehaviourProfile? GetSnapshotClosestTo(string entityId, DateTimeOffset asOf) =>
        GetHistory(entityId)
            .OrderBy(s => Math.Abs((s.TakenAt - asOf).Ticks))
            .FirstOrDefault()?.Profile;
}

public sealed class EfSemanticCaseStore : ISemanticCaseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly AgentTrustDbContext _db;

    public EfSemanticCaseStore(AgentTrustDbContext db) => _db = db;

    public void Upsert(SemanticCaseRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var existing = _db.SemanticCases.Find(record.Case.CaseId);
        var entity = new SemanticCaseEntity
        {
            CaseId = record.Case.CaseId,
            ScopeId = record.Case.ScopeId,
            Title = record.Case.Title,
            Narrative = record.Case.Narrative,
            Outcome = record.Case.Outcome,
            TagsJson = JsonSerializer.Serialize(record.Case.Tags, JsonOptions),
            EmbeddingJson = JsonSerializer.Serialize(record.Embedding, JsonOptions),
            ResolvedAt = record.Case.ResolvedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        if (existing is null) _db.SemanticCases.Add(entity);
        else _db.Entry(existing).CurrentValues.SetValues(entity);
        _db.SaveChanges();
    }

    public IReadOnlyList<SemanticCaseRecord> GetByScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return _db.SemanticCases.AsNoTracking()
            .Where(e => e.ScopeId == scopeId || e.ScopeId == "global")
            .ToList()
            .Select(e => new SemanticCaseRecord(
                new HistoricalCaseMemory(e.CaseId, e.Title, e.Narrative, e.Outcome,
                    JsonSerializer.Deserialize<List<string>>(e.TagsJson, JsonOptions) ?? [], e.ScopeId, e.ResolvedAt),
                JsonSerializer.Deserialize<List<float>>(e.EmbeddingJson, JsonOptions) ?? []))
            .ToList();
    }
}

public sealed class EfOutcomeStore : IOutcomeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly AgentTrustDbContext _db;
    public EfOutcomeStore(AgentTrustDbContext db) => _db = db;

    public void Record(DecisionFeedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        feedback.Validate();
        if (_db.DecisionFeedback.Any(e => e.TransactionId == feedback.TransactionId))
            throw new InvalidOperationException($"Feedback already exists for transaction '{feedback.TransactionId}'.");
        _db.DecisionFeedback.Add(ToEntity(feedback));
        _db.SaveChanges();
    }

    public void SetValidation(string transactionId, OutcomeValidationStatus status, string validatorId, DateTimeOffset validatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(validatorId);
        if (status == OutcomeValidationStatus.Pending) throw new ArgumentException("A validation decision cannot restore Pending status.", nameof(status));
        var entity = _db.DecisionFeedback.Find(transactionId)
            ?? throw new KeyNotFoundException($"No feedback exists for transaction '{transactionId}'.");
        var updated = ToDomain(entity) with { ValidationStatus = status, ValidatedBy = validatorId, ValidatedAt = validatedAt };
        updated.Validate();
        entity.ValidationStatus = status.ToString();
        entity.ValidatedBy = validatorId;
        entity.ValidatedAt = validatedAt;
        _db.SaveChanges();
    }

    public IReadOnlyList<DecisionFeedback> GetAll() =>
        _db.DecisionFeedback.AsNoTracking().ToList().OrderBy(e => e.RecordedAt).Select(ToDomain).ToList();

    public IReadOnlyList<DecisionFeedback> GetCurated() =>
        _db.DecisionFeedback.AsNoTracking().Where(e => e.ValidationStatus == nameof(OutcomeValidationStatus.Validated))
            .ToList().OrderBy(e => e.RecordedAt).Select(ToDomain).ToList();

    private static DecisionFeedbackEntity ToEntity(DecisionFeedback f) => new()
    {
        TransactionId = f.TransactionId, InvestigationId = f.InvestigationId,
        AiRecommendation = f.AiRecommendation.ToString(), AgentConfidence = f.AgentConfidence,
        ActualOutcome = f.ActualOutcome.ToString(), HumanConfidence = f.HumanConfidence, Notes = f.Notes,
        ReasonCodesJson = JsonSerializer.Serialize(f.ReasonCodes ?? [], JsonOptions),
        UsefulEvidenceIdsJson = JsonSerializer.Serialize(f.UsefulEvidenceIds ?? [], JsonOptions),
        MisleadingEvidenceIdsJson = JsonSerializer.Serialize(f.MisleadingEvidenceIds ?? [], JsonOptions),
        Source = f.Source.ToString(), ValidationStatus = f.ValidationStatus.ToString(),
        ValidatedBy = f.ValidatedBy, ValidatedAt = f.ValidatedAt, RecordedAt = f.RecordedAt
    };

    private static DecisionFeedback ToDomain(DecisionFeedbackEntity e) => new(
        e.TransactionId, Enum.Parse<IntelligenceRecommendation>(e.AiRecommendation), Enum.Parse<ActualOutcome>(e.ActualOutcome),
        e.Notes, e.RecordedAt, e.InvestigationId, e.AgentConfidence, e.HumanConfidence,
        JsonSerializer.Deserialize<List<string>>(e.ReasonCodesJson, JsonOptions) ?? [],
        JsonSerializer.Deserialize<List<string>>(e.UsefulEvidenceIdsJson, JsonOptions) ?? [],
        JsonSerializer.Deserialize<List<string>>(e.MisleadingEvidenceIdsJson, JsonOptions) ?? [],
        Enum.Parse<OutcomeSource>(e.Source), Enum.Parse<OutcomeValidationStatus>(e.ValidationStatus), e.ValidatedBy, e.ValidatedAt);
}
