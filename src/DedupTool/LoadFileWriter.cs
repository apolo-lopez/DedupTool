using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    /// <summary>Characters that force a field to be quoted (RFC 4180 §2.6/§2.7).</summary>
    private static readonly char[] FieldSpecialChars = { '"', ',', '\r', '\n' };

    /// <summary>
    /// RFC 4180 field escaping (task 6.1, design D6, ai/QUESTIONS.md Q5): a field
    /// containing a double quote, comma, CR or LF is wrapped in double quotes, and
    /// every embedded double quote is doubled. Every other field is returned
    /// verbatim, so ordinary values keep their exact bytes.
    /// </summary>
    public static string EscapeField(string value)
    {
        if (value.AsSpan().IndexOfAny(FieldSpecialChars) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        // Ingest job expects LF line endings regardless of platform.
        var sb = new StringBuilder();

        // The header is the fixed ingest contract: emitted verbatim (its commas are
        // separators, so escaping it would corrupt it) with a single trailing LF.
        sb.Append(Header).Append('\n');

        // Rows are sorted by DocID with ORDINAL comparison (task 6.3, §6.1): the
        // load-file bytes must not depend on input or pipeline order, so "DOC-10"
        // precedes "DOC-9" (ordinal, not numeric) and a shuffled batch produces
        // the same file as a sorted one. OrderBy is a stable sort, so equal docIds
        // (invalid input, already warned about) keep their relative order.
        //
        // Every field is escaped (task 6.2): a value containing a quote, comma, CR
        // or LF is quoted per RFC 4180, so embedded punctuation cannot break the
        // column contract. Values without special characters pass through unchanged.
        foreach (var r in rows.OrderBy(row => row.DocId, StringComparer.Ordinal))
        {
            sb.Append(string.Join(",",
                EscapeField(r.DocId), EscapeField(r.FamilyId), EscapeField(r.ParentId),
                EscapeField(r.Custodian), EscapeField(r.DocType), EscapeField(r.FileName),
                EscapeField(r.SentDate), EscapeField(r.Hash))).Append('\n');
        }
        return sb.ToString();
    }

    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        File.WriteAllText(path, ToCsv(rows));
    }
}
