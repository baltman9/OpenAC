using System.Globalization;
using AcDream.Core.CharGen;

namespace AcDream.App.UI.Layout;

/// <summary>
/// Skill display names. The authored skill table in the client data is the
/// source for every skill it still carries: character creation reads it
/// through <see cref="ChargenOptions"/>, and appraisals read it through
/// <see cref="RetailAppraisalNameResolver"/>. Both fall back to
/// <see cref="Fallback(int)"/>, which covers the retired skills the authored
/// table dropped (and any host running without client data). Keep the table
/// spelled the way the authored data spells it, so a fallback name never
/// disagrees with the name the same skill gets everywhere else.
/// </summary>
internal static class RetailSkillNames
{
    internal static string Resolve(ChargenOptions options, uint skillId)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.TryGetSkillDetail(skillId, out ChargenSkillDetail detail)
               && !string.IsNullOrWhiteSpace(detail.Name)
            ? detail.Name
            : Fallback((int)skillId);
    }

    internal static string Fallback(int skill) => skill switch
    {
        1 => "Axe",
        2 => "Bow",
        3 => "Crossbow",
        4 => "Dagger",
        5 => "Mace",
        6 => "Melee Defense",
        7 => "Missile Defense",
        8 => "Sling",
        9 => "Spear",
        10 => "Staff",
        11 => "Sword",
        12 => "Thrown Weapon",
        13 => "Unarmed Combat",
        14 => "Arcane Lore",
        15 => "Magic Defense",
        16 => "Mana Conversion",
        17 => "Spellcraft",
        18 => "Item Tinkering",
        19 => "Assess Person",
        20 => "Deception",
        21 => "Healing",
        22 => "Jump",
        23 => "Lockpick",
        24 => "Run",
        25 => "Awareness",
        26 => "Armor Repair",
        27 => "Assess Creature",
        28 => "Weapon Tinkering",
        29 => "Armor Tinkering",
        30 => "Magic Item Tinkering",
        31 => "Creature Enchantment",
        32 => "Item Enchantment",
        33 => "Life Magic",
        34 => "War Magic",
        35 => "Leadership",
        36 => "Loyalty",
        37 => "Fletching",
        38 => "Alchemy",
        39 => "Cooking",
        40 => "Salvaging",
        41 => "Two Handed Combat",
        42 => "Gearcraft",
        43 => "Void Magic",
        44 => "Heavy Weapons",
        45 => "Light Weapons",
        46 => "Finesse Weapons",
        47 => "Missile Weapons",
        48 => "Shield",
        49 => "Dual Wield",
        50 => "Recklessness",
        51 => "Sneak Attack",
        52 => "Dirty Fighting",
        53 => "Challenge",
        54 => "Summoning",
        _ => $"Skill {skill.ToString(CultureInfo.InvariantCulture)}",
    };

    internal static bool IsUnnamed(string name)
        => name.StartsWith("Skill ", StringComparison.Ordinal);
}
