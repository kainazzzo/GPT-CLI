using System.Text;
using GPT.CLI.Embeddings;
using Xunit;

namespace GptCli.Tests.Embeddings;

public sealed class DocumentTests
{
    [Fact]
    public void LoadEmbeddings_parses_valid_json()
    {
        var json = """
            [
              {"text":"alpha","description":"a","embed":[1.0,0.0]},
              {"text":"beta","description":"b","embed":[0.0,1.0]}
            ]
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var docs = Document.LoadEmbeddings(stream);
        Assert.Equal(2, docs.Count);
        Assert.Equal("alpha", docs[0].Text);
        Assert.Equal(new[] { 1.0, 0.0 }, docs[0].Embedding);
    }

    [Fact]
    public void LoadEmbeddings_invalid_or_empty_json_returns_empty_list()
    {
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("{not-json"));
        Assert.Empty(Document.LoadEmbeddings(invalid));

        using var empty = new MemoryStream(Encoding.UTF8.GetBytes(""));
        Assert.Empty(Document.LoadEmbeddings(empty));
    }

    [Fact]
    public void FindMostSimilarDocuments_ranks_and_respects_limit()
    {
        var docs = new List<Document>
        {
            new() { Text = "far", Embedding = new List<double> { 0, 1 } },
            new() { Text = "near", Embedding = new List<double> { 1, 0.1 } },
            new() { Text = "exact", Embedding = new List<double> { 1, 0 } }
        };

        var ranked = Document.FindMostSimilarDocuments(docs, new List<double> { 1, 0 }, numResults: 2).ToList();
        Assert.Equal(2, ranked.Count);
        Assert.Equal("exact", ranked[0].Document.Text);
        Assert.Equal("near", ranked[1].Document.Text);
        Assert.True(ranked[0].Similarity > ranked[1].Similarity);
    }

    [Fact]
    public async Task ChunkToDocumentsAsync_splits_by_chunk_size_including_remainder()
    {
        var docs = await Document.ChunkToDocumentsAsync("abcdefghij", chunkSize: 4);
        Assert.Equal(3, docs.Count);
        Assert.Equal("abcd", docs[0].Text);
        Assert.Equal("efgh", docs[1].Text);
        Assert.Equal("ij", docs[2].Text);
    }
}
