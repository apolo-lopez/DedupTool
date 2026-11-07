using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Group 6 — load-file writer. Starts with RFC 4180 field escaping (task 6.1,
/// design D6): quoting rules only; applying them to all eight columns, the exact
/// header and LF endings are task 6.2, row ordering is task 6.3.
/// </summary>
public class LoadFileWriterTests
{
    [Theory]
    [InlineData("plain.pdf", "plain.pdf")]                       // ordinary field: verbatim
    [InlineData("", "")]                                        // empty stays empty
    [InlineData("a,b.txt", "\"a,b.txt\"")]                      // comma forces quoting
    [InlineData("say \"hi\".txt", "\"say \"\"hi\"\".txt\"")]     // quotes are doubled
    [InlineData("line1\nline2.txt", "\"line1\nline2.txt\"")]     // LF forces quoting
    [InlineData("line1\r\nline2.txt", "\"line1\r\nline2.txt\"")] // CR (and CRLF) forces quoting
    public void EscapeField_FollowsRfc4180(string value, string expected)
    {
        Assert.Equal(expected, LoadFileWriter.EscapeField(value));
    }

    [Fact]
    public void EscapeField_QuotesAndDoublesTogetherWhenBothNeeded()
    {
        // A value with a comma AND a quote must be quoted once and double every quote.
        Assert.Equal("\"He said \"\"a, b\"\"\"", LoadFileWriter.EscapeField("He said \"a, b\""));
    }

    [Fact]
    public void EscapeField_DoesNotAlterOrdinaryFieldBytes()
    {
        // Guard against an unconditional quote-everything implementation.
        const string value = "vendor-report.pdf";
        var escaped = LoadFileWriter.EscapeField(value);
        Assert.Equal(value, escaped);
        Assert.DoesNotContain('"', escaped);
    }

    // --- Task 6.2: escaping applied to all eight fields, header byte-exact, LF endings ---

    private static LoadFileRow Row(
        string docId = "DOC-000001",
        string familyId = "DOC-000001",
        string parentId = "",
        string custodian = "jsmith",
        string docType = "email",
        string fileName = "mail.eml",
        string sentDate = "2024-03-05T19:22:00Z",
        string hash = "abc123") =>
        new()
        {
            DocId = docId,
            FamilyId = familyId,
            ParentId = parentId,
            Custodian = custodian,
            DocType = docType,
            FileName = fileName,
            SentDate = sentDate,
            Hash = hash,
        };

    [Fact]
    public void Header_IsEmittedByteExactAndUnescaped()
    {
        var csv = LoadFileWriter.ToCsv(Array.Empty<LoadFileRow>());

        Assert.Equal("DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash\n", csv);
        Assert.StartsWith(LoadFileWriter.Header + "\n", csv);
    }

    [Fact]
    public void CommaInFileName_IsQuotedOnItsRow()
    {
        var csv = LoadFileWriter.ToCsv(new[] { Row(fileName: "report, final.pdf") });
        var line = csv.Split('\n')[1];

        Assert.Equal(
            "DOC-000001,DOC-000001,,jsmith,email,\"report, final.pdf\",2024-03-05T19:22:00Z,abc123",
            line);
    }

    [Fact]
    public void EveryFieldIsEscaped_NotJustFileName()
    {
        // Commas in all eight columns: each value must be quoted individually, so the
        // line is the eight quoted values joined by the seven separator commas.
        var csv = LoadFileWriter.ToCsv(new[]
        {
            Row(docId: "D,1", familyId: "F,1", parentId: "P,1", custodian: "c,1",
                docType: "e,1", fileName: "f,1", sentDate: "2024,1", hash: "h,1"),
        });
        var line = csv.Split('\n')[1];

        Assert.Equal(
            "\"D,1\",\"F,1\",\"P,1\",\"c,1\",\"e,1\",\"f,1\",\"2024,1\",\"h,1\"",
            line);
    }

    [Fact]
    public void LineEndings_AreLfOnly()
    {
        var csv = LoadFileWriter.ToCsv(new[] { Row(), Row(docId: "DOC-000002", fileName: "second.eml") });

        Assert.DoesNotContain('\r', csv);
        Assert.EndsWith("\n", csv);
        // header + 2 rows + the empty tail after the final LF
        Assert.Equal(4, csv.Split('\n').Length);
    }

    // --- Task 6.3: rows sorted by DocID (ordinal) before writing ---

    private static string[] DataRowDocIds(string csv) =>
        csv.Split('\n')
            .Skip(1)
            .Where(line => line.Length > 0)
            .Select(line => line.Split(',')[0])
            .ToArray();

    [Fact]
    public void ReversedRowList_ProducesByteIdenticalOutput()
    {
        var rows = new[]
        {
            Row(docId: "DOC-000001"),
            Row(docId: "DOC-000002", fileName: "second.eml"),
            Row(docId: "DOC-000010", fileName: "tenth.eml"),
        };

        var ascending = LoadFileWriter.ToCsv(rows);
        var descending = LoadFileWriter.ToCsv(rows.Reverse());

        Assert.Equal(ascending, descending);
        Assert.Equal(new[] { "DOC-000001", "DOC-000002", "DOC-000010" }, DataRowDocIds(ascending));
    }

    [Fact]
    public void Sort_IsOrdinal_SoDoc10PrecedesDoc9()
    {
        // Numeric/culture sorting would put DOC-9 first; ordinal puts "DOC-10" first.
        var csv = LoadFileWriter.ToCsv(new[] { Row(docId: "DOC-9"), Row(docId: "DOC-10") });

        Assert.Equal(new[] { "DOC-10", "DOC-9" }, DataRowDocIds(csv));
    }

    [Fact]
    public void EveryPermutationOfTheRowList_ProducesTheSameBytes()
    {
        var rows = new[]
        {
            Row(docId: "DOC-000003", fileName: "c.eml"),
            Row(docId: "DOC-000001", fileName: "a.eml"),
            Row(docId: "DOC-000012", fileName: "l.eml"),
            Row(docId: "DOC-000002", fileName: "b.eml"),
            Row(docId: "DOC-000010", fileName: "j.eml"),
        };

        var baseline = LoadFileWriter.ToCsv(rows);

        foreach (var seed in new[] { 5, 50, 500 })
        {
            var shuffled = TestShuffle.DeterministicShuffle(rows, seed);
            Assert.NotEqual(rows.Select(r => r.DocId), shuffled.Select(r => r.DocId)); // premise
            Assert.Equal(baseline, LoadFileWriter.ToCsv(shuffled));
        }
    }
}
