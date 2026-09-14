using GPT.CLI.Embeddings;
using Xunit;

namespace GptCli.Tests.Embeddings;

public sealed class CosineSimilarityTests
{
    [Fact]
    public void Identical_vectors_are_one()
    {
        var v = new List<double> { 1, 2, 3 };
        Assert.Equal(1.0, CosineSimilarity.Calculate(v, new List<double>(v)), 10);
    }

    [Fact]
    public void Orthogonal_vectors_are_zero()
    {
        var a = new List<double> { 1, 0 };
        var b = new List<double> { 0, 1 };
        Assert.Equal(0.0, CosineSimilarity.Calculate(a, b), 10);
    }

    [Fact]
    public void Opposite_vectors_are_negative_one()
    {
        var a = new List<double> { 1, 0 };
        var b = new List<double> { -1, 0 };
        Assert.Equal(-1.0, CosineSimilarity.Calculate(a, b), 10);
    }

    [Fact]
    public void Mismatched_lengths_throw()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CosineSimilarity.Calculate(new List<double> { 1, 2 }, new List<double> { 1 }));
        Assert.Contains("same length", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
