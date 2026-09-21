using AcDream.Core.Items;

namespace AcDream.Core.Spells;

public readonly record struct SpellTargetPolicyResult(
    bool Allowed,
    string? Message)
{
    public static SpellTargetPolicyResult Accept { get; } =
        new(true, null);
}

public static class RetailSpellTargetPolicy
{
    private const uint SpecialTargetMask = 0x00008107u;

    /// <summary>
    /// Whether the caster can be the target: a self-targeted or untargeted
    /// spell, or one whose mask carries a self bit. The same rule as the
    /// first refusal in <see cref="Evaluate"/>, for a caller that has no
    /// target object yet.
    /// </summary>
    public static bool CanTargetSelf(SpellMetadata spell) =>
        spell.IsSelfTargeted
        || spell.IsUntargeted
        || (spell.TargetMask & SpecialTargetMask) != 0u;

    public static SpellTargetPolicyResult Evaluate(
        uint localPlayerId,
        ClientObject target,
        SpellMetadata spell)
    {
        uint mask = spell.TargetMask;
        uint special = mask & SpecialTargetMask;
        if (target.ObjectId == localPlayerId && special == 0u)
            return new(false, "You cannot cast this spell upon yourself.");
        if (target.StackSize > 1)
            return new(false, "Cannot cast spell on a stack of items.");

        if (((uint)target.Type & mask) == 0u && special == 0u)
            return new(false, $"This spell cannot be cast on {target.Name}.");

        var flags = (PublicWeenieFlags)
            target.PublicWeenieBitfield.GetValueOrDefault();
        bool playerOrAttackable = (flags
            & (PublicWeenieFlags.Player
                | PublicWeenieFlags.Attackable)) != 0;
        if (!playerOrAttackable || target.PetOwnerId != 0u)
            return new(false, $"This spell cannot be cast on {target.Name}.");

        return SpellTargetPolicyResult.Accept;
    }
}
