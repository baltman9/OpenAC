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
}
