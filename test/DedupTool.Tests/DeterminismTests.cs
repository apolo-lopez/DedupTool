using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Task 4.4 — determinism at the Deduper level (dedup spec: "suppression never
/// depends on input order"). For any permutation of a valid batch the surviving
/// set, every computed FamilyID/SentDate value and the suppressed count must be
/// identical. Row ORDER is deliberately not asserted here: rows are emitted in
/// input order until task 6.3 sorts them, so byte-identical files are 6.3/8.2's
/// verify item — comparing the docId-sorted projections below is the property
/// 4.4 owns, and it is exactly what makes 6.3's sort sufficient.
/// </summary>
public class DeterminismTests
{
    /// <summary>
    /// Every emitted row's full projection, ordered by docId — the order-sensitive
    /// part of the row set (docId order = the 6.3 writer order) so equality here
    /// means "same rows, same FamilyID/SentDate/parent/custodian/type/name/hash".
    /// </summary>
    private static string[] Projection(DedupResult result) =>
        result.Rows
            .OrderBy(r => r.DocId, StringComparer.Ordinal)
            .Select(r => string.Join('|',
                r.DocId, r.FamilyId, r.ParentId, r.Custodian, r.DocType, r.FileName, r.SentDate, r.Hash))
            .ToArray();

    private static DedupResult Run(IEnumerable<Document> documents) =>
        Deduper.Run(new Batch { Documents = documents.ToList() });

    [Fact]
    public void SampleBatch_ShuffledRuns_ProduceIdenticalProjections()
    {
        var batch = TestPaths.LoadSampleBatch();
        var original = batch.Documents.ToList();
        var baseline = Deduper.Run(batch);

        // Premise guard: the permutations below must actually reorder the input,
        // otherwise this test would pass without exercising anything.
        Assert.NotEqual(
            original.Select(d => d.DocId),
            TestShuffle.DeterministicShuffle(original, seed: 1).Select(d => d.DocId));

        foreach (var seed in new[] { 1, 2, 424242 })
        {
            var shuffled = TestShuffle.DeterministicShuffle(original, seed);

            // Same multiset of documents, different order.
            Assert.Equal(
                original.Select(d => d.DocId).OrderBy(id => id, StringComparer.Ordinal),
                shuffled.Select(d => d.DocId).OrderBy(id => id, StringComparer.Ordinal));

            var result = Run(shuffled);

            Assert.Equal(baseline.Suppressed, result.Suppressed);
            Assert.Equal(baseline.Rows.Count, result.Rows.Count);
            Assert.Equal(Projection(baseline), Projection(result));
            Assert.Empty(result.Warnings); // valid batch stays warning-free in every order
        }
    }

    [Fact]
    public void MixedBatch_ShuffledRuns_ProduceIdenticalProjections()
    {
        // One batch exercising every suppression seed at once: cross-mailbox email
        // pair with whole families (root + attachment + attached email + grandchild),
        // a same-parent attachment duplicate, and a loose duplicate.
        var winner = TestDocuments.Email("DOC-0001", "alice", "shaP1",
            sentDate: "2024-03-05T14:22:00-05:00",
            from: "Alice.Manager@Corp-Example.com",
            to: new[] { "Bob@Corp-Example.com", "Carol@Corp-Example.com" },
            subject: "Q1 Status Update",
            body: "line1\nline2");
        var loser = TestDocuments.Email("DOC-0009", "bob", "shaP9",
            sentDate: "2024-03-05T19:22:00Z",
            from: "alice.manager@corp-example.com",
            to: new[] { "Carol@Corp-Example.com", "Bob@Corp-Example.com" },
            subject: "Q1  Status Update",
            body: "line1\r\nline2");

        var docs = new List<Document>
        {
            winner,
            TestDocuments.Attachment("DOC-0002", "alice", "shaDup", "DOC-0001"),
            TestDocuments.Attachment("DOC-0003", "alice", "shaDup", "DOC-0001"), // same-parent dup
            TestDocuments.Email("DOC-0004", "alice", "shaE1", parentId: "DOC-0001",
                from: "alice.manager@corp-example.com",
                to: new[] { "bob@corp-example.com", "carol@corp-example.com" },
                subject: "Q1 Status Update",
                body: "line1\nline2"),
            loser,
            TestDocuments.Attachment("DOC-0010", "bob", "shaDup", "DOC-0009"), // other parent → kept
            TestDocuments.Email("DOC-0011", "bob", "shaE9", parentId: "DOC-0009",
                from: "ALICE.MANAGER@CORP-EXAMPLE.COM",
                to: new[] { "Bob@Corp-Example.com", "Carol@Corp-Example.com" },
                subject: "Q1 Status Update",
                body: "line1\nline2"),
            TestDocuments.Attachment("DOC-0012", "bob", "shaDup2", "DOC-0010"), // grandchild
            TestDocuments.Loose("DOC-0005", "alice", "shaL"),
            TestDocuments.Loose("DOC-0006", "bob", "shaL"), // loose dup
            TestDocuments.Email("DOC-0007", "carol", "shaP7",
                subject: "Unrelated", body: "Untouched family."),
            TestDocuments.Attachment("DOC-0008", "carol", "shaDup", "DOC-0007"),
        };

        var baseline = Run(docs);
        var baselineProjection = Projection(baseline);

        foreach (var seed in new[] { 7, 11, 99, 12345 })
        {
            var shuffled = TestShuffle.DeterministicShuffle(docs, seed);
            Assert.NotEqual(docs.Select(d => d.DocId), shuffled.Select(d => d.DocId));

            var result = Run(shuffled);

            Assert.Equal(baseline.Suppressed, result.Suppressed);
            Assert.Equal(Projection(baseline), Projection(result));
        }

        // Sanity on the batch itself, so "deterministic" cannot mean "deterministically wrong":
        // the winner family survives whole (root + attachment-dup survivor + attached
        // email), the loser family falls wholesale (root, attachment, attached email,
        // grandchild), the unrelated family is untouched, and DOC-0005 wins the loose pair.
        Assert.Equal(
            new[] { "DOC-0001", "DOC-0002", "DOC-0004", "DOC-0005", "DOC-0007", "DOC-0008" },
            baselineProjection.Select(p => p.Split('|')[0]).ToArray());
        Assert.Equal(6, baseline.Suppressed); // DOC-0003, DOC-0006, DOC-0009, DOC-0010, DOC-0011, DOC-0012
        Assert.Empty(baseline.Warnings);
    }
}
