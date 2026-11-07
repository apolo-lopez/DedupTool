using System.Text.RegularExpressions;

namespace DedupTool;

/// <summary>
/// §3.3 cross-mailbox email fingerprint (design D4): a formatting-only
/// normalized view of the five content fields that legitimately vary between
/// copies of the same email received by different mailboxes. Equal
/// fingerprints ⇒ "same email", decided with default record equality — plain
/// ordinal string comparison everywhere, no culture-sensitive casing (§6.1).
///
/// Normalization profile ("full", per ai/QUESTIONS.md Q4 — 8/8 fixtures):
/// addresses trimmed + case-folded, recipients sorted case-folded, subject
/// whitespace runs collapsed to one space with CASE PRESERVED, body line
/// endings folded to LF and outer whitespace trimmed with CASE PRESERVED,
/// sentDate reduced to its UTC instant. Content case is never compared
/// case-insensitively, so case-only variants do NOT match (false-positive
/// guard: irrecoverable content loss).
///
/// Excluded by design (raw bytes/derived names differ between copies):
/// sha256, sizeBytes, fileName, custodian, docId.
///
/// Task 4.1 provides the normalization only — wiring fingerprints into the
/// dedup decision (grouping + wholesale suppression) is task 4.2.
/// </summary>
public sealed partial record EmailFingerprint(
    string From,      // trim + case-fold
    string To,        // each recipient trim + case-fold, ordinal sort, "\n"-joined list
    string Subject,   // whitespace runs → one space, trimmed; case preserved
    string Body,      // CRLF/CR → LF, outer whitespace trimmed; case preserved
    string SentDate)  // UTC instant yyyy-MM-ddTHH:mm:ssZ (IsoDate.FormatUtc)
{
    /// <summary>
    /// Normalizes an email document into its fingerprint. Returns null when
    /// the sentDate is missing or unparseable — those documents are invalid
    /// and already excluded by the Validator upstream (design D4: offset-less
    /// timestamps never reach the dedup pipeline), so 4.2 treats a null
    /// fingerprint defensively rather than using it as a grouping key.
    /// </summary>
    public static EmailFingerprint? Compute(Document email)
    {
        if (email.SentDate is not { } sentDate)
        {
            return null;
        }

        var instant = IsoDate.FormatUtc(sentDate);
        if (instant is null)
        {
            return null;
        }

        return new EmailFingerprint(
            From: FoldAddress(email.From),
            To: FoldRecipients(email.To),
            Subject: CollapseWhitespace(email.Subject ?? ""),
            Body: NormalizeBody(email.Body ?? ""),
            SentDate: instant);
    }

    /// <summary>Copied recipients: trim + case-fold each, ordinal sort — set politics aside, D4 says "compare as list".</summary>
    private static string FoldRecipients(List<string>? recipients)
    {
        if (recipients is null)
        {
            return "";
        }

        var folded = recipients.Select(FoldAddress).ToArray();
        Array.Sort(folded, StringComparer.Ordinal);
        return string.Join("\n", folded);
    }

    private static string FoldAddress(string? address) => (address ?? "").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    /// <summary>
    /// Subject: internal whitespace runs (header folding artifacts, tabs)
    /// collapse to a single space; case is preserved.
    /// </summary>
    private static string CollapseWhitespace(string subject) =>
        WhitespaceRunRegex().Replace(subject, " ").Trim();

    /// <summary>
    /// Body: mailbox-dependent line endings fold to LF, then outer whitespace
    /// (e.g. trailing newlines) is trimmed; case is preserved.
    /// </summary>
    private static string NormalizeBody(string body) => body
        .Replace("\r\n", "\n")
        .Replace("\r", "\n")
        .Trim();
}
