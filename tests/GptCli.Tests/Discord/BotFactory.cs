using Discord.WebSocket;
using GPT.CLI;
using GPT.CLI.Chat.Discord;
using GptCli.Tests.TestDoubles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GptCli.Tests.Discord;

internal static class BotFactory
{
    public static InstructionGPT Create(
        GptOptions options = null,
        FakeOpenAiHttp fake = null,
        HttpClient http = null,
        IConfiguration configuration = null)
    {
        options ??= new GptOptions
        {
            Model = "gpt-4o",
            ApiKey = "sk-test",
            BotToken = "bot-token",
            LearningPersonalityPrompt = "be nice"
        };
        configuration ??= new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["GPT:PromptDebounceSeconds"] = "5"
            })
            .Build();

        http ??= new HttpClient(fake ?? new FakeOpenAiHttp(), disposeHandler: false);

        return new InstructionGPT(
            new DiscordSocketClient(),
            configuration,
            new OpenAILogic(options, http),
            options,
            new ServiceCollection().BuildServiceProvider(),
            new FakeHostApplicationLifetime());
    }
}
