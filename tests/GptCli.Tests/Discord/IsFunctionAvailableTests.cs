using GPT.CLI.Chat.Discord;
using GPT.CLI.Chat.Discord.Commands;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class IsFunctionAvailableTests
{
    private static InstructionGPT.ChannelState StateWith(string moduleId, bool enabled)
    {
        return new InstructionGPT.ChannelState
        {
            Options = new InstructionGPT.ChannelOptions
            {
                ModulesEnabled = new Dictionary<string, bool> { [moduleId] = enabled }
            }
        };
    }

    [Fact]
    public void Null_function_is_unavailable()
    {
        Assert.False(InstructionGPT.IsFunctionAvailable(StateWith("infobot", true), null));
    }

    [Fact]
    public void Empty_module_id_is_always_available()
    {
        var fn = new GptCliFunction { ToolName = "ping", ModuleId = " " };
        Assert.True(InstructionGPT.IsFunctionAvailable(StateWith("infobot", false), fn));
    }

    [Fact]
    public void ExposeWhenModuleDisabled_stays_available()
    {
        var fn = new GptCliFunction
        {
            ToolName = "enable",
            ModuleId = "infobot",
            ExposeWhenModuleDisabled = true
        };
        Assert.True(InstructionGPT.IsFunctionAvailable(StateWith("infobot", false), fn));
    }

    [Fact]
    public void Otherwise_follows_module_enablement()
    {
        var fn = new GptCliFunction { ToolName = "set", ModuleId = "infobot" };
        Assert.False(InstructionGPT.IsFunctionAvailable(StateWith("infobot", false), fn));
        Assert.True(InstructionGPT.IsFunctionAvailable(StateWith("infobot", true), fn));
    }
}
