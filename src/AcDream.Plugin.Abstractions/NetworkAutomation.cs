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
/// One line another client on this computer asked the clients around it to
/// run, as this client reads it back. It is a command line exactly as a
/// player would have typed it, and nothing about it is authoritative: it is
/// what that client asked for, not something the game server said.
/// </summary>
/// <param name="Sequence">
/// A number that only grows, in the order this client first read these
/// lines. Hand the highest one back to the next capture to pick up where you
/// left off. Numbers can skip: a line this client cannot use -- one aimed at
/// labels it does not answer to, or one it cannot make sense of -- is
/// counted and then dropped.
/// </param>
/// <param name="ClientId">
/// The publishing client instance, matching
/// <see cref="PluginNetworkClient.ClientId"/>.
/// </param>
/// <param name="SenderObjectId">
/// The character that asked for it. Never this client's own character: a
/// client does not read back its own broadcasts.
/// </param>
/// <param name="Tags">
/// The labels the sender aimed the line at, or an empty list when it was
/// aimed at every client. A line only ever reaches a client whose own labels
/// include one of these.
/// </param>
/// <param name="Line">The command line, as the sender wrote it.</param>
/// <param name="SentAt">
/// When the sending client said it asked for the line, by that client's
/// clock.
/// </param>
public readonly record struct PluginPeerCommand(
    long Sequence,
    uint ClientId,
    uint SenderObjectId,
    IReadOnlyList<string> Tags,
    string Line,
    DateTimeOffset SentAt);

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

    /// <summary>
    /// Sets the labels this client answers to, replacing whatever it was
    /// started with. The labels go in the note the other clients read, so
    /// they see this client under them, and they decide which broadcast
    /// command lines this client runs.
    /// </summary>
    /// <param name="tags">
    /// The labels. They are trimmed, empty ones are dropped, repeats
    /// ignoring case are folded together, and what is left is capped at 128.
    /// An empty list clears them, which leaves this client answering only to
    /// broadcasts aimed at everybody.
    /// </param>
    /// <returns>
    /// False for a null list, a label longer than 64 characters, or a host
    /// that tells other clients nothing -- which is what the default
    /// implementation does.
    /// </returns>
    bool SetTags(IReadOnlyList<string> tags) => false;

    /// <summary>
    /// Asks the other clients on this computer to run a line, as though the
    /// player had typed it into the chat entry there. It is the one
    /// free-form channel between clients: the receiving client submits the
    /// line through its own chat entry, so its own commands are consulted
    /// first, then the plugins' chat interceptors, then the verbs plugins
    /// and the client have registered, and anything left goes to a channel,
    /// a tell or the server exactly as typed speech does.
    /// </summary>
    /// <param name="line">
    /// The line, written exactly as it would be typed. It may be a plugin
    /// verb, one of the client's own commands, a server command or something
    /// to say; a line nothing claims is answered on the receiving client the
    /// way an unknown command typed there is answered.
    /// </param>
    /// <param name="tags">
    /// The labels to aim it at. Only a client whose own labels include one
    /// of these runs it. An empty or null list aims it at every client on
    /// this computer that is playing in the same world.
    /// </param>
    /// <param name="delayMilliseconds">
    /// How far apart the recipients run it. Every client that takes the line
    /// orders itself against the others by client id, with the sender first,
    /// and waits its own place in that order times this many milliseconds
    /// before running it -- so the first recipient waits one delay, the
    /// second two, and so on. Zero has them all run it as soon as they read
    /// it.
    /// </param>
    /// <returns>
    /// False for an empty line, a line longer than 512 characters or
    /// carrying a control character, more than 16 labels or one longer than
    /// 64 characters, a delay below zero or above sixty seconds, a character
    /// that is not in the world, or a host that tells other clients nothing
    /// -- which is what the default implementation does.
    /// </returns>
    /// <remarks>
    /// The sending client does not run its own line: it already knows what
    /// it asked for, and a plugin that wants the line run here as well runs
    /// it here itself.
    /// </remarks>
    bool BroadcastCommand(
        string line,
        IReadOnlyList<string> tags,
        int delayMilliseconds) => false;

    /// <summary>
    /// What the other clients on this computer have asked the clients around
    /// them to run. Reading them changes nothing: the client runs the lines
    /// meant for it by itself, and this is for a plugin that wants to see
    /// what was asked for, or to act on a line rather than leave it to a
    /// verb.
    /// </summary>
    /// <param name="afterSequence">
    /// The highest <see cref="PluginPeerCommand.Sequence"/> already dealt
    /// with, or zero for everything still recent. Hand back the highest
    /// sequence from one call to the next and each line arrives once.
    /// </param>
    /// <returns>
    /// The lines above that sequence, oldest first, never including this
    /// client's own and never including one aimed at labels this client does
    /// not answer to. Empty when there is nothing new, when no other client
    /// on this computer is playing in the same world, or on a host that does
    /// not read other clients -- which is what the default implementation
    /// returns.
    /// </returns>
    IReadOnlyList<PluginPeerCommand> CaptureCommands(long afterSequence) =>
        Array.Empty<PluginPeerCommand>();
}
