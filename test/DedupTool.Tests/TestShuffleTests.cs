using Xunit;

namespace DedupTool.Tests;

public class TestShuffleTests
{
    private static readonly int[] Sample = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    /// <summary>Task 1.3 verify: the result is a true permutation — same elements, different order.</summary>
    [Fact]
    public void DeterministicShuffle_IsATruePermutation()
    {
        var shuffled = TestShuffle.DeterministicShuffle(Sample);

        // Same elements: same multiset (order-insensitive comparison).
        Assert.Equal(Sample.OrderBy(x => x), shuffled.OrderBy(x => x));

        // Different order: not the identity permutation.
        Assert.NotEqual(Sample, shuffled);
    }

    /// <summary>Deterministic: the same input + seed always yields the identical permutation.</summary>
    [Fact]
    public void DeterministicShuffle_SameSeed_SameResult()
    {
        var first = TestShuffle.DeterministicShuffle(Sample);
        var second = TestShuffle.DeterministicShuffle(Sample);

        Assert.Equal(first, second);
    }

    /// <summary>Empty and single-element inputs are handled without error.</summary>
    [Fact]
    public void DeterministicShuffle_SmallInputs_AreHandled()
    {
        Assert.Empty(TestShuffle.DeterministicShuffle(Array.Empty<int>()));
        Assert.Equal(new[] { 42 }, TestShuffle.DeterministicShuffle(new[] { 42 }));
    }
}
