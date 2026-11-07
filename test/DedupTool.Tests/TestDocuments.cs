using DedupTool;

namespace DedupTool.Tests;

/// <summary>
/// Shared factories for batch documents. Every builder fills sensible defaults so a
/// test only states the fields it cares about; all values are overridable.
/// Defaults are chosen so unrelated tests never accidentally collide: each document
/// gets a docId-derived file name, subject and body (so two emails built from
/// defaults are distinct), and a fixed-size, fixed-date baseline.
/// </summary>
internal static class TestDocuments
{
    public static Document Loose(string docId, string custodian, string sha256, string? fileName = null) =>
        new()
        {
            DocId = docId,
            Custodian = custodian,
            DocType = "loose",
            ParentId = null,
            FileName = fileName ?? docId + ".txt",
            Sha256 = sha256,
            SizeBytes = 10,
        };

    public static Document Attachment(string docId, string custodian, string sha256, string parentId, string? fileName = null) =>
        new()
        {
            DocId = docId,
            Custodian = custodian,
            DocType = "attachment",
            ParentId = parentId,
            FileName = fileName ?? docId + ".bin",
            Sha256 = sha256,
            SizeBytes = 10,
        };

    public static Document Email(
        string docId,
        string custodian,
        string sha256,
        string? parentId = null,
        string? sentDate = "2024-03-05T19:22:00Z",
        string from = "sender@example.com",
        string[]? to = null,
        string? subject = null,
        string? body = null) =>
        new()
        {
            DocId = docId,
            Custodian = custodian,
            DocType = "email",
            ParentId = parentId,
            FileName = docId + ".eml",
            Sha256 = sha256,
            SizeBytes = 10,
            SentDate = sentDate,
            From = from,
            To = to?.ToList() ?? new List<string> { "recipient@example.com" },
            Subject = subject ?? "Subject " + docId,
            Body = body ?? "Body " + docId,
        };
}
