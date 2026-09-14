using GPT.CLI.Chat.Discord;
using Xunit;

namespace GptCli.Tests.Discord;

public sealed class ModuleEnablementTests
{
    private static InstructionGPT.ChannelState State(Action<InstructionGPT.ChannelOptions> configure = null)
    {
        var options = new InstructionGPT.ChannelOptions();
        configure?.Invoke(options);
        return new InstructionGPT.ChannelState { Options = options };
    }

    [Fact]
    public void IsModuleEnabled_defaults_to_false()
    {
        Assert.False(InstructionGPT.IsModuleEnabled(State(), "infobot"));
        Assert.False(InstructionGPT.IsModuleEnabled(null, "infobot"));
        Assert.False(InstructionGPT.IsModuleEnabled(new InstructionGPT.ChannelState(), "infobot"));
        Assert.False(InstructionGPT.IsModuleEnabled(State(), " "));
    }

    [Fact]
    public void IsModuleEnabled_respects_explicit_flag()
    {
        var on = State(o => o.ModulesEnabled["infobot"] = true);
        var off = State(o => o.ModulesEnabled["infobot"] = false);
        Assert.True(InstructionGPT.IsModuleEnabled(on, "Infobot"));
        Assert.False(InstructionGPT.IsModuleEnabled(off, "infobot"));
    }

    [Fact]
    public void Polls_alias_maps_to_poll()
    {
        var state = State(o => o.ModulesEnabled["poll"] = true);
        Assert.True(InstructionGPT.IsModuleEnabled(state, "polls"));
        Assert.Equal("poll", InstructionGPT.NormalizeModuleId("POLLS"));
    }

    [Fact]
    public void Casino_falls_back_to_legacy_flag_when_key_missing()
    {
        var legacyOn = State(o => o.CasinoEnabled = true);
        Assert.True(InstructionGPT.IsModuleEnabled(legacyOn, "casino"));

        var explicitOff = State(o =>
        {
            o.CasinoEnabled = true;
            o.ModulesEnabled["casino"] = false;
        });
        Assert.False(InstructionGPT.IsModuleEnabled(explicitOff, "casino"));
    }

    [Fact]
    public void SetModuleEnabled_syncs_infobot_and_casino_legacy_flags()
    {
        var state = State();
        InstructionGPT.SetModuleEnabled(state, "infobot", true);
        Assert.True(state.Options.LearningEnabled);
        Assert.True(InstructionGPT.IsModuleEnabled(state, "infobot"));

        InstructionGPT.SetModuleEnabled(state, "casino", true);
        Assert.True(state.Options.CasinoEnabled);
        Assert.True(InstructionGPT.IsModuleEnabled(state, "casino"));

        InstructionGPT.SetModuleEnabled(state, "infobot", false);
        Assert.False(state.Options.LearningEnabled);
    }
}
