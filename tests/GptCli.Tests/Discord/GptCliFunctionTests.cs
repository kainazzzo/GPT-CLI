using GPT.CLI.Chat.Discord.Commands;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class GptCliFunctionTests
{
    [Fact]
    public void TryGetJsonProperty_reads_object_values()
    {
        Assert.True(GptCliFunction.TryGetJsonProperty("""{"term":"alpha","n":3}""", "term", out var term));
        Assert.Equal("alpha", term.GetString());
        Assert.True(GptCliFunction.TryGetJsonProperty("""{"term":"alpha","n":3}""", "n", out var n));
        Assert.Equal(3, n.GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"term":"alpha"}""")]
    public void TryGetJsonProperty_returns_false_for_invalid_or_missing(string json)
    {
        var name = json != null && json.Contains("term", StringComparison.Ordinal) ? "missing" : "term";
        Assert.False(GptCliFunction.TryGetJsonProperty(json, name, out _));
    }

    [Fact]
    public void ToToolDefinition_requires_tool_name_and_lists_required_params()
    {
        var missing = new GptCliFunction { Description = "x" };
        Assert.Throws<InvalidOperationException>(() => missing.ToToolDefinition());

        var fn = new GptCliFunction
        {
            ToolName = "infobot_set",
            Description = "Set a factoid",
            Parameters = new[]
            {
                new GptCliParamSpec("term", GptCliParamType.String, "The term", Required: true),
                new GptCliParamSpec("text", GptCliParamType.String, "The text", Required: false),
                new GptCliParamSpec(" ", GptCliParamType.String, "ignored")
            }
        };

        var def = fn.ToToolDefinition();
        Assert.Equal("function", def.Type);
        Assert.Equal("infobot_set", def.Function.Name);
        Assert.Equal("Set a factoid", def.Function.Description);
        Assert.False(def.Function.Strict);
        Assert.Contains("term", def.Function.Parameters.Required);
        Assert.DoesNotContain("text", def.Function.Parameters.Required);
        Assert.True(def.Function.Parameters.Properties.ContainsKey("term"));
        Assert.False(def.Function.Parameters.Properties.ContainsKey(" "));
    }
}
