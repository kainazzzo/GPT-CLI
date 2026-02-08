namespace GPT.CLI.Chat.Dnd;

public sealed record DndRuleset(
    int BaseDefense,
    int SpellMpCost,
    int AttackDamageDiceSides,
    int SpellDamageDiceSides)
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
}

