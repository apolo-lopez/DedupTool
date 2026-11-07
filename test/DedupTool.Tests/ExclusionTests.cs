using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Task 2.3: invalid documents and their descendants are excluded from the
/// survivor set — no row, never counted as suppressed, one warning each
/// (spec input-handling: "Invalid documents do not fail the run").
/// </summary>
public class ExclusionTests
{
    [Fact]
    public void InvalidDocument_OmittedFromRows()
    {
        var valid = TestDocuments.Loose("L1", "c1", "h1");
        var invalid = TestDocuments.Loose("L2", "c1", sha256: "");

        var result = Deduper.Run(new Batch { Documents = new() { valid, invalid } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("L1", row.DocId);
        var w = Assert.Single(result.Warnings);
        Assert.Equal("L2", w.DocId);
        Assert.Equal("empty sha256", w.Reason);
    }

    [Fact]
    public void SkippedDocument_DescendantsAlsoSkipped()
    {
        // E is invalid; A (child) and B (grandchild) are valid on their own
        // but must go with it — no orphan rows, no dangling ParentID.
        var e = TestDocuments.Email("E", "c1", sha256: "");
        var a = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var b = TestDocuments.Attachment("B", "c1", "h3", parentId: "A");
        var survivor = TestDocuments.Loose("V", "c1", "h4");

        var result = Deduper.Run(new Batch { Documents = new() { e, a, b, survivor } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("V", row.DocId);

        Assert.Equal(3, result.Warnings.Count);
        Assert.Equal(new[] { "E", "A", "B" }, result.Warnings.Select(w => w.DocId));
        Assert.Equal("empty sha256", result.Warnings[0].Reason);
        Assert.Equal("parent \"E\" skipped", result.Warnings[1].Reason);
        Assert.Equal("parent \"A\" skipped", result.Warnings[2].Reason);
    }

    [Fact]
    public void NoEmittedRowReferencesSkippedParent()
    {
        // A's own parentId is unknown → A invalid; B is a valid-on-its-child of A
        // → descendant-skipped. Neither may appear, and no row may point at them.
        var survivor = TestDocuments.Loose("V", "c1", "h1");
        var a = TestDocuments.Attachment("A", "c1", "h2", parentId: "MISSING");
        var b = TestDocuments.Attachment("B", "c1", "h3", parentId: "A");

        var result = Deduper.Run(new Batch { Documents = new() { survivor, a, b } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("V", row.DocId);
        Assert.Equal("", row.ParentId);

        Assert.Equal(2, result.Warnings.Count);
        Assert.Equal("unknown parentId \"MISSING\"", result.Warnings[0].Reason);
        Assert.Equal("parent \"A\" skipped", result.Warnings[1].Reason);

        Assert.All(result.Rows, r =>
            Assert.DoesNotContain(r.ParentId, new[] { "A", "B", "MISSING" }));
    }

    [Fact]
    public void SuppressedCountExcludesInvalidDocuments()
    {
        // L2 shares L1's sha — under the old pass-through logic it would be
        // counted as suppressed. It is skipped instead, so it must not be.
        var l1 = TestDocuments.Loose("L1", "c1", "h1");
        var l2 = TestDocuments.Attachment("L2", "c1", "h1", parentId: "MISSING");
        var l3 = TestDocuments.Loose("L3", "c2", "h1"); // genuine duplicate of L1

        var result = Deduper.Run(new Batch { Documents = new() { l1, l2, l3 } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("L1", row.DocId);
        Assert.Equal(1, result.Suppressed); // L3 only — L2 is skipped, not suppressed
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void SkippedDocument_DoesNotOccupyShaSlot()
    {
        // The invalid email never enters the dedup accounting: the later valid
        // document with the same sha must survive, not be "suppressed" by it.
        var invalid = TestDocuments.Email("E", "c1", "h1", sentDate: "last Tuesday");
        var valid = TestDocuments.Loose("L", "c1", "h1");

        var result = Deduper.Run(new Batch { Documents = new() { invalid, valid } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("L", row.DocId);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void DuplicateDocId_ChildrenFollowFirstOccurrence()
    {
        // The second DUP is invalid (duplicate docId) but must NOT drag along
        // the first occurrence's child — children address the parent by docId,
        // which resolves first-wins.
        var first = TestDocuments.Loose("DUP", "c1", "h1");
        var second = TestDocuments.Loose("DUP", "c2", "h2");
        var child = TestDocuments.Attachment("C", "c1", "h3", parentId: "DUP");

        var result = Deduper.Run(new Batch { Documents = new() { first, second, child } });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new[] { "DUP", "C" }, result.Rows.Select(r => r.DocId));
        Assert.Equal(0, result.Suppressed);

        var w = Assert.Single(result.Warnings);
        Assert.Equal("DUP", w.DocId);
        Assert.Equal("duplicate docId", w.Reason);
    }

    [Fact]
    public void CycleMembersAndDescendantsSkipped()
    {
        // X ↔ Y cycle; Z descends from X (its chain contains the cycle, so
        // validation rejects it too). Only the clean loose doc survives.
        var x = TestDocuments.Attachment("X", "c1", "h1", parentId: "Y");
        var y = TestDocuments.Attachment("Y", "c1", "h2", parentId: "X");
        var z = TestDocuments.Attachment("Z", "c1", "h3", parentId: "X");
        var survivor = TestDocuments.Loose("V", "c1", "h4");

        var result = Deduper.Run(new Batch { Documents = new() { x, y, z, survivor } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("V", row.DocId);
        Assert.Equal(3, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal("parentId cycle", w.Reason));
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void ValidBatch_AllRowsKeptZeroWarnings()
    {
        var email = TestDocuments.Email("E", "c1", "h1");
        var attachment = TestDocuments.Attachment("A", "c1", "h2", parentId: "E");
        var loose = TestDocuments.Loose("L", "c1", "h3");

        var result = Deduper.Run(new Batch { Documents = new() { email, attachment, loose } });

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }
}
