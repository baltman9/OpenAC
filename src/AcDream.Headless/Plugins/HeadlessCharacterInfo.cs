using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Plugins;

/// <summary>
/// Reports the headless host's own live character identity to plugins.
/// </summary>
/// <remarks>
/// Only the fields plugins actually key session/character lifetime off of
/// (name, world, account, object id, roster index, in-world state) are
/// sourced from the runtime here. Vitals, skills, attributes, and active
/// enchantments still forward to <see cref="NoOpAutomationSurface"/>'s
/// stub values -- no headless macro reads them yet, and wiring them up is
/// left for a follow-up once a macro needs them.
/// </remarks>
internal sealed class HeadlessCharacterInfo : ICharacterInfo
{
    private readonly GameRuntime _runtime;

    internal HeadlessCharacterInfo(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public bool IsInWorld =>
        _runtime.Lifecycle.State == RuntimeLifecycleState.InWorld;

    public string Name
    {
        get
        {
            uint playerId = _runtime.PlayerIdentity.ServerGuid;
            string? hydratedName = _runtime.InventoryOwner.Objects.Get(playerId)?.Name;
            if (!string.IsNullOrEmpty(hydratedName))
                return hydratedName;
            // The player object hasn't streamed in yet at the moment login
            // completes, but the character roster already carried the name
            // from the selection edge -- use it until the object arrives
            // and takes over.
            return _runtime.CharacterSelection.TryGet(
                playerId, out RuntimeCharacterSelectionEntry entry)
                ? entry.Name
                : string.Empty;
        }
    }

    public string WorldName => _runtime.CharacterSelection.Snapshot.WorldName;

    public int ServerPopulation => _runtime.CharacterSelection.Snapshot.ServerPopulation ?? -1;

    public string AccountName => _runtime.CharacterSelection.Snapshot.AccountName;

    public int CharacterIndex =>
        _runtime.CharacterSelection.TryGet(
            _runtime.PlayerIdentity.ServerGuid, out RuntimeCharacterSelectionEntry entry)
            ? entry.ActiveIndex
            : -1;

    public uint ObjectId => _runtime.PlayerIdentity.ServerGuid;

    public uint CurrentHealth => NoOpAutomationSurface.Instance.Character.CurrentHealth;
    public uint MaxHealth => NoOpAutomationSurface.Instance.Character.MaxHealth;
    public uint CurrentStamina => NoOpAutomationSurface.Instance.Character.CurrentStamina;
    public uint MaxStamina => NoOpAutomationSurface.Instance.Character.MaxStamina;
    public uint CurrentMana => NoOpAutomationSurface.Instance.Character.CurrentMana;
    public uint MaxMana => NoOpAutomationSurface.Instance.Character.MaxMana;

    public IReadOnlyList<PluginSkillInfo> Skills =>
        NoOpAutomationSurface.Instance.Character.Skills;

    public IReadOnlyList<PluginAttributeInfo> Attributes =>
        NoOpAutomationSurface.Instance.Character.Attributes;

    public IReadOnlyList<PluginActiveEnchantment> ActiveEnchantments =>
        NoOpAutomationSurface.Instance.Character.ActiveEnchantments;

    public bool TryGetSkill(uint skillId, out PluginSkillInfo skill) =>
        NoOpAutomationSurface.Instance.Character.TryGetSkill(skillId, out skill);
}
