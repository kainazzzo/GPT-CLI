namespace GPT.CLI.Chat.Dnd;

public sealed record DndRuleset(
    int BaseDefense,
    int SpellMpCost,
    int AttackDamageDiceSides,
    int SpellDamageDiceSides,
    int DefaultCheckDc = 12,
    int MinCheckDc = 8,
    int MaxCheckDc = 18)
{
    public static DndRuleset Default { get; } = new(
        BaseDefense: 10,
        SpellMpCost: 3,
        AttackDamageDiceSides: 8,
        SpellDamageDiceSides: 10);

    public int GetStatMod(int stat)
    {
        // DnD-style modifiers: floor((stat - 10) / 2)
        // Integer division truncates toward zero, so do an explicit floor for negatives.
        var x = stat - 10;
        if (x >= 0)
        {
            return x / 2;
        }

        // e.g. -1 => -1, -2 => -1, -3 => -2
        return -((Math.Abs(x) + 1) / 2);
    }

    public int GetDefenseTarget(DndStats stats)
        => BaseDefense + GetStatMod(stats.Def) + GetStatMod(stats.Dex);

    public int GetInitiativeModifier(DndStats stats)
        => GetStatMod(stats.Dex) + Math.Max(0, GetStatMod(stats.Luck));

    public int GetAttackToHitModifier(DndStats stats)
        => GetStatMod(stats.Str);

    public int GetSpellToHitModifier(DndStats stats)
        => GetStatMod(stats.SpellPower);

    public int ClampCheckDc(int dc)
        => Math.Clamp(dc, MinCheckDc, MaxCheckDc);

    public int GetCheckModifier(DndStats stats, DndCheckStat stat)
    {
        if (stats == null)
        {
            return 0;
        }

        var value = stat switch
        {
            DndCheckStat.Str => stats.Str,
            DndCheckStat.Def => stats.Def,
            DndCheckStat.Dex => stats.Dex,
            DndCheckStat.SpellPower => stats.SpellPower,
            DndCheckStat.Luck => stats.Luck,
            _ => 10
        };

        return GetStatMod(value);
    }
}

