# Plugin API: chat, lifecycle, spells, storage, clipboard, objects, confirmations, session and loot

Everything here lives in `AcDream.Plugin.Abstractions` and has a default
implementation, so a plugin written against an older build still compiles and
a host that cannot provide something returns an inert value rather than
throwing. `docs/plugin-ui-markup.md` covers the panel markup separately.

## Chat

### Reading lines

`host.Automation.Chat` offers two ways to read the client's text.

```csharp
host.Automation.Chat.Received += message =>
{
    // message.Kind, message.LogTextType, message.CombatKind, message.Received
};
```

`Received` fires once for every line the client takes delivery of, in
arrival order, on the same thread that raises `IEvents.Tick`. Nothing is
dropped: a handler sees every line while it is subscribed.

`CaptureMessages(afterSequence)` is the older poll. It keeps the last 512
lines, so a plugin that polls less often than that loses the overflow. Use
`Received` for anything that must be complete, such as a log.

`PluginChatMessage` carries:

| Member | Meaning |
|---|---|
| `Sequence` | Host-local, monotonic. Pass the last one back to `CaptureMessages`. |
| `SenderObjectId`, `Sender`, `Text`, `ChannelName` | Who said what, and where. |
| `Kind` | `0` local speech, `1` ranged speech, `2` channel, `3` tell, `4` system, `5` popup, `6` emote, `7` soul emote, `8` combat, `100` status notice (see below). |
| `LogTextType` | The text class the client colours the line by. |
| `CombatKind` | `0` when the line is not a combat line, `1` ordinary outgoing, `2` incoming, `3` failure. |
| `Received` | When the client took delivery of the line. |

### Dropping lines

```csharp
IDisposable filter = host.Automation.Chat.RegisterFilter(
    message => message.Text.Contains("Your spell burned"));
```

A filter is consulted *before* the line is shown. Returning true drops it
outright: it reaches neither the transcript, the chat windows,
`CaptureMessages`, `Received`, nor the chat log file.

- Filters run in registration order and stop at the first rejection.
- A filter that throws suppresses nothing; the host records the fault.
- Dispose the handle to remove one filter. The host removes every filter a
  plugin installed when that plugin unloads, so a plugin cannot leave the
  client permanently muted.
- A filter registered before login still applies to the next session.
- A dropped incoming tell never becomes the client's reply/retell target:
  the same append point that filters gate is where that target is recorded,
  so a suppressed tell leaves no trace to `/r` back to.
- Filters are client-wide, not per-plugin. A line one plugin drops is
  invisible to the client and to every other plugin, including one polling
  `CaptureMessages`. Match narrowly — a filter written for one plugin's own
  noise can silently blind every other plugin and the transcript itself.
- Do not post a message from inside a filter callback. The filter runs
  during line delivery, and posting there re-enters the same delivery path.

Short status notices — the ones shown over the world rather than written
into the transcript, such as "You're too busy!" — pass through the same
filters with `Kind == PluginChatMessage.StatusTextKind` (100). Check the
kind if a filter should treat them differently from transcript lines.

### Writing lines

`PostSystemMessage(text)` is unchanged. `PostMessage(text, logTextType)`
writes in one of the client's own text classes, so a plugin's own output can
use the colour the class carries. `Submit(text)` still runs the full chat
pipeline, commands included.

## Lifecycle

```csharp
host.Events.LoginComplete += () => { /* the local player is in the world */ };
host.Events.Logoff        += () => { /* the session is ending */ };
host.Events.LocalPlayerDied += deathMessage => { /* the server's message */ };
```

- `LoginComplete` fires once each time the local player enters the world.
  A reconnect does not reload plugins, so it fires again on the same
  instance — treat it as "there is a fresh world to work with", not as
  one-time setup.
- `Logoff` fires when the in-world session ends, before teardown, so a
  handler can still read gameplay state.
- `LocalPlayerDied` carries the server's death message. It comes from the
  death notification itself, so a plugin does not have to match chat text.

`host.Automation.Character.ServerPopulation` reports the players the server
reported connected in its login-time world-name message, or `-1` before that
message has arrived. The server sends this once, at login: it is a snapshot,
not a live count, and it does not change again for the rest of the session
even as players come and go.

## Spells

`host.Automation.Spells` gains the whole table, not just what the character
knows:

```csharp
IReadOnlyList<PluginSpellInfo> everySpell = host.Automation.Spells.All;

if (host.Automation.Spells.TryFindByName("Heal Self", partialMatch: true, out PluginSpellInfo spell))
{
    // ...
}
```

`All` is built on first use and cached. `TryFindByName` ignores case: an
exact match wins, and `partialMatch: true` falls back to the first name that
contains the text.

## Storage

`host.Storage.RootPath` is the absolute directory the plugin's keys are
written beneath, or `null` when the storage is not backed by files. It is
for telling a user where their data went — keys still go through
`ReadText` / `WriteText` / `List` / `Delete`.

## Clipboard

```csharp
if (!host.Clipboard.TrySetText(report))
    host.Log.Warn("Nothing was copied.");
```

The graphical client copies through the same device its own text controls
use. A host without a window has no clipboard and returns false, as does a
failed attempt, so always handle false rather than assuming the copy
happened.

## Objects

```csharp
host.Events.ObjectChanged += change =>
{
    // change.ObjectId, change.Kind
};
host.Events.ContainerOpened += containerObjectId => { /* the corpse/chest/crate now open */ };
host.Events.ContainerClosed += containerObjectId => { /* it just closed */ };
```

`ObjectChanged` fires for every change to a world object the client is
tracking, on the same thread as `Tick`, in the host's own delivery order.
`PluginObjectChange.Kind` is one of:

| Kind | Meaning |
|---|---|
| `Created` | The object entered the client's object table for the first time. |
| `Updated` | A non-positional property or other field changed. |
| `IdentReceived` | The client took delivery of appraisal data for the object: the first reveal AND a later refresh of data already held (durability, stack count, and similar can change between requests). |
| `Moved` | The object's position changed enough to move it into a different cell. An in-cell position update that does not cross a cell boundary reports as `Updated` instead. |
| `Released` | The object left the client's object table (deleted, withdrawn, or an owned item leaving inventory). |

A bulk container reset carries no single object id and is not reported.

`IdentReceived` is reported from the appraisal response path; every other
kind is reported from the entity and inventory delta observers, which are
separate sources delivered in the same `Tick`-thread order but not
interleaved by a single shared sequence. An item held in inventory can
therefore raise two `ObjectChanged` calls for one underlying change (one
from the entity side, one from the inventory side); do not assume exactly
one call per change for such objects.

`ContainerOpened` / `ContainerClosed` track the client's one open external
container — a corpse, a chest, a housing storage crate. A vendor's shop pane
is a separate surface (not covered by this event) and does not raise it.
Replacing one open container with another before it closes still reports a
`ContainerClosed` for the one that was open.

## Confirmations

```csharp
host.Events.ConfirmationRequested += confirmation =>
{
    // confirmation.ContextId, confirmation.Type, confirmation.Text
    host.Automation.Dialogs.Answer(confirmation.ContextId, accept: true);
};
```

`ConfirmationRequested` fires whenever the server asks the client to show a
yes/no confirmation dialog. `Type` is the server's raw wire value; the only
one with fixed, known meaning is `5`, the crafting-percent confirmation
("this has a chance to fail, continue?"). Every other value is server-defined
and only distinguishable by `Text`.

`Dialogs.Answer(contextId, accept)` answers the dialog exactly as the
client's own Yes/No buttons would — it drives the same response builder, so
the server sees the identical reply. It returns `false` when there is no
outstanding dialog with that context id (already answered, timed out, or the
id does not match).

## Session

```csharp
if (!host.Automation.Login.Logout())
{
    // no in-world session to log out of
}
```

`Login.Logout()` runs the client's own graceful logout — the same route the
UI's logout control uses. It returns `false` when the surface is not
`IsAvailable` (no in-world session); it does not report the outcome of the
logout itself beyond having sent the request.

On the graphical host this returns to the character-select screen with the
process still running. On the headless host there is no character-select
screen to return to: `Logout()` tears down the whole session (the same
teardown a direct disconnect produces) rather than leaving it parked at a
selection step, so a headless plugin that calls it should expect the
session to end, not to see another character list.

## Loot

A classifier is registered under `<pluginId>/<classifierId>` — the id a
plugin passes to `Register` is scoped by its own manifest id before other
plugins ever see it. MossTank, for example, registers `"moss-tank"` and is
visible to the rest of the client as `"<its plugin id>/moss-tank"`; use the
scoped id, not the bare one, when calling `TryNeedsIdentification` or
`TryClassifyWithProfile` from a different plugin.

Beyond the live-profile `Classify` a registered `IPluginLootClassifier`
already provides, two more members exist:

```csharp
bool blocked = host.LootClassifiers.TryNeedsIdentification(classifierId, context);

bool found = host.LootClassifiers.TryClassifyWithProfile(
    classifierId, "Vendor", context, out PluginLootClassification classification);
```

`NeedsIdentification` (and its registry forwarder `TryNeedsIdentification`)
reports whether an item cannot yet be classified with confidence: it lacks
appraisal data and at least one active rule needs an appraised property to
evaluate. A plugin can use this to hold off deciding until an identify
request completes.

`TryClassifyWithProfile` — both the classifier's own member and the
registry's forwarder of the same name — classifies against a *named, stored*
profile instead of the classifier's live one, such as VTank's "vendor" and
"trader" list files. It returns `false` when the named profile does not
exist; a classifier with no notion of named profiles defaults to the same.

A classifier whose own vocabulary is richer than the public
`PluginLootAction` enum still reports a match rather than "no rule fired"
when a rule resolves to one of its private actions: it reports `NoLoot` as
the closest public equivalent, with `Matched` true and `RuleName` left
intact so a caller can still see which rule decided the item, even though
the action itself does not survive translation.

## Trade

```csharp
host.Automation.Trade.Opened += opened =>
{
    // opened.InitiatorObjectId (the local player), opened.PartnerObjectId
};
host.Automation.Trade.ItemAdded += added =>
{
    // added.ItemObjectId, added.Mine (true = staged on my side)
};
host.Automation.Trade.PartnerTradeAccepted += partnerId => { /* they hit accept */ };
host.Automation.Trade.Closed += () => { /* for any reason */ };

if (host.Automation.Trade.IsOpen)
{
    host.Automation.Trade.Add(itemObjectId);
    host.Automation.Trade.Accept();
}
```

`Trade` mirrors the retail-look secure-trade window one field at a time:
`IsOpen`, `PartnerObjectId`, `PartnerName`, `MyItems`, `PartnerItems`,
`MyAccepted`, `PartnerAccepted`. `Add`, `Accept`, `Decline`, `Reset`, and
`End` send the exact same wire commands the window's own buttons do, gated
the same way: each returns `PluginTradeCommandResult` with a
`PluginTradeCommandStatus` of `Unavailable` (no in-world session),
`NotOpen` (no trade window is open), `InvalidItem`, or `Sent`.

The event named `PartnerTradeAccepted` — not `PartnerAccepted` — carries the
partner's object id when they accept. It could not be named `PartnerAccepted`
because that name is already the live acceptance flag; C# does not allow a
property and an event to share a name on one interface.

There is no wire bit for "who asked for this trade first": `Opened.
InitiatorObjectId` is always the local player's own object id, and
`PartnerObjectId` is always the other side, regardless of who actually sent
the open request.

## Vendor

```csharp
host.Automation.Vendor.Opened += vendorId => { /* the shop pane just opened */ };
host.Automation.Vendor.TransactionCompleted += result =>
{
    // result.Kind (Buy/Sell), result.Success, result.Notice
};

foreach (PluginVendorItem item in host.Automation.Vendor.Items)
{
    // item.TemplateObjectId, item.Name, item.UnitPrice (retail buy-rate math), item.StackSize
}

host.Automation.Vendor.AddToBuyList(templateObjectId, count: 1);
host.Automation.Vendor.BuyAll();

host.Automation.Vendor.AddToSellList(ownedItemObjectId);
host.Automation.Vendor.SellAll();
```

`Vendor.Items` lists what the shop currently has for sale, priced with the
same retail buy-rate formula the vendor window shows (quantity 1). Staging
is entirely local to this surface — `AddToBuyList` / `AddToSellList` and
their `Remove*` / `Clear*` counterparts never touch the wire — until
`BuyAll` or `SellAll` commits the staged list through the same builder the
window's own Buy All / Sell All buttons use, and clears the list on send. A
vendor selling a full stack sells however many of that item the character
currently owns, matching the window's own default. `TryCaptureProperties`
reads a listed item's full appraisal-shaped property set (the same data
assessing it would show), by its `TemplateObjectId`.

`IsBusy` reports whether a buy/sell (or any other item transaction) is
already in flight — `BuyAll`/`SellAll` refuse with `Busy` rather than queue
behind it.

## Hotkeys

```csharp
IPluginHotkeyRegistration handle = host.Hotkeys.Register(
    "quick-heal",
    "Quick Heal",
    new PluginKeyChord(PluginKey.H, Ctrl: true),
    () => { /* Ctrl+H was pressed */ });

if (!handle.IsBound)
    host.Log.Warn("Quick Heal's default chord collided with a client binding.");
```

`Register` id is scoped by the plugin's own manifest id before the host ever
sees it, so two plugins registering `"quick-heal"` do not collide with each
other. A stored user override for the scoped id replaces the caller's
default chord at registration time; `handle.EffectiveChord` reports which
chord actually ended up bound. A chord that collides with an existing client
key binding is refused rather than silently stealing it: `handle.IsBound` is
`false` and the handler never fires. Disposing the handle revokes the
binding; the host also revokes every hotkey a plugin registered when that
plugin unloads.

A hotkey does not fire while the chat bar has keyboard focus unless Ctrl or
Alt is part of the chord — otherwise every letter typed into chat would also
be a candidate hotkey press.

The graphical host may receive a `Register` call before its keyboard and
input dispatcher exist yet (plugin loading is not strictly ordered against
input-dispatcher composition); the registration is queued and resolved the
moment the input layer comes up, so `IsBound` can flip from `false` to `true`
without the plugin doing anything further.

## Headless

A headless host implements this same contract, with a few members left as
placeholders rather than wired to real state:

- `Character`, `Spells`, and `Magic` are entirely no-op: every member of
  those three returns its inert default (`ServerPopulation` is always `-1`,
  `Spells.All` / `TryFindByName` are always empty / always miss, `Magic`
  never reports casting or accepts a cast request). A headless plugin that
  needs character or spell state reads it from the bot policy layer, not
  from this surface.
- `CaptureMessages` is unimplemented; use `Received` instead, which does
  work.
- `Objects`, `ContainerOpened`/`ContainerClosed`, `ConfirmationRequested`,
  and `Login.Logout` are real and wired to the same runtime state and
  session-command routes the graphical host uses -- these are not
  placeholders.
- `Dialogs.Answer` is real when the headless session was configured with a
  confirmation route; otherwise it returns `false` like any host with
  nothing bound.
- `Trade` and `Vendor` are real on both hosts: the same shared adapter binds
  over the same `GameRuntime`, so a headless bot sees identical state and
  sends the identical wire commands a graphical plugin would.
- `Hotkeys` is the inert no-op registry — there is no keyboard to bind to
  without a window. `Register` always returns a handle with `IsBound`
  `false` and the handler never fires.
