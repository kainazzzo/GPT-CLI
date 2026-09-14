using GPT.CLI.Chat.Discord;
using WelcomeModuleExample;
using Xunit;

namespace GptCli.Modules.Tests.Welcome;

public sealed class WelcomeModuleTests
{
    [Fact]
    public void Validations_and_nudge_cooldown()
    {
        var welcome = new InstructionGPT.WelcomeState
        {
            Validations = new List<InstructionGPT.WelcomeValidationRule>
            {
                new() { Id = "ack", Type = "acknowledge" },
                new() { Id = "react", Type = "reaction", Value = "✅" },
                new() { Id = "phrase", Type = "phrase", Value = "I agree" }
            }
        };
        var user = WelcomeOnboardingModule.GetOrCreateUserState(welcome, 5);
        Assert.Same(user, WelcomeOnboardingModule.GetOrCreateUserState(welcome, 5));

        Assert.True(WelcomeOnboardingModule.ApplyAcknowledgeValidation(welcome, user));
        Assert.Contains("ack", user.CompletedValidations);
        Assert.False(WelcomeOnboardingModule.ApplyAcknowledgeValidation(welcome, user));

        Assert.True(WelcomeOnboardingModule.ApplyReactionValidation(welcome, user, "✅"));
        Assert.False(WelcomeOnboardingModule.ApplyReactionValidation(welcome, user, "❌"));

        Assert.True(WelcomeOnboardingModule.ApplyPhraseValidation(welcome, user, "yes I agree to this"));
        Assert.False(WelcomeOnboardingModule.ApplyPhraseValidation(welcome, user, "nope"));

        Assert.True(WelcomeOnboardingModule.ShouldNudge(user));
        user.LastNudgeUtc = DateTime.UtcNow;
        Assert.False(WelcomeOnboardingModule.ShouldNudge(user));
        user.LastNudgeUtc = DateTime.UtcNow.AddMinutes(-11);
        Assert.True(WelcomeOnboardingModule.ShouldNudge(user));
    }

    [Fact]
    public void BuildRulesText_and_ListRules()
    {
        var empty = new InstructionGPT.WelcomeState();
        Assert.Contains("No rules", WelcomeOnboardingModule.ListRules(empty), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No rules", WelcomeOnboardingModule.BuildRulesText(empty), StringComparison.OrdinalIgnoreCase);

        empty.Rules.Add(new InstructionGPT.WelcomeRule { Id = "r1", Text = "Be kind" });
        empty.Rules.Add(new InstructionGPT.WelcomeRule { Id = "r2", Text = "No spam" });
        var listed = WelcomeOnboardingModule.ListRules(empty);
        Assert.Contains("1. Be kind", listed, StringComparison.Ordinal);
        Assert.Contains("2. No spam", listed, StringComparison.Ordinal);
        Assert.Contains("Be kind", WelcomeOnboardingModule.BuildRulesText(empty), StringComparison.Ordinal);
    }
}
