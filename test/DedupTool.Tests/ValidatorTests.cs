using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>Task 2.2: validation pass — one warning per invalid doc naming docId + reason; valid batch → zero warnings.</summary>
public class ValidatorTests
{
    // --- Valid input ---

    [Fact]
    public void ValidBatch_ProducesZeroWarnings()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var attachment = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var loose = TestDocuments.Loose("L", "c1", "h3");

        var warnings = Validator.Validate(new[] { email, attachment, loose });

        Assert.Empty(warnings);
    }

    [Fact]
    public void DeduperRun_ValidBatch_HasNoWarnings()
    {
        var batch = new Batch
        {
            Documents = new() { TestDocuments.Email("E", "c1", "h1"), TestDocuments.Attachment("A", "c1", "h2", "E") },
        };

        var result = Deduper.Run(batch);

        Assert.Empty(result.Warnings);
    }

    // --- One warning per invalid doc, naming docId + reason ---

    [Fact]
    public void UnknownParent_ProducesOneWarningNamingDocAndReason()
    {
        var orphan = TestDocuments.Attachment("A", "c1", "h1", parentId: "MISSING");

        var warnings = Validator.Validate(new[] { orphan });

        var w = Assert.Single(warnings);
        Assert.Equal("A", w.DocId);
        Assert.Equal("unknown parentId \"MISSING\"", w.Reason);
    }

    [Fact]
    public void EmptySha256_ProducesOneWarning()
    {
        var doc = TestDocuments.Loose("L", "c1", sha256: "");

        var w = Assert.Single(Validator.Validate(new[] { doc }));

        Assert.Equal("L", w.DocId);
        Assert.Equal("empty sha256", w.Reason);
    }

    [Fact]
    public void EmptyDocId_ProducesOneWarning()
    {
        var doc = TestDocuments.Loose(docId: "", custodian: "c1", sha256: "h1");

        var w = Assert.Single(Validator.Validate(new[] { doc }));

        Assert.Equal("", w.DocId);
        Assert.Equal("empty docId", w.Reason);
    }

    [Fact]
    public void DuplicateDocId_SecondOccurrenceWarnsFirstDoesNot()
    {
        var first = TestDocuments.Loose("DUP", "c1", "h1");
        var second = TestDocuments.Loose("DUP", "c2", "h2");

        var warnings = Validator.Validate(new[] { first, second });

        var w = Assert.Single(warnings);
        Assert.Equal("DUP", w.DocId);
        Assert.Equal("duplicate docId", w.Reason);
    }

    // --- sentDate (emails only) ---

    [Fact]
    public void UnparseableSentDate_ProducesOneWarning()
    {
        var email = TestDocuments.Email("E", "c1", "h1", sentDate: "last Tuesday");

        var w = Assert.Single(Validator.Validate(new[] { email }));

        Assert.Equal("E", w.DocId);
        Assert.Equal("sentDate not ISO 8601 with offset: \"last Tuesday\"", w.Reason);
    }

    [Fact]
    public void OffsetLessSentDate_ProducesOneWarning()
    {
        var email = TestDocuments.Email("E", "c1", "h1", sentDate: "2024-03-05T19:22:00");

        var w = Assert.Single(Validator.Validate(new[] { email }));

        Assert.Equal("E", w.DocId);
        Assert.Equal("sentDate not ISO 8601 with offset: \"2024-03-05T19:22:00\"", w.Reason);
    }

    [Fact]
    public void MissingSentDate_OnEmail_ProducesOneWarning()
    {
        var email = TestDocuments.Email("E", "c1", "h1", sentDate: null);

        var w = Assert.Single(Validator.Validate(new[] { email }));

        Assert.Equal("E", w.DocId);
        Assert.Equal("missing sentDate", w.Reason);
    }

    [Theory]
    [InlineData("2024-03-05T14:22:00-05:00")]
    [InlineData("2024-03-05T19:22:00Z")]
    [InlineData("2024-03-05T19:22:00.123Z")]
    [InlineData("2024-03-05T19:22:00+05:30")]
    public void ValidSentDate_ProducesNoWarning(string sentDate)
    {
        var email = TestDocuments.Email("E", "c1", "h1", sentDate: sentDate);

        Assert.Empty(Validator.Validate(new[] { email }));
    }

    [Fact]
    public void NonEmailDocuments_SentDateNotValidated()
    {
        // sentDate checks are scoped to emails (spec input-handling); a stray
        // value on a non-email doc must not warn.
        var loose = TestDocuments.Loose("L", "c1", "h1");
        loose.SentDate = "not a date";
        var attachment = TestDocuments.Attachment("A", "c1", "h2", parentId: "L");
        attachment.SentDate = "not a date";

        Assert.Empty(Validator.Validate(new[] { loose, attachment }));
    }

    // --- Cycles ---

    [Fact]
    public void ParentCycle_BothMembersWarned()
    {
        var x = TestDocuments.Attachment("X", "c1", "h1", parentId: "Y");
        var y = TestDocuments.Attachment("Y", "c1", "h2", parentId: "X");

        var warnings = Validator.Validate(new[] { x, y });

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal("parentId cycle", w.Reason));
        Assert.Equal(new[] { "X", "Y" }, warnings.Select(w => w.DocId));
    }

    [Fact]
    public void SelfParent_IsACycle()
    {
        var s = TestDocuments.Attachment("S", "c1", "h1", parentId: "S");

        var w = Assert.Single(Validator.Validate(new[] { s }));

        Assert.Equal("S", w.DocId);
        Assert.Equal("parentId cycle", w.Reason);
    }

    [Fact]
    public void DescendantOfCycle_ItsChainContainsTheCycle()
    {
        var x = TestDocuments.Attachment("X", "c1", "h1", parentId: "Y");
        var y = TestDocuments.Attachment("Y", "c1", "h2", parentId: "X");
        var z = TestDocuments.Attachment("Z", "c1", "h3", parentId: "X");

        var warnings = Validator.Validate(new[] { x, y, z });

        Assert.Equal(3, warnings.Count);
        Assert.Equal("parentId cycle", warnings[2].Reason);
    }

    // --- One warning per doc; input-order output ---

    [Fact]
    public void MultipleProblems_ProduceExactlyOneWarning_FirstCheckWins()
    {
        // Empty docId, unknown parent AND empty sha256 — still one warning.
        var doc = TestDocuments.Attachment(docId: "", custodian: "c1", sha256: "", parentId: "MISSING");

        var w = Assert.Single(Validator.Validate(new[] { doc }));

        Assert.Equal("empty docId", w.Reason);
    }

    [Fact]
    public void WarningsFollowInputOrder()
    {
        var first = TestDocuments.Loose("D1", "c1", sha256: "");
        var second = TestDocuments.Loose("D2", "c2", sha256: "");
        var valid = TestDocuments.Loose("D3", "c3", "h3");
        var third = TestDocuments.Loose("D4", "c4", sha256: "");

        var warnings = Validator.Validate(new[] { first, second, valid, third });

        Assert.Equal(new[] { "D1", "D2", "D4" }, warnings.Select(w => w.DocId));
    }

    // --- Deduper wiring (design D5) ---

    [Fact]
    public void DeduperRun_SurfacesWarningsInResult()
    {
        var batch = new Batch
        {
            Documents = new() { TestDocuments.Loose("L1", "c1", sha256: "") },
        };

        var result = Deduper.Run(batch);

        var w = Assert.Single(result.Warnings);
        Assert.Equal("L1", w.DocId);
        Assert.Equal("empty sha256", w.Reason);
    }
}
