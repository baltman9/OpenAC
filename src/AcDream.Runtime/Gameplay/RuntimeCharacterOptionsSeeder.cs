using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Raised when a session document declares character options that cannot be
/// satisfied: a name outside the declarable set, or a pair of options the
/// client will never hold at once.
/// </summary>
public sealed class RuntimeDeclaredCharacterOptionsException : Exception
{
    public RuntimeDeclaredCharacterOptionsException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The character options a session document may declare, and how a declared
/// map is read.
///
/// A character's options live on the server, so declaring one in a session
/// document means changing the character itself. Only options an unattended
/// session genuinely needs are declarable: what it accepts from others, what
/// it does in a fight, and which chat channels it listens to. Anything to do
/// with how the world is drawn or how the panels behave is deliberately not
/// here; it would be meaningless for a client that draws nothing, and a
/// surprise for a player whose character was borrowed for a run.
///
/// Both clients read the list from here, so a document that starts one starts
/// the other and means the same thing on both.
/// </summary>
public static class RuntimeDeclaredCharacterOptions
{
    /// <summary>Every option a session document may declare.</summary>
    public static IReadOnlySet<CharacterOptionId> Allowed { get; } =
        new HashSet<CharacterOptionId>
        {
            CharacterOptionId.IgnoreAllegianceRequests,
            CharacterOptionId.IgnoreFellowshipRequests,
            CharacterOptionId.IgnoreTradeRequests,
            CharacterOptionId.AllowGive,
            CharacterOptionId.FellowshipShareXP,
            CharacterOptionId.AcceptLootPermits,
            CharacterOptionId.FellowshipShareLoot,
            CharacterOptionId.FellowshipAutoAcceptRequests,
            CharacterOptionId.DisplayAllegianceLogonNotifications,
            CharacterOptionId.UseChargeAttack,
            CharacterOptionId.UseCraftSuccessDialog,
            CharacterOptionId.AutoRepeatAttack,
            CharacterOptionId.LeadMissileTargets,
            CharacterOptionId.UseFastMissiles,
            CharacterOptionId.ConfirmVolatileRareUse,
            CharacterOptionId.AppearOffline,
            CharacterOptionId.ListenToAllegianceChat,
            CharacterOptionId.ListenToGeneralChat,
            CharacterOptionId.ListenToTradeChat,
            CharacterOptionId.ListenToLFGChat,
            CharacterOptionId.ListenToRoleplayChat,
            CharacterOptionId.ListenToSocietyChat,
            CharacterOptionId.MainPackPreferred,
            CharacterOptionId.ToggleRun,
            CharacterOptionId.AutoTarget,
            CharacterOptionId.SalvageMultiple,
        };

    /// <summary>The same list, spelled the way a document spells it.</summary>
    public static IReadOnlySet<string> AllowedNames { get; } =
        new HashSet<string>(
            Allowed.Select(static id => id.ToString()),
            StringComparer.Ordinal);

    /// <summary>
    /// Checks a declared map and says what is wrong with it, in words a client
    /// can put in front of whoever wrote the document. The session id only
    /// names which session the complaint is about.
    /// </summary>
    /// <returns>Null when the map is fine.</returns>
    public static string? Describe(
        string sessionId,
        IReadOnlyDictionary<string, bool>? declared)
    {
        if (declared is null)
            return null;

        foreach (string name in declared.Keys)
        {
            if (!AllowedNames.Contains(name))
            {
                return $"Session '{sessionId}' characterOptions declares "
                    + $"'{name}', which is not a declarable character option "
                    + "name.";
            }
        }

        if (declared.TryGetValue(
                nameof(CharacterOptionId.IgnoreFellowshipRequests),
                out bool ignoreFellowship)
            && ignoreFellowship
            && declared.TryGetValue(
                nameof(CharacterOptionId.FellowshipAutoAcceptRequests),
                out bool autoAcceptFellowship)
            && autoAcceptFellowship)
        {
            return $"Session '{sessionId}' characterOptions declares both "
                + $"'{nameof(CharacterOptionId.IgnoreFellowshipRequests)}' and "
                + $"'{nameof(CharacterOptionId.FellowshipAutoAcceptRequests)}' "
                + "as true; the two exclude one another, so the combination is "
                + "unsatisfiable -- turning one on always clears the other.";
        }

        return null;
    }

    /// <summary>
    /// Reads a declared map into option ids. A name that is not declarable is
    /// refused here rather than dropped, so a mistake can never turn into a
    /// session that quietly runs with different options.
    /// </summary>
    public static Dictionary<CharacterOptionId, bool> Parse(
        string sessionId,
        IReadOnlyDictionary<string, bool>? declared)
    {
        var parsed = new Dictionary<CharacterOptionId, bool>();
        if (declared is null)
            return parsed;

        if (Describe(sessionId, declared) is { } complaint)
            throw new RuntimeDeclaredCharacterOptionsException(complaint);

        foreach ((string name, bool value) in declared)
            parsed[Enum.Parse<CharacterOptionId>(name, ignoreCase: false)] = value;

        return parsed;
    }
}

/// <summary>
/// Puts the character options a session document declared onto the character,
/// once, when the session is far enough into the world to ask for them.
///
/// The options belong to the server, so this cannot simply set them: it waits
/// until the server has said what the character's options currently are,
/// compares that with what the document declared, and sends a change only for
/// the ones that differ. Options the server stores as it receives them need
/// nothing more; the rest are followed by one save at the end, so a session
/// with ten differences costs one save rather than ten.
///
/// Both clients seed the same way from the same document, so a character
/// started by either arrives with the same options.
/// </summary>
public sealed class RuntimeCharacterOptionsSeeder
{
    private readonly IReadOnlyList<KeyValuePair<CharacterOptionId, bool>> _declared;
    private readonly GameRuntime _runtime;
    private readonly IRuntimeCharacterCommands _commands;
    private bool _loginCompleteSent;

    public RuntimeCharacterOptionsSeeder(
        IReadOnlyDictionary<CharacterOptionId, bool> declared,
        GameRuntime runtime,
        IRuntimeCharacterCommands commands)
    {
        ArgumentNullException.ThrowIfNull(declared);
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _declared = [.. declared.OrderBy(static pair => (uint)pair.Key)];
    }

    public bool HasDeclaredOptions => _declared.Count > 0;

    /// <summary>The client has told the server its login is complete.</summary>
    public void NoteLoginCompleteSent()
    {
        _loginCompleteSent = true;
        TryDiffAndSend();
    }

    /// <summary>The server has said what the character's options are.</summary>
    public void NoteOptionsSeeded() => TryDiffAndSend();

    private void TryDiffAndSend()
    {
        if (!_loginCompleteSent || _declared.Count == 0)
            return;

        RuntimeCharacterOptionsState options = _runtime.CharacterOwner.Options;
        if (!options.HasServerSeed)
            return;

        RuntimeGenerationToken generation = _runtime.Generation;
        bool needsFlush = false;
        foreach ((CharacterOptionId id, bool desired) in _declared)
        {
            if (!CharacterOptionTable.TryGet(id, out CharacterOptionTableEntry entry))
                continue;

            uint word = entry.IsOptions1 ? options.Options1 : options.Options2;
            bool current = (word & entry.Mask) != 0u;
            if (current == desired)
                continue;

            _commands.SetSingleOption(generation, (uint)id, desired);
            if (!entry.IsAutoSave)
                needsFlush = true;
        }

        if (needsFlush)
            _commands.SaveOptions(generation);
    }
}
