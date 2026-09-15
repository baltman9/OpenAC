# Plugin API: chat, lifecycle, spells, storage and clipboard

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
says are connected, or `-1` before it has said.

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
