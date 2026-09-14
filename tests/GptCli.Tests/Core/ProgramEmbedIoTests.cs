using System.Text;
using GPT.CLI;
using Xunit;

namespace GptCli.Tests.Core;

public sealed class ProgramEmbedIoTests
{
    [Fact]
    public async Task ReadEmbedFilesAsync_empty_options_returns_empty()
    {
        var docs = await Program.ReadEmbedFilesAsync(new GptOptions());
        Assert.Empty(docs);

        docs = await Program.ReadEmbedFilesAsync(new GptOptions { EmbedFilenames = Array.Empty<string>() });
        Assert.Empty(docs);
    }

    [Fact]
    public async Task ReadEmbedFilesAsync_loads_json_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gptcli-embed-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """[{"text":"from-file","embed":[0.1,0.2]}]""");
            var docs = await Program.ReadEmbedFilesAsync(new GptOptions { EmbedFilenames = new[] { path } });
            Assert.Single(docs);
            Assert.Equal("from-file", docs[0].Text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ReadEmbedDirectoriesAsync_skips_missing_and_loads_nested_json()
    {
        var missing = await Program.ReadEmbedDirectoriesAsync(new GptOptions
        {
            EmbedDirectoryNames = new[] { Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}") }
        });
        Assert.Empty(missing);

        var root = Path.Combine(Path.GetTempPath(), $"gptcli-embed-dir-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(nested, "docs.json"),
                """[{"text":"from-dir","embed":[1.0]}]""",
                Encoding.UTF8);

            var docs = await Program.ReadEmbedDirectoriesAsync(new GptOptions
            {
                EmbedDirectoryNames = new[] { root }
            });
            Assert.Single(docs);
            Assert.Equal("from-dir", docs[0].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
