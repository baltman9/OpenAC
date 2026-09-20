using AcDream.Core.Net;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The live-session event route an arm runs. It is the production class both
/// clients build -- <see cref="LiveSessionEventRouter"/> over a
/// <see cref="RuntimeLiveEntitySessionController"/> -- attached to that arm's
/// own world connection, so a scripted server message travels the real route
/// into the real runtime owners.
///
/// The binding records here are the runtime owners both clients bind. Three
/// hooks each client adds on top of them are NOT under this, because neither
/// can be made without the thing that owns it:
/// * the windowed client's entity sink, which is its drawn-world hydration and
///   network-update controllers -- with nothing drawn there is no such sink, so
///   both arms run the runtime's own entity controller and an arm cannot speak
///   for what the drawn one adds;
/// * each client's confirmation hooks and its appraisal presentation, which
///   belong to the session host and the panel tree rather than to the route;
/// * the windowless client's console reporting.
/// A scenario that would turn on one of those is not run here. What IS under
/// it is everything a plugin can see: objects arriving and leaving, positions,
/// motion, physics state, health, death, chat and the container, use and
/// appraisal answers.
/// </summary>
internal static class ParityInboundRoute
{
    internal static LiveSessionEventRouter Create(
        GameRuntime runtime,
        WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(session);
        var entities = new RuntimeLiveEntitySessionController(runtime, session);
        return new LiveSessionEventRouter(
            session,
            entities.CreateSink(),
            new LiveEnvironmentSessionSink(
                change => _ = runtime.EnvironmentOwner.ApplyAdminEnvirons(change),
                runtime.EnvironmentOwner.SynchronizeFromServer),
            new LiveInventorySessionBindings(
                runtime.InventoryOwner.Objects,
                () => runtime.PlayerIdentity.ServerGuid,
                runtime.InventoryOwner.Shortcuts.Load,
                error =>
                {
                    runtime.InventoryOwner.ExternalContainers.ApplyUseDone(error);
                    runtime.ActionOwner.SpellCast.CompleteUse(error);
                    runtime.ActionOwner.Transactions.CompleteUse(error);
                },
                runtime.InventoryOwner.ItemMana,
                ExternalContainers: runtime.InventoryOwner.ExternalContainers,
                OnAppraisal: appraisal =>
                    runtime.ActionOwner.Transactions
                        .AcceptAppraisalResponse(appraisal.Guid),
                Vendor: runtime.InventoryOwner.Vendor,
                Book: runtime.BookOwner,
                PlayerName: () =>
                    runtime.InventoryOwner.Objects
                        .Get(runtime.PlayerIdentity.ServerGuid)?.Name
                    ?? string.Empty),
            new LiveCharacterSessionBindings(
                runtime.ActionOwner.Combat,
                runtime.CharacterOwner,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => runtime.Clock.SimulationTimeSeconds),
            new LiveSocialSessionBindings(
                runtime.CommunicationOwner.Chat,
                runtime.CommunicationOwner.TurbineChat,
                runtime.CommunicationOwner.Friends,
                runtime.CommunicationOwner.Squelch,
                (text, type) => runtime.CommunicationOwner.AddText(text, type),
                Fellowship: runtime.FellowshipOwner,
                Allegiance: runtime.AllegianceOwner,
                Trade: runtime.TradeOwner,
                House: runtime.HouseOwner,
                Contracts: runtime.ContractsOwner,
                PlayerGuid: () => runtime.PlayerIdentity.ServerGuid,
                OnLocalPlayerDeath:
                    runtime.CommunicationOwner.ReportLocalPlayerDeath),
            actions: runtime.ActionOwner);
    }
}
