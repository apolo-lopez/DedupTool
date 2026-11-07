using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// The eight ai/QUESTIONS.md Q4 normalization fixtures (user decision:
/// full formatting-only normalization, ordinal comparisons everywhere) as a
/// table-driven theory. Each row is a PAIR of standalone email documents that
/// differ by exactly the named variance class, plus the "noise" a cross-mailbox
/// duplicate group always carries — different custodian, docId, sha256, size
/// and fileName — pinning those fields as fingerprint-irrelevant (design D4).
///
/// expectedDup=true ⇒ the pair's fingerprints are equal (one survives);
/// expectedDup=false ⇒ the fingerprints differ (both stay — the case-only
/// rows are the false-positive guard for §3.3).
/// </summary>
public class EmailFingerprintTests
{
    // Row layout: id | email A "from|to1,to2|subject|body|sentDate" |
    //             email B (same shape) | expected dup?
    // The 'to' segment is comma-separated and may be reordered between copies.
    [Theory]
    [InlineData("1-sample-pair-address-case-lfcrlf-tzoffset",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "alice.manager@corp-example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\r\nline2|2024-03-04T08:30:00Z",
        true)]
    [InlineData("2-subject-header-folding-double-space",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1  Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        true)]
    [InlineData("3-recipients-listed-in-different-order",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Carol@Corp-Example.com,Bob@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        true)]
    [InlineData("4-body-differs-only-by-letter-case",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|Line1\nLine2|2024-03-04T10:30:00+02:00",
        false)]
    [InlineData("5-subject-differs-only-by-letter-case",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|q1 status update|line1\nline2|2024-03-04T10:30:00+02:00",
        false)]
    [InlineData("6-body-differs-only-by-trailing-newline",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2\n|2024-03-04T10:30:00+02:00",
        true)]
    [InlineData("7-same-instant-different-offset-representation",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T09:30:00+01:00",
        true)]
    [InlineData("8-genuinely-different-recipients",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Carol@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        "Alice.Manager@Corp-Example.com|Bob@Corp-Example.com,Dora@Corp-Example.com|Q1 Status Update|line1\nline2|2024-03-04T10:30:00+02:00",
        false)]
    public void Q4FixturePair_ClassifiesAsDecided(string id, string emailA, string emailB, bool expectedDup)
    {
        // Different custodians/docIds/sha256/sizes/fileNames on every pair —
        // cross-mailbox noise that in no way affects the fingerprint.
        var a = EmailFingerprint.Compute(ParseEmail("DOC-A", "mailbox_one", emailA));
        var b = EmailFingerprint.Compute(ParseEmail("DOC-B", "mailbox_two", emailB));

        Assert.NotNull(a);
        Assert.NotNull(b);

        if (expectedDup)
        {
            Assert.Equal(a, b);
            // Hash-level agreement too — 4.2 will group by Dictionary key.
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }
        else
        {
            Assert.NotEqual(a, b);
        }
    }

    /// <summary>Documents without a valid instant are not fingerprintable — Validator excludes them upstream; Compute responds null defensively.</summary>
    [Fact]
    public void Compute_MissingOrInvalidSentDate_ReturnsNull()
    {
        var missing = ParseEmail("DOC-X", "c1",
            "Alice@corp.example.com|Bob@corp.example.com|Hi|body|"); // empty sentDate
        var invalid = ParseEmail("DOC-Y", "c1",
            "Alice@corp.example.com|Bob@corp.example.com|Hi|body|2024-03-04T10:30:00"); // offset-less

        Assert.Null(EmailFingerprint.Compute(missing));
        Assert.Null(EmailFingerprint.Compute(invalid));
    }

    private static Document ParseEmail(string docId, string custodian, string spec)
    {
        var parts = spec.Split('|');
        Assert.Equal(5, parts.Length);
        var doc = new Document
        {
            DocId = docId,
            Custodian = custodian,
            DocType = "email",
            ParentId = null,
            FileName = docId + ".eml",
            Sha256 = docId + "-sha",
            SizeBytes = docId.Length,
            From = parts[0],
            Subject = parts[2],
            Body = parts[3],
            SentDate = parts[4],
            To = parts[1].Length == 0
                ? new List<string>()
                : parts[1].Split(',').ToList(),
        };
        return doc;
    }
}
