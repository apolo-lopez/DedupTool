using DedupTool;
using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Group 7 — CLI behaviour. This file covers task 7.1: ordered warnings on stderr,
/// exit code 0, load file still written for the valid remainder. Calls the real
/// <c>Program.Main</c> (exposed via <c>InternalsVisibleTo</c>) with temporary files.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CliTests : IDisposable
{
    private readonly string _dir;

    public CliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "deduptool-cli-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Runs the CLI with stdout/stderr captured, restoring the console afterwards.</summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = global::Program.Main(args);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private string WriteBatch(string batchId, string json)
    {
        var path = Path.Combine(_dir, batchId + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void UnknownParent_WarnsOnStderr_ExitsZero_AndWritesRemainder()
    {
        var batchPath = WriteBatch("invalid", """
        {
          "batchId": "invalid",
          "documents": [
            { "docId": "DOC-000001", "custodian": "c1", "docType": "loose", "fileName": "ok.txt", "sha256": "s1", "sizeBytes": 1 },
            { "docId": "DOC-000002", "custodian": "c1", "docType": "attachment", "parentId": "DOC-MISSING", "fileName": "orphan.pdf", "sha256": "s2", "sizeBytes": 1 },
            { "docId": "DOC-000003", "custodian": "c1", "docType": "attachment", "parentId": "DOC-000002", "fileName": "child.pdf", "sha256": "s3", "sizeBytes": 1 }
          ]
        }
        """);
        var outPath = Path.Combine(_dir, "out.csv");

        var (exitCode, stdout, stderr) = RunCli(batchPath, outPath);

        Assert.Equal(0, exitCode);

        // One warning line per skipped document: the invalid doc and its descendant.
        var lines = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("warning: DOC-000002: ", lines[0]);
        Assert.Contains("unknown parentId", lines[0]);
        Assert.StartsWith("warning: DOC-000003: ", lines[1]);
        Assert.Contains("skipped", lines[1]);

        // Summary counts exclude skipped docs, and none of them are "suppressed".
        Assert.Equal($"invalid: 3 documents in, 1 out, 0 suppressed as duplicates.{Environment.NewLine}", stdout);

        Assert.True(File.Exists(outPath));
        var csv = File.ReadAllText(outPath);
        Assert.Contains("DOC-000001", csv);
        Assert.DoesNotContain("DOC-000002", csv);
        Assert.DoesNotContain("DOC-000003", csv);
    }

    [Fact]
    public void ValidBatch_WritesNothingToStderr()
    {
        var outPath = Path.Combine(_dir, "sample.csv");

        var (exitCode, stdout, stderr) = RunCli(TestPaths.SampleBatchPath, outPath);

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr);
        Assert.Equal(
            $"batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates.{Environment.NewLine}",
            stdout);
        Assert.True(File.Exists(outPath));
    }

    [Fact]
    public void EmptyDocId_WarningNamesItRatherThanPrintingBlank()
    {
        var batchPath = WriteBatch("blank-docid", """
        {
          "batchId": "blank-docid",
          "documents": [
            { "docId": "", "custodian": "c1", "docType": "loose", "fileName": "x.txt", "sha256": "s1", "sizeBytes": 1 }
          ]
        }
        """);
        var outPath = Path.Combine(_dir, "blank.csv");

        var (exitCode, _, stderr) = RunCli(batchPath, outPath);

        Assert.Equal(0, exitCode);
        Assert.Contains("warning: (missing docId): empty docId", stderr);
    }

    // --- Task 7.2: unusable batch file → stderr error, non-zero exit, no load file ---

    private void AssertFatalInputError(int exitCode, string stderr, string outPath)
    {
        Assert.Equal(1, exitCode);

        // Exactly one line: a message, not an unhandled-exception stack trace.
        var lines = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.StartsWith("error: cannot read batch", line);

        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void MalformedJson_ReportsError_ExitsNonZero_NoLoadFile()
    {
        var batchPath = WriteBatch("malformed", "{ this is not json: [");
        var outPath = Path.Combine(_dir, "malformed-out.csv");

        var (exitCode, _, stderr) = RunCli(batchPath, outPath);

        AssertFatalInputError(exitCode, stderr, outPath);
        Assert.Contains(batchPath, stderr);
    }

    [Fact]
    public void MissingBatchFile_ReportsError_ExitsNonZero_NoLoadFile()
    {
        var missing = Path.Combine(_dir, "does-not-exist.json");
        var outPath = Path.Combine(_dir, "missing-out.csv");

        var (exitCode, _, stderr) = RunCli(missing, outPath);

        AssertFatalInputError(exitCode, stderr, outPath);
    }

    [Fact]
    public void EmptyBatchFile_ReportsError_ExitsNonZero_NoLoadFile()
    {
        var batchPath = WriteBatch("empty", "");
        var outPath = Path.Combine(_dir, "empty-out.csv");

        var (exitCode, _, stderr) = RunCli(batchPath, outPath);

        AssertFatalInputError(exitCode, stderr, outPath);
    }

    [Fact]
    public void FailedRun_LeavesAnExistingOutputFileUntouched()
    {
        // A failed run must not truncate a previous load file: "no load file is
        // created" means the writer is never reached.
        var batchPath = WriteBatch("malformed2", "not json");
        var outPath = Path.Combine(_dir, "existing.csv");
        File.WriteAllText(outPath, "previous contents\n");

        var (exitCode, _, stderr) = RunCli(batchPath, outPath);

        Assert.Equal(1, exitCode);
        Assert.Single(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("previous contents\n", File.ReadAllText(outPath));
    }

    // --- Task 7.3: the usage contract is preserved ---

    private const string UsageLine = "usage: DedupTool <batch.json> <loadfile.csv>";

    public static IEnumerable<object[]> TooFewArgumentCases()
    {
        yield return new object[] { Array.Empty<string>() };
        yield return new object[] { new[] { "only-one.json" } };
    }

    [Theory]
    [MemberData(nameof(TooFewArgumentCases))]
    public void TooFewArguments_PrintsUsageAndExitsTwo(string[] args)
    {
        var (exitCode, stdout, stderr) = RunCli(args);

        Assert.Equal(2, exitCode);
        Assert.Equal("", stdout);
        Assert.Equal(UsageLine + Environment.NewLine, stderr);
    }

    [Fact]
    public void TooManyArguments_PrintsUsage_ExitsTwo_AndCreatesNoLoadFile()
    {
        var outPath = Path.Combine(_dir, "never.csv");

        var (exitCode, stdout, stderr) = RunCli(TestPaths.SampleBatchPath, outPath, "extra-argument");

        Assert.Equal(2, exitCode);
        Assert.Equal("", stdout);
        Assert.Equal(UsageLine + Environment.NewLine, stderr);
        Assert.False(File.Exists(outPath));
    }

    // --- Task 7.4: the success summary is exactly one line with exact counts ---

    private const string SampleSummary =
        "batch-sample: 16 documents in, 12 out, 4 suppressed as duplicates.";

    [Fact]
    public void Summary_OnSample_IsExactlyOneLineWithTheSpecifiedCounts()
    {
        var (exitCode, stdout, _) = RunCli(TestPaths.SampleBatchPath, Path.Combine(_dir, "summary.csv"));

        Assert.Equal(0, exitCode);
        var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        Assert.Equal(SampleSummary, line);
        Assert.EndsWith(Environment.NewLine, stdout); // the line is newline-terminated, no extra output
    }

    [Fact]
    public void Summary_OnADifferentBatch_ReportsThatInputsCounts()
    {
        // 3 in, 2 out, 1 suppressed: two loose docs sharing a hash plus an email.
        var batchPath = WriteBatch("counts", """
        {
          "batchId": "counts",
          "documents": [
            { "docId": "DOC-1", "custodian": "c1", "docType": "loose", "fileName": "a.txt", "sha256": "same", "sizeBytes": 1 },
            { "docId": "DOC-2", "custodian": "c2", "docType": "loose", "fileName": "b.txt", "sha256": "same", "sizeBytes": 1 },
            { "docId": "DOC-3", "custodian": "c1", "docType": "email", "fileName": "m.eml", "sha256": "mail", "sizeBytes": 1, "sentDate": "2024-03-05T19:22:00Z", "from": "a@x.com", "to": ["b@x.com"], "subject": "S", "body": "B" }
          ]
        }
        """);

        var (exitCode, stdout, _) = RunCli(batchPath, Path.Combine(_dir, "counts.csv"));

        Assert.Equal(0, exitCode);
        Assert.Equal(
            "counts: 3 documents in, 2 out, 1 suppressed as duplicates." + Environment.NewLine,
            stdout);
    }

    [Fact]
    public void Summary_InputCountIncludesSkippedDocuments_ButSuppressedDoesNot()
    {
        var batchPath = WriteBatch("skips", """
        {
          "batchId": "skips",
          "documents": [
            { "docId": "DOC-1", "custodian": "c1", "docType": "loose", "fileName": "ok.txt", "sha256": "s1", "sizeBytes": 1 },
            { "docId": "DOC-2", "custodian": "c1", "docType": "attachment", "parentId": "DOC-MISSING", "fileName": "orphan.pdf", "sha256": "s2", "sizeBytes": 1 },
            { "docId": "DOC-3", "custodian": "c1", "docType": "loose", "fileName": "ok2.txt", "sha256": "s3", "sizeBytes": 1 }
          ]
        }
        """);

        var (exitCode, stdout, stderr) = RunCli(batchPath, Path.Combine(_dir, "skips.csv"));

        Assert.Equal(0, exitCode);
        // "in" is every document in the input file; the skipped one is neither out nor
        // suppressed (spec: skipped documents are not duplicates).
        Assert.Equal(
            "skips: 3 documents in, 2 out, 0 suppressed as duplicates." + Environment.NewLine,
            stdout);
        Assert.Contains("warning: DOC-2", stderr);
    }
}
