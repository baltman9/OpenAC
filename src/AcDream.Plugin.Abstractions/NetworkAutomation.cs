namespace AcDream.Plugin.Abstractions;

/// <summary>
/// What one other client running on this same computer last published about
/// itself, so plugins can coordinate several characters played side by side.
/// </summary>
/// <param name="ClientId">
/// A stable non-zero number identifying the publishing client instance for as
/// long as it runs.
/// </param>
/// <param name="PlayerId">That client's character object id.</param>
/// <param name="Name">That client's character name.</param>
/// <param name="WorldName">The world that client is logged in to.</param>
/// <param name="Position">Where that character stood when it last published.</param>
/// <param name="Tags">
/// Free-form labels the publishing client was started with, for example to mark
/// the role it plays; empty when it was given none.
/// </param>
/// <param name="CurrentHealth">That character's current health points.</param>
/// <param name="CurrentMana">That character's current mana points.</param>
/// <param name="CurrentStamina">That character's current stamina points.</param>
/// <param name="MaxHealth">That character's maximum health points.</param>
/// <param name="MaxMana">That character's maximum mana points.</param>
/// <param name="MaxStamina">That character's maximum stamina points.</param>
/// <param name="Heading">
/// The direction that character faced, in degrees clockwise from north.
/// </param>
public readonly record struct PluginNetworkClient(
    uint ClientId,
    uint PlayerId,
    string Name,
    string WorldName,
    PluginNavigationPosition Position,
    IReadOnlyList<string> Tags,
    uint CurrentHealth,
    uint CurrentMana,
    uint CurrentStamina,
    uint MaxHealth,
    uint MaxMana,
    uint MaxStamina,
    float Heading);

/// <summary>
/// One spell another client on this computer said it cast, as this client
/// reads it back. Nothing here is authoritative: it is what that client
/// believed about its own cast, so treat it as a hint about what is already
/// on a target rather than as a fact from the server.
/// </summary>
/// <param name="Sequence">
/// A number that only grows, in the order this client first read these casts.
/// Hand the highest one back to the next capture to pick up where you left
/// off. Numbers can skip: a cast this client cannot make sense of is counted
/// and then dropped.
/// </param>
/// <param name="ClientId">
/// The publishing client instance, matching
/// <see cref="PluginNetworkClient.ClientId"/>.
/// </param>
/// <param name="CasterObjectId">
/// The character that cast it. Never this client's own character: a client
/// does not read back its own casts.
/// </param>
/// <param name="TargetObjectId">The object it was cast at.</param>
/// <param name="SpellId">
/// The spell, as this client's own spell table knows it. A spell this client
/// cannot find in its own table is never reported.
/// </param>
/// <param name="EffectiveSkill">
/// The magic skill the caster was casting with, as that client reckoned it.
/// Zero when the caster did not say.
/// </param>
/// <param name="SecondsRemaining">
/// Seconds left of the effect, counted down from the duration the caster
/// published by however long ago it said the cast happened, and never below
/// zero. Zero for an attempt, which carries no duration at all.
/// </param>
/// <param name="Landed">
/// True when the caster said the spell landed, false when this is only an
/// attempt that may still fizzle or be resisted.
/// </param>
public readonly record struct PluginPeerCast(
    long Sequence,
    uint ClientId,
    uint CasterObjectId,
    uint TargetObjectId,
    uint SpellId,
    int EffectiveSkill,
    double SecondsRemaining,
    bool Landed);

/// <summary>
/// Seeing the other clients this computer is running. Each client publishes its
/// own character periodically and reads what the others published; nothing is
/// sent to the game server and nothing leaves the machine.
/// </summary>
public interface INetworkAutomation
{
    /// <summary>
    /// True when this host publishes and reads peer state. The default
    /// implementation always reports false.
    /// </summary>
    bool IsAvailable => false;

    /// <summary>
    /// The other clients on this computer, never including the caller's own.
    /// </summary>
    /// <returns>
    /// One entry per peer whose published state is still recent and well
    /// formed; an empty list when there are no such peers or the host does not
    /// support peers, which is what the default implementation returns.
    /// </returns>
    IReadOnlyList<PluginNetworkClient> CaptureClients() =>
        Array.Empty<PluginNetworkClient>();

    /// <summary>
    /// Tells the other clients on this computer that this character has
    /// begun casting a spell at something, before it is known whether it
    /// lands. Use it so a second character can decide not to start the same
    /// spell at the same target; it says nothing about an effect being in
    /// place, which is what <see cref="AnnounceCastSuccess"/> is for.
    /// </summary>
    /// <param name="targetObjectId">The object being cast at.</param>
    /// <param name="spellId">
    /// The spell, which must be one this client's own spell table knows.
    /// </param>
    /// <param name="effectiveSkill">
    /// The magic skill the character is casting with, or zero to say
    /// nothing about it. A negative number is refused.
    /// </param>
    /// <returns>
    /// False for a zero target, a spell this client's spell table does not
    /// know, a negative skill, a character that is not in the world, or a
    /// host that does not tell other clients anything -- which is what the
    /// default implementation does.
    /// </returns>
    bool AnnounceCastAttempt(
        uint targetObjectId,
        uint spellId,
        int effectiveSkill) => false;

    /// <summary>
    /// Tells the other clients on this computer that a spell this character
    /// cast has landed and how long its effect lasts, so another character
    /// can stand down rather than re-landing it.
    /// </summary>
    /// <param name="targetObjectId">The object it landed on.</param>
    /// <param name="spellId">
    /// The spell, which must be one this client's own spell table knows.
    /// </param>
    /// <param name="effectiveSkill">
    /// The magic skill it was cast with, or zero to say nothing about it.
    /// </param>
    /// <param name="durationSeconds">
    /// How long the effect lasts in total, from now. A reader is handed
    /// what is left of it rather than this number.
    /// </param>
    /// <returns>
    /// False for a zero target, a spell this client's spell table does not
    /// know, a negative skill, a duration that is not a finite positive
    /// number of seconds or is longer than a day, a character that is not in
    /// the world, or a host that does not tell other clients anything --
    /// which is what the default implementation does.
    /// </returns>
    bool AnnounceCastSuccess(
        uint targetObjectId,
        uint spellId,
        int effectiveSkill,
        double durationSeconds) => false;

    /// <summary>
    /// What the other clients on this computer have said they cast. Nothing
    /// is applied to this client's own bookkeeping by reading it: what to do
    /// with a peer's cast is the plugin's decision, and a plugin that wants
    /// a landed one counted as an effect in place passes it to
    /// <see cref="IEnchantmentAutomation.ReportCast"/> with
    /// <see cref="PluginPeerCast.SecondsRemaining"/> as the duration.
    /// </summary>
    /// <param name="afterSequence">
    /// The highest <see cref="PluginPeerCast.Sequence"/> already dealt with,
    /// or zero for everything still recent. Hand back the highest sequence
    /// from one call to the next and each cast arrives once.
    /// </param>
    /// <returns>
    /// The casts above that sequence, oldest first, never including this
    /// character's own and never including a spell this client's own spell
    /// table cannot identify. Empty when there is nothing new, when no other
    /// client on this computer is playing in the same world, or on a host
    /// that does not read other clients -- which is what the default
    /// implementation returns.
    /// </returns>
    IReadOnlyList<PluginPeerCast> CaptureCasts(long afterSequence) =>
        Array.Empty<PluginPeerCast>();
}
