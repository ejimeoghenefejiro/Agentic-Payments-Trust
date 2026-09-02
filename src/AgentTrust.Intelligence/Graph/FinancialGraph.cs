namespace AgentTrust.Intelligence.Graph;

public enum GraphNodeType
{
    Customer,
    Device,
    Merchant,
    Beneficiary,
    Account,
    SettlementAccount,
    IpAddress
}

public sealed record GraphNode(string NodeId, GraphNodeType Type);

public sealed record GraphEdge(string FromNodeId, string ToNodeId, string RelationshipType, int Weight);

/// <summary>
/// The doc's section-7 relationship model: transactions are not always independent — a customer
/// connects to devices, accounts, cards, beneficiaries and merchants; merchants connect to
/// customer accounts, devices and a settlement account. Deliberately a small in-memory graph
/// (no graph database) since the point here is the analysis, not the storage engine.
/// </summary>
public sealed class FinancialGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly Dictionary<(string From, string To, string Relationship), GraphEdge> _edges = new();

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<GraphEdge> Edges => _edges.Values;

    public void AddNode(string nodeId, GraphNodeType type) => _nodes.TryAdd(nodeId, new GraphNode(nodeId, type));

    public void AddEdge(string fromNodeId, string toNodeId, string relationshipType, int weight = 1)
    {
        var key = (fromNodeId, toNodeId, relationshipType);
        _edges[key] = _edges.TryGetValue(key, out var existing)
            ? existing with { Weight = existing.Weight + weight }
            : new GraphEdge(fromNodeId, toNodeId, relationshipType, weight);
    }

    public GraphNode? FindNode(string nodeId) => _nodes.GetValueOrDefault(nodeId);

    public IReadOnlyList<GraphEdge> EdgesFrom(string nodeId, string? relationshipType = null) =>
        _edges.Values.Where(e => e.FromNodeId == nodeId && (relationshipType is null || e.RelationshipType == relationshipType)).ToList();

    public IReadOnlyList<GraphEdge> EdgesTo(string nodeId, string? relationshipType = null) =>
        _edges.Values.Where(e => e.ToNodeId == nodeId && (relationshipType is null || e.RelationshipType == relationshipType)).ToList();

    /// <summary>Distinct nodes reachable from a starting node via edges of the given
    /// relationship type, up to maxHops away — used to answer "how many devices does this
    /// merchant's customer base actually use" without hand-writing a join for every query shape.</summary>
    public IReadOnlyList<string> Neighbors(string startNodeId, string relationshipType, int maxHops = 1)
    {
        var visited = new HashSet<string> { startNodeId };
        var frontier = new List<string> { startNodeId };

        for (var hop = 0; hop < maxHops; hop++)
        {
            var next = new List<string>();
            foreach (var nodeId in frontier)
            {
                foreach (var edge in EdgesFrom(nodeId, relationshipType))
                {
                    if (visited.Add(edge.ToNodeId))
                    {
                        next.Add(edge.ToNodeId);
                    }
                }
            }
            frontier = next;
            if (frontier.Count == 0) break;
        }

        visited.Remove(startNodeId);
        return visited.ToList();
    }
}
