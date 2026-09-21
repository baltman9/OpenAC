namespace AcDream.Plugin.Abstractions;

/// <summary>Which of the character's pools a request spends on.</summary>
public enum PluginAdvancementKind
{
    /// <summary>
    /// A primary attribute, named by <see cref="PluginAttributeInfo.StatId"/>
    /// and paid for in experience.
    /// </summary>
    Attribute = 0,

    /// <summary>
    /// One of health, stamina and mana, named by
    /// <see cref="PluginVitalInfo.StatId"/> and paid for in experience.
    /// </summary>
    Vital,

    /// <summary>
    /// A skill, named by <see cref="PluginSkillInfo.SkillId"/> and paid for in
    /// experience.
    /// </summary>
    Skill,

    /// <summary>
    /// Training a skill, named by <see cref="PluginSkillInfo.SkillId"/>. This
    /// one is paid for in skill credits rather than experience, so its cost is
    /// a small number and is bounded by
    /// <see cref="PluginAdvancement.MaxSkillCredits"/>.
    /// </summary>
    TrainSkill,
}

/// <summary>The bounds the client checks an advancement request against.</summary>
public static class PluginAdvancement
{
    /// <summary>
    /// The largest experience cost the client will carry to the server. It is
    /// far above anything a single raise can cost and far below the largest
    /// number the field can hold, so a cost that overflowed or was never set
    /// is refused here instead of being sent.
    /// </summary>
    public const ulong MaxExperienceCost = 1_000_000_000_000UL;

    /// <summary>
    /// The largest number of skill credits the client will carry to the
    /// server for <see cref="PluginAdvancementKind.TrainSkill"/>, for the same
    /// reason as <see cref="MaxExperienceCost"/>. Training costs a handful of
    /// credits, so anything near this is already a mistake.
    /// </summary>
    public const uint MaxSkillCredits = 1_000u;
}

/// <summary>How the client answered a plugin's advancement request.</summary>
public enum PluginAdvancementStatus
{
    /// <summary>
    /// The request could not be made: the character is not in the world,
    /// there is no live session, or this host does not provide the surface.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// Nothing is named by that stat id for that kind: a zero, an attribute or
    /// pool number outside the ones that exist, or a skill the client has
    /// never been told the character has.
    /// </summary>
    UnknownStat,

    /// <summary>
    /// The cost was zero, or larger than the client will carry -- see
    /// <see cref="PluginAdvancement.MaxExperienceCost"/> and
    /// <see cref="PluginAdvancement.MaxSkillCredits"/>.
    /// </summary>
    InvalidCost,

    /// <summary>
    /// The request went out to the server. Whether the server allows the
    /// spend is its own decision, and arrives later as an updated skill,
    /// attribute or pool.
    /// </summary>
    Sent,

    /// <summary>
    /// The client declined to send it; <c>Notice</c> says why when there is
    /// something to say.
    /// </summary>
    Refused,
}

/// <summary>The outcome of one advancement request, with an optional explanation.</summary>
/// <param name="Status">What the client did with the request.</param>
/// <param name="Notice">A short human-readable reason, when there is one.</param>
public readonly record struct PluginAdvancementResult(
    PluginAdvancementStatus Status,
    string? Notice = null)
{
    /// <summary>True when the request actually went out to the server.</summary>
    public bool Accepted => Status == PluginAdvancementStatus.Sent;
}
