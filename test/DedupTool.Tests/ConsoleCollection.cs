using Xunit;

namespace DedupTool.Tests;

/// <summary>
/// Console redirection is process-global (<c>Console.SetOut</c>/<c>SetError</c>), so
/// tests that capture stdout/stderr must not run concurrently with anything else.
/// xUnit parallelizes by collection, and <c>DisableParallelization</c> also keeps this
/// collection from overlapping other collections — without it, a CLI test that
/// captures stderr can silently swallow another class's warnings (observed: two
/// <c>CliTests</c> failures while <c>GoldenE2ETests</c> held the console).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
