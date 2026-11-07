using System.Text.Json;
using System.Text.RegularExpressions;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Task 8.1 — golden end-to-end test over <c>data/batch-sample.json</c>. The complete
/// expected load file is inline: exact header, 12 data rows in DocID order, UTC
/// SentDate on every email/attachment row, empty SentDate on loose rows.
/// The CLI test runs the real <c>Program.Main</c> (via <c>InternalsVisibleTo</c>), so
/// this covers JSON parsing, validation, dedup, projection, ordering and escaping.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class GoldenE2ETests : IDisposable
{
    private readonly string _dir;

    public GoldenE2ETests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "deduptool-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a lingering handle must not fail the test run.
        }
    }

    /// <summary>
    /// The expected load file, verbatim. 16 documents in → DOC-000005 survives
    /// (INGEST-4471: same hash as DOC-000003 under a different parent), the DOC-000009
    /// fingerprint-duplicate family (009/010/011) is suppressed wholesale, and
    /// DOC-000013 is the loose duplicate of DOC-000008.
    /// </summary>
    private static string ExpectedCsv() =>
        string.Join('\n', new[]
        {
            "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash",
            "DOC-000001,DOC-000001,,jsmith,email,Q3 budget review.eml,2024-03-05T19:22:00Z,f01c985325582f4e8eaa526b7e31bbc433628dcfe58e7d67044709953c1ca527",
            "DOC-000002,DOC-000001,DOC-000001,jsmith,attachment,Q3-budget.xlsx,2024-03-05T19:22:00Z,d0687f0fe9d3b02f06db97d7dc8ae8a3994fc27b44fdfaf7bf5997214c444f93",
            "DOC-000003,DOC-000001,DOC-000001,jsmith,attachment,vendor-report.pdf,2024-03-05T19:22:00Z,812ffd6153e7cc868416c5311a64daad44ef784610adf830f27a080f89af757c",
            "DOC-000004,DOC-000004,,jsmith,email,FW vendor report.eml,2024-03-06T14:10:00Z,3874c04f52d2eeca77202fb0f71a2f66b91408f44ca2d530fcd4d02bd2030bde",
            "DOC-000005,DOC-000004,DOC-000004,jsmith,attachment,vendor-report.pdf,2024-03-06T14:10:00Z,812ffd6153e7cc868416c5311a64daad44ef784610adf830f27a080f89af757c",
            "DOC-000006,DOC-000006,,jsmith,email,Lunch.eml,2024-03-06T16:45:00Z,7a5c40b9ebe1494f39ef594540d293b80001796da90992837b222387fa958ad9",
            "DOC-000007,DOC-000007,,jsmith,loose,notes.txt,,6266648e5c67fdb71e687d3ba9b5a0b92804937ac2fae8bf8cc6cc430d5de8bc",
            "DOC-000008,DOC-000008,,jsmith,loose,policy.docx,,5395fc0e907dd29f22317a7ecde0e385021fc95902e28eb22149ce99f88b69ab",
            "DOC-000012,DOC-000012,,mlee,email,Re Q3 budget review.eml,2024-03-05T20:05:00Z,e6bcef092770c7ec269f6f0669fba80714b0989553057fe674982baf681cdf5b",
            "DOC-000014,DOC-000014,,mlee,loose,mlee-notes.txt,,cc26e098ae131c88d1559d82a2071e7b746b7411dd5c5c0b506e40ce532b43ad",
            "DOC-000015,DOC-000015,,mlee,email,Team offsite.eml,2024-03-07T16:00:00Z,8f21091f355773850f73331c36f8c2d8b9b3d366355b806b1329f6582f585400",
            "DOC-000016,DOC-000015,DOC-000015,mlee,attachment,agenda.docx,2024-03-07T16:00:00Z,f174b50144f6ca573f4f952b54b03ebc5eb56d3b05ad1c9b3f15d15015265042",
        }) + "\n";

    [Fact]
    public void SampleBatch_ThroughTheCli_ProducesTheGoldenLoadFile()
    {
        var outPath = Path.Combine(_dir, "golden.csv");

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            exitCode = global::Program.Main(new[] { TestPaths.SampleBatchPath, outPath });
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr.ToString());
        Assert.Equal(
            $"batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates.{Environment.NewLine}",
            stdout.ToString());

        Assert.True(File.Exists(outPath));
        Assert.Equal(ExpectedCsv(), File.ReadAllText(outPath));
    }

    [Fact]
    public void GoldenRows_SatisfyTheSpecifiedExpectations()
    {
        var result = Deduper.Run(TestPaths.LoadSampleBatch());

        Assert.Equal(12, result.Rows.Count);
        Assert.Equal(4, result.Suppressed);
        Assert.Empty(result.Warnings);

        var byId = result.Rows.ToDictionary(r => r.DocId, StringComparer.Ordinal);

        // INGEST-4471: same sha256 as DOC-000003, different surviving parent → kept.
        Assert.True(byId.ContainsKey("DOC-000005"));
        Assert.Equal("DOC-000004", byId["DOC-000005"].ParentId);
        Assert.Equal("812ffd6153e7cc868416c5311a64daad44ef784610adf830f27a080f89af757c", byId["DOC-000005"].Hash);
        Assert.Equal(byId["DOC-000003"].Hash, byId["DOC-000005"].Hash);

        // §3.3: the losing cross-mailbox family and the loose duplicate are gone.
        foreach (var suppressedId in new[] { "DOC-000009", "DOC-000010", "DOC-000011", "DOC-000013" })
        {
            Assert.False(byId.ContainsKey(suppressedId), $"{suppressedId} must not be emitted");
        }

        // Exact UTC format on every email/attachment row, empty on loose rows.
        var utc = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        foreach (var row in result.Rows)
        {
            if (row.DocType == "loose")
            {
                Assert.Equal("", row.SentDate);
            }
            else
            {
                Assert.Matches(utc, row.SentDate);
            }
        }

        // Deterministic DocID (ordinal) order.
        Assert.Equal(
            result.Rows.Select(r => r.DocId).OrderBy(id => id, StringComparer.Ordinal),
            result.Rows.Select(r => r.DocId));
    }

    // --- Task 8.2: the load file does not depend on input order ---

    [Fact]
    public void ShuffledSample_ProducesTheGoldenCsv()
    {
        var batch = TestPaths.LoadSampleBatch();
        var docs = batch.Documents.ToList();

        foreach (var seed in new[] { 1, 2, 424242, 999983 })
        {
            var shuffled = TestShuffle.DeterministicShuffle(docs, seed);

            // Premise guard: a real permutation (same documents, different order).
            Assert.NotEqual(docs.Select(d => d.DocId), shuffled.Select(d => d.DocId));
            Assert.Equal(
                docs.Select(d => d.DocId).OrderBy(id => id, StringComparer.Ordinal),
                shuffled.Select(d => d.DocId).OrderBy(id => id, StringComparer.Ordinal));

            var result = Deduper.Run(new Batch { BatchId = batch.BatchId, Documents = shuffled });

            Assert.Equal(ExpectedCsv(), LoadFileWriter.ToCsv(result.Rows));
            Assert.Equal(12, result.Rows.Count);
            Assert.Equal(4, result.Suppressed);
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void ShuffledSampleThroughTheCli_WritesTheGoldenFile()
    {
        var batch = TestPaths.LoadSampleBatch();
        batch.Documents = TestShuffle.DeterministicShuffle(batch.Documents, seed: 777);

        var batchPath = Path.Combine(_dir, "shuffled-batch.json");
        File.WriteAllText(batchPath, JsonSerializer.Serialize(batch));
        var outPath = Path.Combine(_dir, "shuffled.csv");

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(originalError);
            exitCode = global::Program.Main(new[] { batchPath, outPath });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outPath));
        Assert.Equal(ExpectedCsv(), File.ReadAllText(outPath));
    }
}
