namespace DedupTool;

/// <summary>
/// Family model (design D2): a <c>docId</c> index, a <c>parentId → children</c> map,
/// and memoized family-root resolution with a cycle guard.
/// </summary>
public sealed class FamilyIndex
{
    private readonly Dictionary<string, Document> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Document>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Document> _rootCache = new(StringComparer.Ordinal);

    public FamilyIndex(IEnumerable<Document> documents)
    {
        foreach (var d in documents)
        {
            // Duplicate docIds: first occurrence wins (see design Risks — validation
            // in task 2.2 warns about them).
            _byId.TryAdd(d.DocId, d);

            if (d.ParentId is { } parentId)
            {
                if (!_children.TryGetValue(parentId, out var list))
                {
                    list = new List<Document>();
                    _children[parentId] = list;
                }

                list.Add(d);
            }
        }
    }

    /// <summary>The document with the given <c>docId</c>, or null when unknown.</summary>
    public Document? Find(string docId) =>
        _byId.TryGetValue(docId, out var d) ? d : null;

    /// <summary>Documents whose <c>parentId</c> equals <paramref name="docId"/>; empty when none.</summary>
    public IReadOnlyList<Document> ChildrenOf(string docId) =>
        _children.TryGetValue(docId, out var list) ? list : Array.Empty<Document>();

    /// <summary>
    /// Walks the <c>parentId</c> chain to the top and returns the family root
    /// (design D2). Memoized per document; every node visited on an acyclic walk
    /// is cached too. An unknown parent ends the walk at the topmost known
    /// document; a visited-set guard breaks cycles (defense-in-depth — validation
    /// rejects cycles earlier, so nothing is cached in that case).
    /// </summary>
    public Document ResolveRoot(Document doc)
    {
        if (_rootCache.TryGetValue(doc.DocId, out var cached))
        {
            return cached;
        }

        var path = new List<Document> { doc };
        var visited = new HashSet<string>(StringComparer.Ordinal) { doc.DocId };
        var current = doc;
        var cycle = false;

        while (current.ParentId is { } parentId)
        {
            if (!_byId.TryGetValue(parentId, out var parent))
            {
                break; // unknown parent — validation (task 2.2) warns; stop here
            }

            if (!visited.Add(parent.DocId))
            {
                cycle = true; // cycle guard — validation (task 2.2) rejects these
                break;
            }

            current = parent;
            path.Add(current);
        }

        if (!cycle)
        {
            foreach (var node in path)
            {
                _rootCache[node.DocId] = current;
            }
        }

        return current;
    }
}
