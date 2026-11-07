using System.Globalization;
using System.Text.RegularExpressions;

namespace DedupTool;

/// <summary>
/// Validation pass (design D1 phase 1, D5): identifies invalid documents and
/// produces one ordered warning per invalid doc, naming the docId and reason.
/// Checks per document, first failure wins (in this order): empty docId →
/// duplicate docId → unknown parentId → parentId cycle → empty sha256 →
/// email sentDate. Warnings follow input order so a given input file always
/// produces the same warnings (§6.1). Invalid documents are *reported* here;
/// excluding them and their descendants happens in a later phase (task 2.3).
/// </summary>
public static partial class Validator
{
    /// <summary>
    /// ISO 8601 date-time with a required offset: <c>yyyy-MM-ddTHH:mm:ss[.f…](Z|±hh:mm)</c>.
    /// Offset-less timestamps are rejected on purpose: <c>DateTimeOffset.TryParse</c>
    /// would otherwise assume the machine's local zone — a nondeterminism trap (§6.1,
    /// design D4). Uppercase <c>Z</c> and colon-form offsets only (design D7 formats).
    /// </summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffsetRegex();

    /// <summary>Warnings only, in input order (design D5).</summary>
    public static List<ValidationWarning> Validate(IReadOnlyList<Document> documents) =>
        Analyze(documents).Select(issue => issue.Warning).ToList();

    /// <summary>
    /// Warnings plus the input position of each invalid document, so the
    /// pipeline can exclude exactly those documents (task 2.3). One issue per
    /// invalid document, in input order.
    /// </summary>
    public static List<ValidationIssue> Analyze(IReadOnlyList<Document> documents)
    {
        // First occurrence wins for lookups (matches FamilyIndex). Presence is what
        // the unknown-parent check tests; whether the found doc is itself valid is
        // a separate question (its own warning, and exclusion in task 2.3).
        var byId = new Dictionary<string, Document>(StringComparer.Ordinal);
        foreach (var d in documents)
        {
            if (!string.IsNullOrEmpty(d.DocId))
            {
                byId.TryAdd(d.DocId, d);
            }
        }

        var issues = new List<ValidationIssue>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var cycleMemo = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (var i = 0; i < documents.Count; i++)
        {
            var d = documents[i];

            // "Already used by an earlier document": any earlier non-empty docId
            // counts, even when that earlier document is itself invalid.
            var isDuplicate = !string.IsNullOrEmpty(d.DocId) && !seenIds.Add(d.DocId);

            var reason = FirstProblem(d, isDuplicate, byId, cycleMemo);
            if (reason is not null)
            {
                issues.Add(new ValidationIssue(i, new ValidationWarning(d.DocId, reason)));
            }
        }

        return issues;
    }

    private static string? FirstProblem(
        Document d,
        bool isDuplicate,
        Dictionary<string, Document> byId,
        Dictionary<string, bool> cycleMemo)
    {
        if (string.IsNullOrWhiteSpace(d.DocId))
        {
            return "empty docId";
        }

        if (isDuplicate)
        {
            return "duplicate docId";
        }

        if (d.ParentId is { } parentId && !byId.ContainsKey(parentId))
        {
            return $"unknown parentId \"{parentId}\"";
        }

        if (d.ParentId is not null && ChainContainsCycle(d, byId, cycleMemo))
        {
            return "parentId cycle";
        }

        if (string.IsNullOrWhiteSpace(d.Sha256))
        {
            return "empty sha256";
        }

        if (string.Equals(d.DocType, "email", StringComparison.Ordinal))
        {
            return SentDateProblem(d.SentDate);
        }

        return null;
    }

    private static string? SentDateProblem(string? sentDate)
    {
        if (string.IsNullOrEmpty(sentDate))
        {
            return "missing sentDate";
        }

        if (!IsIsoWithOffset(sentDate))
        {
            return $"sentDate not ISO 8601 with offset: \"{sentDate}\"";
        }

        return null;
    }

    private static bool IsIsoWithOffset(string value) =>
        IsoWithOffsetRegex().IsMatch(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>
    /// True when walking <paramref name="doc"/>'s parentId chain revisits a node —
    /// i.e. the chain contains a cycle (the doc itself needn't be in the cycle).
    /// Termination: no parent, unknown parent, or a memoized answer. Results are
    /// memoized for every node on the walked path, so a batch costs O(n) total
    /// even for deep chains (design D1: cycles are rejected here, before grouping).
    /// </summary>
    private static bool ChainContainsCycle(
        Document doc,
        Dictionary<string, Document> byId,
        Dictionary<string, bool> memo)
    {
        if (memo.TryGetValue(doc.DocId, out var known))
        {
            return known;
        }

        var path = new List<string> { doc.DocId };
        var onPath = new HashSet<string>(StringComparer.Ordinal) { doc.DocId };
        var current = doc;
        bool result;

        while (true)
        {
            if (current.ParentId is not { } parentId)
            {
                result = false; // reached a root
                break;
            }

            if (!byId.TryGetValue(parentId, out var parent))
            {
                result = false; // unknown parent — reported by its own check
                break;
            }

            if (memo.TryGetValue(parent.DocId, out var memoized))
            {
                result = memoized;
                break;
            }

            if (!onPath.Add(parent.DocId))
            {
                result = true; // revisited a node on this path → cycle
                break;
            }

            path.Add(parent.DocId);
            current = parent;
        }

        foreach (var id in path)
        {
            memo[id] = result;
        }

        return result;
    }
}
