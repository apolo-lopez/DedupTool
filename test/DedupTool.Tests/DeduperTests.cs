using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    [Fact]
    public void IdenticalLooseDocuments_SecondIsSuppressed()
    {
        var batch = new Batch
        {
            Documents = new() { TestDocuments.Loose("A", "c1", "h1"), TestDocuments.Loose("B", "c2", "h1") },
        };

        var result = Deduper.Run(batch);

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var batch = new Batch
        {
            Documents = new() { TestDocuments.Loose("A", "c1", "h1"), TestDocuments.Loose("B", "c1", "h2") },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void LoadFile_StartsWithFixedHeader()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.StartsWith("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash", csv);
    }

    // --- Task 3.1: loose dedup by sha256, batch-wide, lowest-docId survivor ---

    [Fact]
    public void SameHashLooseDocsAcrossCustodians_LowestDocIdSurvives()
    {
        // Higher docId listed FIRST: proves the survivor is chosen by docId,
        // not by input order (first-seen would keep DOC-B here).
        var high = TestDocuments.Loose("DOC-B", "jsmith", "h1");
        var low = TestDocuments.Loose("DOC-A", "mlee", "h1");

        var result = Deduper.Run(new Batch { Documents = new() { high, low } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("DOC-A", row.DocId);
        Assert.Equal("mlee", row.Custodian);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LooseGroups_OnlyDuplicateShaSuppressed()
    {
        var a = TestDocuments.Loose("DOC-A", "c1", "h1");
        var b = TestDocuments.Loose("DOC-B", "c2", "h1");
        var distinct = TestDocuments.Loose("DOC-C", "c3", "h2");

        var result = Deduper.Run(new Batch { Documents = new() { a, b, distinct } });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new[] { "DOC-A", "DOC-C" }, result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void ShuffledInput_SameLooseSurvivors()
    {
        var docs = new List<Document>
        {
            TestDocuments.Loose("DOC-3", "c1", "h1"),
            TestDocuments.Loose("DOC-1", "c2", "h1"),
            TestDocuments.Loose("DOC-2", "c3", "h2"),
            TestDocuments.Loose("DOC-4", "c4", "h1"),
        };

        var straight = Deduper.Run(new Batch { Documents = docs });
        var shuffled = Deduper.Run(new Batch { Documents = TestShuffle.DeterministicShuffle(docs) });

        // h1 group {DOC-1, DOC-3, DOC-4} → DOC-1 survives; DOC-2 (h2) survives.
        Assert.Equal(
            straight.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal),
            shuffled.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains(straight.Rows, r => r.DocId == "DOC-1");
        Assert.Contains(shuffled.Rows, r => r.DocId == "DOC-1");
        Assert.Equal(2, straight.Suppressed);
        Assert.Equal(straight.Suppressed, shuffled.Suppressed);
    }

    [Fact]
    public void LowestDocId_ComparedAsOrdinalNotNumeric()
    {
        // Ordinally "DOC-10" < "DOC-9" ('1' < '9') — pins design D3's
        // min(docId, StringComparer.Ordinal) against a numeric reading.
        var nine = TestDocuments.Loose("DOC-9", "c1", "h1");
        var ten = TestDocuments.Loose("DOC-10", "c2", "h1");

        var result = Deduper.Run(new Batch { Documents = new() { nine, ten } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("DOC-10", row.DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void SkippedLooseDoc_DoesNotJoinGroup()
    {
        // The duplicate-docId occurrence is skipped (task 2.3), so it must not
        // enter the sha group — the valid DOC-A and DOC-B compete normally.
        var first = TestDocuments.Loose("DOC-A", "c1", "h1");
        var duplicate = TestDocuments.Loose("DOC-A", "c2", "h1");
        var other = TestDocuments.Loose("DOC-B", "c3", "h1");

        var result = Deduper.Run(new Batch { Documents = new() { first, duplicate, other } });

        var row = Assert.Single(result.Rows);
        Assert.Equal("DOC-A", row.DocId);
        Assert.Equal(1, result.Suppressed); // DOC-B only — the duplicate is skipped, not suppressed
        var w = Assert.Single(result.Warnings);
        Assert.Equal("duplicate docId", w.Reason);
    }

    // --- Task 3.2: attachment dedup by sha256 within the same parent only ---

    [Fact]
    public void IdenticalAttachmentsSameParent_LowestDocIdSurvives()
    {
        // Higher docId listed FIRST: proves the survivor is chosen by docId,
        // not by input order. The parent email must exist or validation skip
        // both attachments (unknown parentId).
        var parent = TestDocuments.Email("DOC-N1", "c1", "emailHash");
        var high = TestDocuments.Attachment("DOC-N3", "jsmith", "h1", "DOC-N1");
        var low = TestDocuments.Attachment("DOC-N2", "mlee", "h1", "DOC-N1");

        var result = Deduper.Run(new Batch { Documents = new() { parent, high, low } });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("DOC-N1", Assert.Single(result.Rows, r => r.DocType == "email").DocId);
        var attachmentRow = Assert.Single(result.Rows, r => r.DocType == "attachment");
        Assert.Equal("DOC-N2", attachmentRow.DocId);
        Assert.Equal("mlee", attachmentRow.Custodian);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SameShaAttachmentsDifferentParents_BothKept_Ing4471()
    {
        // INGEST-4471 regression: the bug was DOC-000005 (attachment of the
        // surviving family PDF-000004) losing to DOC-000003 (same sha, other
        // family PDF-000001). Family integrity wins (Q1) — both are kept
        // because dedup never compares attachments across parents.
        var familyA = TestDocuments.Email("DOC-P1", "alice", "emailHashA");
        var attachmentA = TestDocuments.Attachment("DOC-A1", "alice", "shared", "DOC-P1");
        var familyB = TestDocuments.Email("DOC-P4", "bob", "emailHashB");
        var attachmentB = TestDocuments.Attachment("DOC-B1", "bob", "shared", "DOC-P4");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { familyA, attachmentA, familyB, attachmentB },
        });

        Assert.Equal(4, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.DocId == "DOC-A1");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-B1");
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AttachmentSuppression_IsolatedPerParent()
    {
        // Same sha twice under DOC-P1 (one suppressed) plus the same sha once
        // under DOC-P4 (kept) — suppression must not leak across parents.
        var parent1 = TestDocuments.Email("DOC-P1", "alice", "emailHash1");
        var parent4 = TestDocuments.Email("DOC-P4", "bob", "emailHash4");
        var low1 = TestDocuments.Attachment("DOC-2", "alice", "hX", "DOC-P1");
        var high1 = TestDocuments.Attachment("DOC-3", "alice", "hX", "DOC-P1");
        var only4 = TestDocuments.Attachment("DOC-5", "bob", "hX", "DOC-P4");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { parent1, parent4, low1, high1, only4 },
        });

        Assert.Equal(4, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.DocId == "DOC-P1");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-2");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-5");
        Assert.Equal(1, result.Suppressed); // DOC-3 only
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AttachmentDedup_IsOrderIndependent()
    {
        // docId DOC-N2 wins the (DOC-N1, h1) group regardless of input order;
        // DOC-N4 (h2) survives; DOC-N3 suppressed.
        var parent = TestDocuments.Email("DOC-N1", "c1", "emailHash");
        var docs = new List<Document>
        {
            TestDocuments.Attachment("DOC-N3", "c3", "h1", "DOC-N1"),
            TestDocuments.Attachment("DOC-N2", "c2", "h1", "DOC-N1"),
            TestDocuments.Attachment("DOC-N4", "c4", "h2", "DOC-N1"),
            parent,
        };

        var straight = Deduper.Run(new Batch { Documents = docs });
        var shuffled = Deduper.Run(new Batch
        {
            Documents = TestShuffle.DeterministicShuffle(docs),
        });

        var straightIds = straight.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal);
        var shuffledIds = shuffled.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal);
        Assert.Equal(straightIds, shuffledIds);
        Assert.Contains("DOC-N2", straightIds);
        Assert.Contains("DOC-N4", straightIds);
        Assert.Equal(1, straight.Suppressed);
        Assert.Equal(straight.Suppressed, shuffled.Suppressed);
    }

    // --- Task 3.3: dedup classes are disjoint (cross-class equal shas kept) ---

    [Fact]
    public void LooseSha_EqualsAttachmentSha_BothKept()
    {
        var parent = TestDocuments.Email("DOC-P1", "c1", "emailHash");
        var loose = TestDocuments.Loose("DOC-L1", "c2", "sharedHash");
        var attachment = TestDocuments.Attachment("DOC-A1", "c3", "sharedHash", "DOC-P1");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { parent, loose, attachment },
        });

        Assert.Equal(3, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.DocId == "DOC-L1");
        Assert.Contains(result.Rows, r => r.DocId == "DOC-A1");
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LooseSha_EqualsEmailSha_BothKept()
    {
        var loose = TestDocuments.Loose("DOC-L1", "c1", "sharedHash");
        var email = TestDocuments.Email("DOC-P1", "c2", "sharedHash");

        var result = Deduper.Run(new Batch { Documents = new() { email, loose } });

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.DocId == "DOC-L1");
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void AttachmentSha_EqualsEmailSha_BothKept()
    {
        // Even when the attachment hangs DIRECTLY on that same email: the
        // attachment branch never consults the email `seen` set.
        var email = TestDocuments.Email("DOC-P1", "c1", "sharedHash");
        var attachment = TestDocuments.Attachment("DOC-A1", "c2", "sharedHash", "DOC-P1");

        var result = Deduper.Run(new Batch { Documents = new() { email, attachment } });

        Assert.Equal(2, result.Rows.Count);
        Assert.Contains(result.Rows, r => r.DocId == "DOC-A1");
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AllThreeClasses_SameSha_AllKept()
    {
        // Loose, attachment and email with one identical sha256: each class
        // dedups only against itself, so the batch keeps every document and
        // a hash shared across classes suppresses nothing.
        var batch = new Batch
        {
            Documents = new()
            {
                TestDocuments.Email("DOC-P1", "c1", "sharedHash"),
                TestDocuments.Attachment("DOC-A1", "c2", "sharedHash", "DOC-P1"),
                TestDocuments.Loose("DOC-L1", "c3", "sharedHash"),
            },
        };

        var result = Deduper.Run(batch);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    // --- Task 3.4: suppression propagates down the children index ---

    [Fact]
    public void SuppressedMiddleOfChain_DragsChildrenAndGrandchildren()
    {
        // Spec deduplication "no orphan rows": in the chain
        // email → DOC-A(suppressed dup) → DOC-B → DOC-C, the whole subtree
        // below the duplicate disappears; survivors keep only the loose
        // attachment DOC-A's sibling. Dragged descendants count as suppressed.
        var email = TestDocuments.Email("DOC-P1", "c1", "emailHash");
        var survivor = TestDocuments.Attachment("DOC-A1", "c1", "h1", "DOC-P1");          // lowest docId
        var doomed = TestDocuments.Attachment("DOC-A2", "c1", "h1", "DOC-P1");            // same-parent dup
        var child = TestDocuments.Attachment("DOC-B1", "c1", "hB", "DOC-A2");
        var grandchild = TestDocuments.Attachment("DOC-C1", "c1", "hC", "DOC-B1");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email, survivor, doomed, child, grandchild },
        });

        var ids = result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOC-A1", "DOC-P1" }, ids);
        Assert.Equal(3, result.Suppressed); // DOC-A2 + DOC-B1 + DOC-C1
        Assert.Empty(result.Warnings);
        // Orphan invariant: every emitted ParentID is itself emitted (or "").
        Assert.All(result.Rows.Select(r => r.ParentId).Where(p => p.Length > 0),
            p => Assert.Contains(ids, id => id == p));
    }

    [Fact]
    public void SuppressionDoesNotLeakToSurvivingSiblingsChildren()
    {
        // The duplicate DOC-A2 is suppressed — and nothing else: DOC-A1
        // survives and DOC-B1 (child of the SURVIVING sibling) stays.
        var email = TestDocuments.Email("DOC-P1", "c1", "emailHash");
        var survivor = TestDocuments.Attachment("DOC-A1", "c1", "h1", "DOC-P1");
        var survivorChild = TestDocuments.Attachment("DOC-B1", "c2", "hB", "DOC-A1");
        var doomed = TestDocuments.Attachment("DOC-A3", "c3", "h1", "DOC-P1");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email, survivor, survivorChild, doomed },
        });

        var ids = result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOC-A1", "DOC-B1", "DOC-P1" }, ids);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void DraggedDocumentThatIsInvalid_StaysSkipped_NotDoubleCounted()
    {
        // A child of a suppressed duplicate that is itself invalid (empty
        // sha) belongs to the skipped pipeline, not the dup counter: it gets
        // its validation warning, and suppressed counts only DOC-A2.
        var email = TestDocuments.Email("DOC-P1", "c1", "emailHash");
        var survivor = TestDocuments.Attachment("DOC-A1", "c1", "h1", "DOC-P1");
        var doomed = TestDocuments.Attachment("DOC-A2", "c1", "h1", "DOC-P1");
        var invalidChild = TestDocuments.Attachment("DOC-B1", "c1", "", "DOC-A2");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email, survivor, doomed, invalidChild },
        });

        Assert.Equal(new[] { "DOC-A1", "DOC-P1" }, result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(1, result.Suppressed); // DOC-A2 only; DOC-B1 excluded as skipped
        var w = Assert.Single(result.Warnings);
        Assert.Equal("DOC-B1", w.DocId);
        Assert.Equal("empty sha256", w.Reason);
    }

    [Fact]
    public void MixedBatch_NoOrphanRows()
    {
        // Property over a mixed batch: duplicate families, dragged subtrees
        // and loose dups together — every emitted ParentID is emitted.
        var email1 = TestDocuments.Email("DOC-P1", "c1", "emailHash1");
        var att1 = TestDocuments.Attachment("DOC-A1", "c1", "h1", "DOC-P1");
        var att2 = TestDocuments.Attachment("DOC-A2", "c1", "h1", "DOC-P1"); // same-parent dup
        var dragged = TestDocuments.Attachment("DOC-B1", "c1", "hB", "DOC-A2");
        var email2 = TestDocuments.Email("DOC-P2", "c2", "emailHash2");
        var att3 = TestDocuments.Attachment("DOC-A3", "c2", "h2", "DOC-P2");
        var loose1 = TestDocuments.Loose("DOC-L1", "c3", "hL");
        var loose2 = TestDocuments.Loose("DOC-L2", "c4", "hL");              // loose dup

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email1, att1, att2, dragged, email2, att3, loose1, loose2 },
        });

        var emitted = result.Rows.Select(r => r.DocId).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "DOC-P1", "DOC-A1", "DOC-P2", "DOC-A3", "DOC-L1" },
            emitted);
        Assert.Equal(3, result.Suppressed); // DOC-A2, DOC-B1, DOC-L2
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "DOC-P1", "DOC-P2" },
            result.Rows.Select(r => r.ParentId).Where(p => p.Length > 0).ToHashSet());
    }

    // --- Task 4.2: cross-mailbox fingerprint grouping → whole losing family suppressed ---

    /// <summary>
    /// The sample pair §3.3: emails recognizable as one across mailboxes even
    /// when raw bytes differ (address case, recipient order, CRLF/LF, date
    /// offset) — the losing email's ENTIRE family goes with it.
    /// </summary>
    private static (Document fromMailboxOne, Document fromMailboxTwo) FingerprintDuplicatePair(
        string docIdOne, string docIdTwo, string bodyOne = "line1\nline2", string bodyTwo = "line1\r\nline2") =>
        (
            TestDocuments.Email(docIdOne, "alice", docIdOne + "-sha",
                sentDate: "2024-03-05T14:22:00-05:00",
                from: "Alice.Manager@Corp-Example.com",
                to: new[] { "Bob@Corp-Example.com", "Carol@Corp-Example.com" },
                subject: "Q1 Status Update",
                body: bodyOne),
            TestDocuments.Email(docIdTwo, "bob", docIdTwo + "-sha",
                sentDate: "2024-03-05T19:22:00Z",
                from: "alice.manager@corp-example.com",
                to: new[] { "Carol@Corp-Example.com", "Bob@Corp-Example.com" },
                subject: "Q1 Status Update",
                body: bodyTwo));

    [Fact]
    public void CrossMailboxDuplicate_LosingFamilySuppressedWholesale()
    {
        var (winner, loser) = FingerprintDuplicatePair("DOC-P1", "DOC-P4");
        var winnerAttachment = TestDocuments.Attachment("DOC-A1", "alice", "shaA1", "DOC-P1");
        var loserAttachment = TestDocuments.Attachment("DOC-A4", "bob", "shaA4", "DOC-P4");
        var loserGrandchild = TestDocuments.Attachment("DOC-N4", "bob", "shaN4", "DOC-A4");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { winner, winnerAttachment, loser, loserAttachment, loserGrandchild },
        });

        var ids = result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOC-A1", "DOC-P1" }, ids);
        Assert.Equal(3, result.Suppressed); // DOC-P4 + DOC-A4 + DOC-N4 — wholesale
        Assert.Empty(result.Warnings);
        Assert.All(result.Rows.Select(r => r.ParentId).Where(p => p.Length > 0),
            p => Assert.Contains(ids, id => id == p)); // orphan invariant intact
    }

    [Fact]
    public void NonFingerprintMatchingEmails_KeepTheirFamilies()
    {
        // Body differing only by letter case → different emails (Q4 fixture 4):
        // both families survive fully.
        var (email1, email2) = FingerprintDuplicatePair("DOC-P1", "DOC-P4", bodyOne: "line1\nline2", bodyTwo: "Line1\r\nLine2");
        var att1 = TestDocuments.Attachment("DOC-A1", "alice", "shaA1", "DOC-P1");
        var att4 = TestDocuments.Attachment("DOC-A4", "bob", "shaA4", "DOC-P4");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email1, att1, email2, att4 },
        });

        var ids = result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOC-A1", "DOC-A4", "DOC-P1", "DOC-P4" }, ids);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void FingerprintDuplicate_SurvivorIsLowestDocIdRegardlessOfInputOrder()
    {
        var (winner, loser) = FingerprintDuplicatePair("DOC-P1", "DOC-P4");

        // Loser listed FIRST — the survivor must still be the lowest docId.
        var result = Deduper.Run(new Batch
        {
            Documents = new() { loser, winner },
        });

        Assert.Single(result.Rows);
        Assert.Equal("DOC-P1", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AttachedEmailWithMatchingOwnParentFingerprint_IsFamilyMemberNotGrouped()
    {
        // An attached copy of its own root email (identical fingerprint) is a
        // family member, never a grouping candidate: it survives with the
        // family it belongs to (task 4.3 pins the cross-family variant).
        var email = TestDocuments.Email("DOC-P1", "alice", "shaA",
            from: "sender@example.com",
            to: new[] { "to@example.com" },
            subject: "Same",
            body: "Body");
        var attachedCopy = TestDocuments.Email("DOC-N1", "alice", "shaN", parentId: "DOC-P1",
            sentDate: "2024-03-05T19:22:00Z",
            from: "sender@example.com",
            to: new[] { "to@example.com" },
            subject: "Same",
            body: "Body");

        var result = Deduper.Run(new Batch
        {
            Documents = new() { email, attachedCopy },
        });

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }

    // --- Task 4.3: attached emails are family members, never fingerprint candidates ---

    [Fact]
    public void AttachedEmailMatchingAnotherFamilysStandalone_IsNotSuppressed()
    {
        // Family 1's standalone email and family 2's ATTACHED email are the same
        // message. Only top-level emails group, so the attached copy is family 2's
        // property: nothing is suppressed and both families stay intact.
        var standalone = TestDocuments.Email("DOC-P1", "alice", "shaP1",
            from: "Sender@Example.com",
            to: new[] { "Recipient@Example.com" },
            subject: "Quarterly numbers",
            body: "Numbers attached.");
        var otherRoot = TestDocuments.Email("DOC-P2", "bob", "shaP2",
            from: "other@example.com",
            to: new[] { "someone@example.com" },
            subject: "Unrelated",
            body: "Something else.");
        var attachedCopy = TestDocuments.Email("DOC-N2", "bob", "shaN2", parentId: "DOC-P2",
            from: "sender@example.com",
            to: new[] { "recipient@example.com" },
            subject: "Quarterly numbers",
            body: "Numbers attached.");

        // Guard the premise: the pair really does fingerprint-match.
        Assert.Equal(EmailFingerprint.Compute(standalone), EmailFingerprint.Compute(attachedCopy));

        var result = Deduper.Run(new Batch
        {
            Documents = new() { standalone, otherRoot, attachedCopy },
        });

        var ids = result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOC-N2", "DOC-P1", "DOC-P2" }, ids);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
        Assert.All(result.Rows.Select(r => r.ParentId).Where(p => p.Length > 0),
            p => Assert.Contains(ids, id => id == p));
    }

    [Fact]
    public void StandalonePairStillDedups_AttachedCopiesFollowTheirFamilies()
    {
        // Both families carry an attached email with the winning fingerprint. If
        // attached emails joined grouping they would be extra suppression
        // candidates; instead each simply travels with its own family.
        var (winner, loser) = FingerprintDuplicatePair("DOC-P1", "DOC-P4");
        var winnerAttachment = TestDocuments.Attachment("DOC-A1", "alice", "shaA1", "DOC-P1");
        var loserAttachment = TestDocuments.Attachment("DOC-A4", "bob", "shaA4", "DOC-P4");
        var winnerAttached = TestDocuments.Email("DOC-E1", "alice", "shaE1", parentId: "DOC-P1",
            from: "alice.manager@corp-example.com",
            to: new[] { "carol@corp-example.com", "bob@corp-example.com" },
            subject: "Q1 Status Update",
            body: "line1\nline2");
        var loserAttached = TestDocuments.Email("DOC-E4", "bob", "shaE4", parentId: "DOC-P4",
            from: "ALICE.MANAGER@CORP-EXAMPLE.COM",
            to: new[] { "Bob@Corp-Example.com", "Carol@Corp-Example.com" },
            subject: "Q1  Status Update",
            body: "line1\r\nline2");

        Assert.Equal(EmailFingerprint.Compute(winner), EmailFingerprint.Compute(loserAttached));

        var result = Deduper.Run(new Batch
        {
            Documents = new() { winner, winnerAttachment, winnerAttached, loser, loserAttachment, loserAttached },
        });

        var ids = result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "DOC-A1", "DOC-E1", "DOC-P1" }, ids);
        Assert.Equal(3, result.Suppressed); // DOC-P4 + DOC-A4 + DOC-E4 — losing family wholesale
        Assert.Empty(result.Warnings);
        Assert.All(result.Rows.Select(r => r.ParentId).Where(p => p.Length > 0),
            p => Assert.Contains(ids, id => id == p));
    }

    [Fact]
    public void AttachedEmailsWithEqualFingerprints_InDifferentFamilies_BothKept()
    {
        var rootOne = TestDocuments.Email("DOC-P1", "alice", "shaP1",
            subject: "Root one", body: "Root one body.");
        var rootTwo = TestDocuments.Email("DOC-P2", "bob", "shaP2",
            subject: "Root two", body: "Root two body.");
        var attachedOne = TestDocuments.Email("DOC-N1", "alice", "shaN1", parentId: "DOC-P1",
            from: "loop@example.com",
            to: new[] { "list@example.com" },
            subject: "Forwarded note",
            body: "Forwarded body.");
        var attachedTwo = TestDocuments.Email("DOC-N2", "bob", "shaN2", parentId: "DOC-P2",
            from: "Loop@Example.com",
            to: new[] { "List@Example.com" },
            subject: "Forwarded  note",
            body: "Forwarded body.\n");

        Assert.Equal(EmailFingerprint.Compute(attachedOne), EmailFingerprint.Compute(attachedTwo));

        var result = Deduper.Run(new Batch
        {
            Documents = new() { rootOne, attachedOne, rootTwo, attachedTwo },
        });

        Assert.Equal(4, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
        Assert.Empty(result.Warnings);
    }
}
