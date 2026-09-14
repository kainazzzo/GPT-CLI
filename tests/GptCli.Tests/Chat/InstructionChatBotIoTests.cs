using Betalgo.Ranul.OpenAI.Contracts.Enums;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using GPT.CLI;
using GPT.CLI.Chat;
using GptCli.Tests.TestDoubles;
using Xunit;

namespace GptCli.Tests.Chat;

public sealed class InstructionChatBotIoTests
{
    [Fact]
    public async Task GetResponseAsync_uses_fake_openai_completion()
    {
        var fake = new FakeOpenAIService();
        fake.ChatResponse.Choices[0].Message = new ChatMessage(ChatCompletionRole.Assistant, "bot-says-hi");
        var options = new GptOptions { Model = "gpt-4o", Prompt = "be brief", MaxTokens = 16 };
        var logic = new OpenAILogic(fake.Service, options);
        var bot = new InstructionChatBot(logic, options);
        bot.AddInstruction(new ChatMessage(ChatCompletionRole.System, "stay short"));
        bot.AddMessage(new ChatMessage(ChatCompletionRole.User, "hello"));

        string last = null;
        await foreach (var response in bot.GetResponseAsync())
        {
            last = response.Choices[0].Message.Content;
        }

        Assert.Equal("bot-says-hi", last);
        Assert.Equal(1, fake.ChatCalls);
        Assert.NotNull(fake.LastChatRequest);
        Assert.Equal("gpt-4o", fake.LastChatRequest.Model);
    }

    [Fact]
    public async Task MapCommon_with_embed_files_injects_closest_documents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gptcli-map-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            [
              {"text":"alpha context","embed":[1.0,0.0]},
              {"text":"beta context","embed":[0.0,1.0]}
            ]
            """);
        try
        {
            var fake = new FakeOpenAIService { Embedding = new List<double> { 1.0, 0.0 } };
            var logic = new OpenAILogic(fake.Service, new GptOptions());
            var options = new GptOptions
            {
                Prompt = "what is alpha?",
                Model = "gpt-4o",
                ClosestMatchLimit = 1,
                EmbedFilenames = new[] { path }
            };

            var request = await ParameterMapping.MapCommon(
                options,
                logic,
                new ChatCompletionCreateRequest { Messages = new List<ChatMessage>() },
                ParameterMapping.Mode.Completion);

            Assert.Contains(request.Messages, m =>
                m.Role == ChatCompletionRole.User &&
                (m.Content ?? string.Empty).Contains("alpha context", StringComparison.Ordinal));
            Assert.DoesNotContain(request.Messages, m =>
                (m.Content ?? string.Empty).Contains("beta context", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
