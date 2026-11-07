using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Group 5 — row projection. This file starts with FamilyID (task 5.1): every
/// emitted row carries the RESOLVED family root's docId, not its immediate
/// parent's. SentDate projection is tasks 5.2/5.3 and is deliberately not
/// asserted here.
/// </summary>
public class RowProjectionTests
{
    private static Dictionary<string, string> FamilyByDocId(DedupResult result) =>
        result.Rows.ToDictionary(r => r.DocId, r => r.FamilyId, StringComparer.Ordinal);

    [Fact]
    public void NestedChain_EveryRowCarriesTheRootEmailDocId()
    {
        // email → attachment → attachment: the current bug reports DOC-000002 as the
        // grandchild's FamilyID (immediate parent); the root is DOC-000001.
        var root = TestDocuments.Email("DOC-000001", "alice", "shaRoot", sentDate: "2024-03-05T19:22:00Z");
        var child = TestDocuments.Attachment("DOC-000002", "alice", "shaChild", "DOC-000001");
        var grandchild = TestDocuments.Attachment("DOC-000003", "alice", "shaGrandchild", "DOC-000002");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { root, child, grandchild },
        });

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);

        var families = FamilyByDocId(result);
        Assert.Equal("DOC-000001", families["DOC-000001"]); // root is its own family
        Assert.Equal("DOC-000001", families["DOC-000002"]); // direct child → parent email
        Assert.Equal("DOC-000001", families["DOC-000003"]); // nested child → ROOT email, not DOC-000002
    }

    [Fact]
    public void SimpleAttachment_CarriesItsParentEmailDocId()
    {
        var email = TestDocuments.Email("DOC-000010", "alice", "shaMail", sentDate: "2024-03-05T19:22:00Z");
        var attachment = TestDocuments.Attachment("DOC-000011", "alice", "shaAtt", "DOC-000010");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email, attachment },
        });

        var families = FamilyByDocId(result);
        Assert.Equal("DOC-000010", families["DOC-000010"]);
        Assert.Equal("DOC-000010", families["DOC-000011"]);
    }

    [Fact]
    public void LooseFile_IsItsOwnFamily()
    {
        var loose = TestDocuments.Loose("DOC-000020", "alice", "shaLoose");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { loose },
        });

        var row = Assert.Single(result.Rows);
        Assert.Equal("DOC-000020", row.FamilyId);
        Assert.Equal("DOC-000020", row.DocId);
    }

    [Fact]
    public void NestedChain_SurvivesShuffled_RootFamilyUnchanged()
    {
        // FamilyID must not depend on processing order (4.4's property applied to 5.1):
        // the root may be seen after its grandchildren in any permutation.
        var docs = new List<Document>
        {
            TestDocuments.Attachment("DOC-000002", "alice", "shaChild", "DOC-000001"),
            TestDocuments.Attachment("DOC-000003", "alice", "shaGrandchild", "DOC-000002"),
            TestDocuments.Email("DOC-000001", "alice", "shaRoot", sentDate: "2024-03-05T19:22:00Z"),
            TestDocuments.Loose("DOC-000004", "bob", "shaLoose"),
        };

        var baseline = FamilyByDocId(Deduper.Run(new Batch { Documents = docs.ToList() }));

        foreach (var seed in new[] { 3, 31, 31415 })
        {
            var shuffled = TestShuffle.DeterministicShuffle(docs, seed);
            Assert.Equal(baseline, FamilyByDocId(Deduper.Run(new Batch { Documents = shuffled })));
        }

        Assert.Equal("DOC-000001", baseline["DOC-000001"]);
        Assert.Equal("DOC-000001", baseline["DOC-000002"]);
        Assert.Equal("DOC-000001", baseline["DOC-000003"]);
        Assert.Equal("DOC-000004", baseline["DOC-000004"]); // loose stays its own family
    }

    // --- Task 5.2: SentDate is always a UTC instant, exactly yyyy-MM-ddTHH:mm:ssZ ---

    /// <summary>Runs a one-email batch and returns that row's projected SentDate.</summary>
    private static string SingleRowSentDate(string sentDate)
    {
        var email = TestDocuments.Email("DOC-000001", "alice", "shaMail", sentDate: sentDate);
        var result = Deduper.Run(new Batch { Documents = new() { email } });
        var row = Assert.Single(result.Rows);
        Assert.Empty(result.Warnings);
        return row.SentDate;
    }

    [Fact]
    public void OffsetTimestamp_IsConvertedToUtc()
    {
        // The load-file spec's own example: 14:22:00-05:00 → 19:22:00Z.
        Assert.Equal("2024-03-05T19:22:00Z", SingleRowSentDate("2024-03-05T14:22:00-05:00"));
    }

    [Fact]
    public void ZuluTimestamp_PassesThroughUnchanged()
    {
        Assert.Equal("2024-03-05T19:22:00Z", SingleRowSentDate("2024-03-05T19:22:00Z"));
    }

    [Fact]
    public void FractionalSeconds_AreTruncated()
    {
        Assert.Equal("2024-03-05T19:22:00Z", SingleRowSentDate("2024-03-05T19:22:00.9876543Z"));
        // Fraction combined with an offset: convert first, then truncate.
        Assert.Equal("2024-03-05T19:22:00Z", SingleRowSentDate("2024-03-05T14:22:00.5-05:00"));
    }

    [Fact]
    public void OffsetConversion_CanRollTheDateForward()
    {
        Assert.Equal("2024-03-06T04:30:00Z", SingleRowSentDate("2024-03-05T23:30:00-05:00"));
    }

    [Fact]
    public void OffsetLessTimestamp_IsRejectedUpstream_NoRowAndOneWarning()
    {
        // Offset-less input would be parsed in the machine's local zone — the §6.1
        // nondeterminism trap. Validation rejects it, so no row exists to format.
        var email = TestDocuments.Email("DOC-000001", "alice", "shaMail", sentDate: "2024-03-05T19:22:00");
        var result = Deduper.Run(new Batch { Documents = new() { email } });

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.Suppressed);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("DOC-000001", warning.DocId);
        Assert.Contains("sentDate not ISO 8601 with offset", warning.Reason);
    }

    // --- Task 5.3: every child carries the family ROOT email's date; loose stays empty ---

    [Fact]
    public void NestedChain_EveryChildCarriesTheRootEmailDate()
    {
        var root = TestDocuments.Email("DOC-000001", "alice", "shaRoot", sentDate: "2024-03-05T14:22:00-05:00");
        var child = TestDocuments.Attachment("DOC-000002", "alice", "shaChild", "DOC-000001");
        var grandchild = TestDocuments.Attachment("DOC-000003", "alice", "shaGrandchild", "DOC-000002");

        var result = Deduper.Run(new Batch { Documents = new() { root, child, grandchild } });
        Assert.Empty(result.Warnings);

        Assert.All(result.Rows, row => Assert.Equal("2024-03-05T19:22:00Z", row.SentDate));
        // The grandchild's own parent (an attachment) carries no timestamp: without the
        // root-based rule its cell is empty — the bug this task fixes.
        Assert.Equal("2024-03-05T19:22:00Z", result.Rows.Single(r => r.DocId == "DOC-000003").SentDate);
    }

    [Fact]
    public void ChildOfAnEmail_CarriesThatEmailsDate()
    {
        var email = TestDocuments.Email("DOC-000010", "alice", "shaMail", sentDate: "2024-03-06T09:10:00-05:00");
        var attachment = TestDocuments.Attachment("DOC-000011", "alice", "shaAtt", "DOC-000010");

        var result = Deduper.Run(new Batch { Documents = new() { email, attachment } });

        Assert.All(result.Rows, row => Assert.Equal("2024-03-06T14:10:00Z", row.SentDate));
    }

    [Fact]
    public void LooseFile_HasAnEmptyDate()
    {
        var loose = TestDocuments.Loose("DOC-000020", "alice", "shaLoose");

        var row = Assert.Single(Deduper.Run(new Batch { Documents = new() { loose } }).Rows);
        Assert.Equal("", row.SentDate);
    }

    [Fact]
    public void TopLevelEmail_KeepsItsOwnDate()
    {
        var email = TestDocuments.Email("DOC-000030", "alice", "shaMail", sentDate: "2024-03-07T16:00:00Z");

        var row = Assert.Single(Deduper.Run(new Batch { Documents = new() { email } }).Rows);
        Assert.Equal("2024-03-07T16:00:00Z", row.SentDate);
    }
}
