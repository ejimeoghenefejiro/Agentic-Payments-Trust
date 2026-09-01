using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentTrust.Core.Models;

namespace AgentTrust.Evidence;

public sealed record ChainedAuditRecord(AuditRecord Record, string PreviousHash, string CurrentHash, int SequenceNumber);

public sealed record AuditChainVerificationResult(bool IsValid, IReadOnlyList<string> Breaks);

/// <summary>
/// Append-only, hash-chained audit ledger. Each entry's hash covers the previous entry's
/// hash plus the canonicalised record, so changing, deleting or reordering any past entry
/// is detectable by Verify(). This is in-memory for the MVP; a persistent implementation
/// (Priority 4) should store ChainedAuditRecord rows and rehydrate via LoadExisting so the
/// same Verify() logic can validate data coming back from storage.
/// </summary>
public sealed class AuditLedger
{
    public const string GenesisHash = "sha256:genesis";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly List<ChainedAuditRecord> _entries = new();
    private readonly IAuditRecordStore? _persistentStore;

    public AuditLedger() { }

    /// <summary>
    /// Backs this ledger with a persistent store and immediately loads its existing entries,
    /// so a fresh AuditLedger instance (e.g. one built per HTTP request scope) continues the
    /// correct global chain instead of restarting sequence numbers at zero.
    /// </summary>
    public AuditLedger(IAuditRecordStore persistentStore)
    {
        _persistentStore = persistentStore;
        _entries.AddRange(persistentStore.LoadAll());
    }

    public IReadOnlyList<ChainedAuditRecord> Entries => _entries;

    public ChainedAuditRecord Append(AuditRecord record)
    {
        var previousHash = _entries.Count == 0 ? GenesisHash : _entries[^1].CurrentHash;
        var currentHash = ComputeHash(previousHash, record);
        var entry = new ChainedAuditRecord(record, previousHash, currentHash, _entries.Count);
        _entries.Add(entry);
        _persistentStore?.Append(entry);
        return entry;
    }

    /// <summary>
    /// Rehydrates a ledger from previously stored entries (e.g. rows read back from a
    /// database) so Verify() can be run against data that may have been tampered with
    /// outside this process.
    /// </summary>
    public static AuditLedger LoadExisting(IEnumerable<ChainedAuditRecord> entries)
    {
        var ledger = new AuditLedger();
        ledger._entries.AddRange(entries);
        return ledger;
    }

    public AuditChainVerificationResult Verify()
    {
        var breaks = new List<string>();
        var expectedPreviousHash = GenesisHash;
        var expectedSequence = 0;

        foreach (var entry in _entries)
        {
            if (entry.SequenceNumber != expectedSequence)
            {
                breaks.Add($"Sequence {expectedSequence}: expected sequence number {expectedSequence} but found {entry.SequenceNumber} (reordering or deletion detected)");
            }

            if (entry.PreviousHash != expectedPreviousHash)
            {
                breaks.Add($"Sequence {entry.SequenceNumber} (tx {entry.Record.TransactionId}): previousHash does not match prior entry's currentHash (chain broken — record deleted, inserted, or reordered)");
            }

            var recomputed = ComputeHash(entry.PreviousHash, entry.Record);
            if (recomputed != entry.CurrentHash)
            {
                breaks.Add($"Sequence {entry.SequenceNumber} (tx {entry.Record.TransactionId}): stored hash does not match recomputed hash (record content changed after being written)");
            }

            expectedPreviousHash = entry.CurrentHash;
            expectedSequence++;
        }

        return new AuditChainVerificationResult(breaks.Count == 0, breaks);
    }

    private static string ComputeHash(string previousHash, AuditRecord record)
    {
        var canonical = JsonSerializer.Serialize(record, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(previousHash + canonical);
        var hash = SHA256.HashData(bytes);
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
