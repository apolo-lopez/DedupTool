using Xunit;

namespace DedupTool.Tests;

public class TestPathsTests
{
    /// <summary>Task 1.2 smoke test: the resolver finds the repo root and the sample batch opens.</summary>
    [Fact]
    public void SampleBatch_OpensFromTestBinaryLocation()
    {
        var path = TestPaths.SampleBatchPath;

        Assert.True(File.Exists(path), $"Expected sample batch at {path}");

        var content = File.ReadAllText(path);

        Assert.Contains("\"batchId\"", content);
        Assert.Contains("DOC-000001", content);
    }
}
