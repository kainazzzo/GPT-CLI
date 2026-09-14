using GPT.CLI.Chat.Dnd;
using Xunit;

namespace GptCli.Dnd.Tests;

public sealed class DndRulesetTests
{
    private static readonly DndRuleset Rules = DndRuleset.Default;

    [Theory]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    [InlineData(12, 1)]
    [InlineData(14, 2)]
    [InlineData(8, -1)]
    [InlineData(9, -1)]
    [InlineData(7, -2)]
    [InlineData(1, -5)]
    public void GetStatMod_matches_dnd_floor_formula(int stat, int expected)
    {
        Assert.Equal(expected, Rules.GetStatMod(stat));
    }

    [Fact]
    public void GetDefenseTarget_adds_def_and_dex_mods_to_base()
    {
        var stats = new DndStats(Str: 10, Def: 12, Dex: 14, SpellPower: 10, Luck: 10);
        // Base 10 + Def +1 + Dex +2 = 13
        Assert.Equal(13, Rules.GetDefenseTarget(stats));
    }

    [Fact]
    public void GetInitiativeModifier_uses_dex_and_nonnegative_luck()
    {
        var withLuck = new DndStats(Str: 10, Def: 10, Dex: 14, SpellPower: 10, Luck: 16);
        // Dex +2 + Luck +3 = 5
        Assert.Equal(5, Rules.GetInitiativeModifier(withLuck));

        var unlucky = new DndStats(Str: 10, Def: 10, Dex: 14, SpellPower: 10, Luck: 6);
        // Dex +2 + max(0, Luck -2) = 2
        Assert.Equal(2, Rules.GetInitiativeModifier(unlucky));
    }

    [Fact]
    public void GetAttackAndSpellToHitModifiers_use_str_and_spell_power()
    {
        var stats = new DndStats(Str: 16, Def: 10, Dex: 10, SpellPower: 8, Luck: 10);
        Assert.Equal(3, Rules.GetAttackToHitModifier(stats));
        Assert.Equal(-1, Rules.GetSpellToHitModifier(stats));
    }

    [Fact]
    public void ClampCheckDc_and_GetCheckModifier_use_existing_stats()
    {
        Assert.Equal(8, Rules.ClampCheckDc(1));
        Assert.Equal(18, Rules.ClampCheckDc(99));
        Assert.Equal(12, Rules.ClampCheckDc(12));

        var stats = new DndStats(Str: 16, Def: 8, Dex: 12, SpellPower: 10, Luck: 14);
        Assert.Equal(3, Rules.GetCheckModifier(stats, DndCheckStat.Str));
        Assert.Equal(-1, Rules.GetCheckModifier(stats, DndCheckStat.Def));
        Assert.Equal(1, Rules.GetCheckModifier(stats, DndCheckStat.Dex));
        Assert.Equal(0, Rules.GetCheckModifier(stats, DndCheckStat.SpellPower));
        Assert.Equal(2, Rules.GetCheckModifier(stats, DndCheckStat.Luck));
    }
}
