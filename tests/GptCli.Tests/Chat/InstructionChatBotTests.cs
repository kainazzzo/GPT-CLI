using Betalgo.Ranul.OpenAI.Contracts.Enums;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using GPT.CLI;
using GPT.CLI.Chat;
using Xunit;

namespace GptCli.Tests.Chat;

public sealed class InstructionChatBotTests
{
    [Fact]
    public void AddMessage_trims_oldest_when_history_exceeds_limit()
    {
        var bot = new InstructionChatBot(openAILogic: null, new GptOptions { MaxChatHistoryLength = 10 });
        bot.AddMessage(new ChatMessage(ChatCompletionRole.User, "hello")); // 5
        bot.AddMessage(new ChatMessage(ChatCompletionRole.User, "world!!!!")); // +9 => 14
        bot.AddMessage(new ChatMessage(ChatCompletionRole.User, "x")); // trims "hello", then +1 => 10

        Assert.Equal(2, bot.ChatBotState.Messages.Count);
        Assert.Equal("world!!!!", bot.ChatBotState.Messages.First().Content);
        Assert.Equal("x", bot.ChatBotState.Messages.Last().Content);
        Assert.Equal(10u, bot.ChatBotState.MessageLength);
    }

    [Fact]
    public void Instructions_add_remove_clear_and_join()
    {
        var bot = new InstructionChatBot();
        bot.AddInstruction(new ChatMessage(ChatCompletionRole.System, "one"));
        bot.AddInstruction(new ChatMessage(ChatCompletionRole.System, "two"));
        Assert.Equal("one\ntwo", bot.InstructionStr);

        bot.RemoveInstruction(0);
        Assert.Equal("two", bot.InstructionStr);

        bot.ClearInstructions();
        Assert.Empty(bot.ChatBotState.Instructions);
        Assert.Equal(string.Empty, bot.InstructionStr);
    }

    [Fact]
    public void ClearMessages_empties_the_queue()
    {
        var bot = new InstructionChatBot(openAILogic: null, new GptOptions());
        bot.AddMessage(new ChatMessage(ChatCompletionRole.User, "keep"));
        bot.ClearMessages();
        Assert.Empty(bot.ChatBotState.Messages);
    }
}
