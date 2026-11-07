namespace DedupTool.Tests;

/// <summary>
/// Deterministic shuffle for order-independence tests (design D8, tasks 3.1/4.4/8.2).
/// Fisher–Yates with a fixed seed so repeated runs — and runs within one test run —
/// always produce the exact same permutation of the same input.
/// </summary>
internal static class TestShuffle
{
    public const int DefaultSeed = 12345;

    /// <summary>Returns a new list containing the same elements as <paramref name="source"/>,
    /// deterministically reordered for the given <paramref name="seed"/>.</summary>
    public static List<T> DeterministicShuffle<T>(IEnumerable<T> source, int seed = DefaultSeed)
    {
        var list = source.ToList();
        var random = new Random(seed);

        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        return list;
    }
}
