using System.Text.Json;
using DedupTool;

namespace DedupTool.Tests;

/// <summary>
/// Resolves repo fixture paths from the test binary location. The test host runs in
/// <c>test/DedupTool.Tests/bin/Debug/net8.0</c>, so the repo root is found by walking
/// up the directory tree until a directory containing <c>data/batch-sample.json</c>
/// is found (per design D8). The result is cached for the test run.
/// </summary>
internal static class TestPaths
{
    private static string? _repoRoot;

    public static string RepoRoot
    {
        get
        {
            if (_repoRoot is not null)
            {
                return _repoRoot;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "data", "batch-sample.json")))
                {
                    _repoRoot = dir.FullName;
                    return _repoRoot;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                $"Could not locate the repo root: no ancestor of {AppContext.BaseDirectory} contains data/batch-sample.json.");
        }
    }

    public static string SampleBatchPath => Path.Combine(RepoRoot, "data", "batch-sample.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Loads the sample batch the way the CLI does (case-insensitive JSON), so e2e
    /// tests exercise the same document graph as a real run. Shared by the
    /// determinism and golden tests (task 8.1) to avoid per-file copies.
    /// </summary>
    public static Batch LoadSampleBatch() =>
        JsonSerializer.Deserialize<Batch>(File.ReadAllText(SampleBatchPath), JsonOptions)
        ?? throw new InvalidOperationException("data/batch-sample.json deserialized to null.");
}
