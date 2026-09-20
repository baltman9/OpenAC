# Building and running

## Prerequisites

- **.NET 10 SDK** in the band pinned by `global.json` (currently `10.0.3xx`).
- **Your own Asheron's Call data files**: `client_portal.dat`,
  `client_cell_1.dat`, `client_highres.dat`, `client_local_English.dat`.
  OpenAC does not distribute them.
- **A server.** OpenAC connects to ACEmulator. The examples use a local server
  at `127.0.0.1:9000`.
- For the graphical client, a **Vulkan 1.3** capable GPU and driver. On
  Linux that means your distribution's Vulkan ICD for your GPU (for example
  `mesa-vulkan-drivers` on Ubuntu) and an X11 or Wayland desktop. On macOS it
  means MoltenVK and the Vulkan loader (`brew install molten-vk
  vulkan-loader`) when running from source; MoltenVK is reached through
  `VK_KHR_portability_enumeration`, and the loader needs
  `VK_ICD_FILENAMES=$(brew --prefix)/etc/vulkan/icd.d/MoltenVK_icd.json`.
  The Apple-silicon launcher distribution supplies its own validated loader,
  MoltenVK, and ICD manifest for launched clients.
  The Intel (`osx-x64`) distribution does too, from a different source; see
  [docs/ci-and-releases.md](ci-and-releases.md#macos-vulkan-runtime).

Windows and Linux (x64), plus macOS (arm64), are supported by the launcher,
graphical client, and bake step. The examples below use PowerShell; the bash
equivalents differ only in how variables are set.
Intel macOS (`osx-x64`) is also supported, best effort until August 2027; see
[docs/ci-and-releases.md](ci-and-releases.md#retiring-intel-macos-support).

## Build and test

```bash
dotnet restore AcDream.slnx
dotnet build AcDream.slnx -c Release
dotnet test AcDream.slnx -c Release --no-build --filter "Lane!=InstalledDat&Lane!=PreparedPackage&Lane!=Live&Lane!=Manual&Lane!=Timing&Lane!=Windows&Lane!=Linux&Lane!=MacOS&Lane!=Unix&Lane!=Vulkan&Lane!=SystemFont&Purpose!=Diagnostic&Status!=KnownFailure"
```

The filter is the portable gate described in `release-gate.md`. Tests behind a
`Lane` need a specific resource; run them when you have it, for example
`--filter "Lane=Vulkan"` on a machine with a GPU.

On Linux, `bash tools/build-linux.sh` runs the Release build with .NET and
NuGet state isolated under
`XDG_CACHE_HOME` (or `/tmp`). This is useful in containers and CI workers with
a read-only home directory. It redirects generated package locks to that cache
so local restores do not rewrite tracked package-lock files. Pass `--test` to
run the portable test filter afterward.

## Prepare the content package

Rendering and collision read a validated prepared package instead of decoding
world meshes on the frame path. Build it once per machine:

```powershell
dotnet run --project src/AcDream.Bake/AcDream.Bake.csproj -c Release -- `
  --dat-dir "C:\Games\Asheron's Call" `
  --out "C:\Games\Asheron's Call\acdream.pak"
```

A complete package from the standard data set is about 570 MiB. It is
machine-local; do not commit it. `ACDREAM_PAK_PATH` overrides the default
`<DAT directory>/acdream.pak`. The launcher runs this step for you.

## Run the graphical client

```powershell
$env:ACDREAM_DAT_DIR   = "C:\Games\Asheron's Call"
$env:ACDREAM_PAK_PATH  = "C:\Games\Asheron's Call\acdream.pak"
$env:ACDREAM_LIVE      = "1"
$env:ACDREAM_TEST_HOST = "127.0.0.1"
$env:ACDREAM_TEST_PORT = "9000"
$env:ACDREAM_TEST_USER = "youraccount"
$env:ACDREAM_TEST_PASS = "yourpassword"

dotnet run --project src/AcDream.App/AcDream.App.csproj -c Release
```

On Linux:

```bash
export ACDREAM_DAT_DIR="$HOME/ac" ACDREAM_PAK_PATH="$HOME/ac/acdream.pak"
export ACDREAM_LIVE=1 ACDREAM_TEST_HOST=127.0.0.1 ACDREAM_TEST_PORT=9000
export ACDREAM_TEST_USER=youraccount ACDREAM_TEST_PASS=yourpassword
dotnet run --project src/AcDream.App/AcDream.App.csproj -c Release
```

The DAT directory can instead be the first positional argument.

For a macOS source build, set the same game variables and run the managed
assembly with the Homebrew loader available:

```bash
export DYLD_LIBRARY_PATH="$(brew --prefix)/lib"
export VK_DRIVER_FILES="$(brew --prefix)/etc/vulkan/icd.d/MoltenVK_icd.json"
dotnet src/AcDream.App/bin/Release/net10.0/AcDream.App.dll "$HOME/ac"
```

The packaged Mac client is named `acdream-client` and loads its bundled graphics
libraries directly. It does not require those environment variables.

## Useful startup options

| Variable | Effect |
|---|---|
| `ACDREAM_DAT_DIR` | Data-file directory |
| `ACDREAM_PAK_PATH` | Prepared package path; defaults to `<DAT dir>/acdream.pak` |
| `ACDREAM_LIVE=1` | Connect to a server instead of loading offline |
| `ACDREAM_TEST_HOST` / `ACDREAM_TEST_PORT` | Server endpoint |
| `ACDREAM_TEST_USER` / `ACDREAM_TEST_PASS` | Graphical-client credentials |
| `ACDREAM_NO_AUDIO=1` | Skip audio initialization |
| `ACDREAM_UNCAPPED_RENDER=1` | Disable frame pacing (for measurement only) |
| `ACDREAM_DISPLAY_PROTOCOL=auto\|x11\|wayland` | Linux window backend selection |
| `ACDREAM_DEVTOOLS=1` | Enable the Vulkan validation and debug-utils layers |
| `ACDREAM_HEADLESS_CONSOLE=0\|1` | Headless interactive console; defaults to on when stdin is a terminal |
| `ACDREAM_HEADLESS_CONSOLE_STREAM=stderr\|stdout` | Which stream that console prints to; `stderr` by default |
| `ACDREAM_PLUGIN_TAGS=a,b` | Words this client wants to be found by; plugins on the clients running on this machine can see one another's tags and filter on them. The headless config's `pluginTags` is the same option |
| `ACDREAM_PLUGIN_SETTINGS_FILE=<path>` | Path to a JSON file holding the startup settings each plugin is given. The file is the same map the headless config names under `pluginSettings`, so a plugin reads the same settings whichever client is running. A named file that is missing, unreadable or the wrong shape stops startup with the reason; unset means no settings |

A few other `ACDREAM_*` variables switch original-client behaviors that are
on by default (`ACDREAM_RETAIL_CHASE`, `ACDREAM_CAMERA_COLLIDE`,
`ACDREAM_CAMERA_ALIGN_SLOPE`, `ACDREAM_RETAIL_CLOSE_DEGRADES`; set `=0` to
disable one for comparison). The rest are diagnostic probes, off by
default and documented beside their read sites in `src/`.

## Run a headless session

`AcDream.Headless` loads no window, GPU, or audio assembly. Create `bot.json`:

```json
{
  "version": 1,
  "process": {
    "content": {
      "datDirectory": "/opt/ac",
      "preparedAssetPath": "/opt/ac/acdream.pak"
    }
  },
  "sessions": [
    {
      "id": "bot-1",
      "endpoint": { "host": "127.0.0.1", "port": 9000 },
      "account": "youraccount",
      "character": { "index": 0 },
      "policy": { "id": "idle" },
      "credential": {
        "provider": "environment",
        "reference": "ACDREAM_BOT_PASSWORD"
      }
    }
  ]
}
```

Then:

```bash
export ACDREAM_BOT_PASSWORD='yourpassword'
dotnet run --project src/AcDream.Headless/AcDream.Headless.csproj -c Release -- validate --config bot.json
dotnet run --project src/AcDream.Headless/AcDream.Headless.csproj -c Release -- run --config bot.json
```

Optional per-session fields: `plugins` (which plugin ids to load),
`pluginSettings`, `loginCommands` / `loginCommandDelayMs`, `statusFile`,
`characterOptions`, and `pluginTags` — an array of words this session
wants to be found by, the same option `ACDREAM_PLUGIN_TAGS` gives the
graphical client. Plugins on the clients running on one machine can see one
another's tags and filter on them, so a bot that should look like part of a
group carries the group's word:

```json
"pluginTags": ["tank", "group-a"]
```

`pluginSettings` is the startup settings each plugin is given, one object of
settings per plugin id. A plugin reads only its own, through
`IPluginHost.SessionSettings`:

```json
"pluginSettings": {
  "acdream.example": { "startMacro": "true", "profile": "tank" }
}
```

The graphical client takes the same map from a file of its own:
`ACDREAM_PLUGIN_SETTINGS_FILE=<path>`, where the whole file is that map and
nothing else:

```json
{
  "acdream.example": { "startMacro": "true", "profile": "tank" }
}
```

Both clients read it the same way and refuse the same mistakes: a plugin id
with nothing behind it, or a setting with no value, stops startup and says
which one. A file the graphical client was told to read and could not is a
startup error too, never a quiet run with no settings — a plugin that decides
what to do on login from a setting would otherwise behave differently under a
window than it does without one.

### The same document on the graphical client

The graphical client reads the same session-config document:

```bash
dotnet run --project src/AcDream.App/AcDream.App.csproj -c Release -- --session-config bot.json
```

It requires exactly one session in the document, and it honours every
per-session field that decides what a plugin sees: `character`, `plugins`,
`pluginTags`, `pluginSettings`, `loginCommands`, `loginCommandDelayMs` and
`statusFile`. Two fields only the windowless client acts on are accepted and
ignored here rather than refused, so one document still starts either client:
`characterOptions` (the windowless client applies the declared options on
login; the graphical client leaves the character's own saved options alone)
and `policy` (which bot policy drives a windowless session; a graphical
session is driven by the player). The one field the graphical client refuses
is `"mode": "probe"`, because a probe never selects a character and there is
no windowed session to show.

Where a document and a startup option say the same thing, the document wins:
its `pluginTags` outranks `ACDREAM_PLUGIN_TAGS`, and its `pluginSettings`
outranks `ACDREAM_PLUGIN_SETTINGS_FILE`, which is left unread. A field the
document leaves out still falls back to the startup option.

For a single local session, `run` also accepts `--user` and `--password`. Add
more session entries for a multi-session process. Built-in policies:
`idle`, `lifecycle-smoke`, `observer-movement`, `portal-route-smoke`.

### The headless console

`run --console` turns a headless process into an interactive client: it prints
the chat box and reads typed lines. Chat appears with the same wording the
graphical client's chat window uses, behind a short tag standing in for the
colour that window would draw the line in, and honouring that window's
message-type filters:

```
[say] Bob says, "hi there"
[tell] Bob tells you, "meet me"
[fellowship] [Fellowship] Bob says, "group up"
[combat] A Drudge Slinker slashes you for 9 points of damage!
[client] navigation route loaded
-- entered world
```

Lines the console produces about the session itself start with `--`, so they
can never be read as chat. `[client]` marks text the client produced for
itself, which the graphical client shows in its status overlay.

The console is on by default when standard input is a terminal.
`ACDREAM_HEADLESS_CONSOLE=1` forces it on for a redirected stdin, and `=0`
turns it off; `--console` overrides both.

#### Typing

A typed line goes into the same chat entry the graphical client's chat box
types into, so it does exactly what that line does there:

```
hello                     say it out loud
/f group up               say it on the fellowship channel
@tell Bob, meet me        tell Bob
/r on my way              reply to whoever told you last
hello *wave*              say it and play the wave
/loc                      a client command
@who                      anything the client does not claim goes to the server
/vt start                 a verb a plugin registered
```

The entry remembers the last 100 lines, and it remembers where plain text is
aimed. Aim it with a channel verb or a tell and the next plain line follows:
after `@tell Bob, meet me` a bare `hello` is a tell to Bob, exactly as it is in
the chat box. A plugin that stages a line with `Chat.Compose` shows it as
`-- draft: ...`, and pressing Enter on an empty console line sends it.

#### Client commands that need a window

The client's own verbs work on either client. Two of them draw something, and
a client without a window answers in plain words rather than not knowing the
verb:

```
/nav grid                 Navigation: this client has nothing to draw the grid on
/nav route Bob            Navigation: this client has nothing to draw a route on
```

Everything else `/nav` and `/motor` do is identical with or without a window,
and so is `/status`, which answers the session's generation, where it is in its
life, and where the character stands:

```
/status
generation=3 state=InWorld position=cell=0xC6A9002B local=(84.31,112.07,42.00)
```

`/status` is a verb of the client's, not of the console's, so the graphical
chat box answers it with the same line. The verbs the client reserves on the
command registry -- `nav`, `motor` and `status` -- are not available to a
plugin.

#### The console's own verbs

Three verbs belong to the console rather than to the client. Each is offered
to the session's command registry first, so a plugin that registers the same
verb keeps it:

| Verb | What it does |
|---|---|
| `/quit` | Ends every session in the process, gracefully. `@quit` still goes to the server. |
| `/session <id>` | Chooses which session an unaddressed line goes to. With no id it says which one that is. |
| `/sessions` | Lists the sessions this process is running and marks the one being talked to. |

#### More than one session

A process running several sessions gives them one console between them. Every
printed line names the session it came from, and a line can be addressed to
one session without changing which one the next line goes to:

```
-- [alpha] entered world
-- [beta] entered world
[alpha] [say] Bob says, "hi there"
@beta /loc                       one line to beta
/session beta                    every following line to beta
-- [alpha] now talking to beta
/sessions
-- [alpha] session alpha
-- [beta] session beta (talking to this one)
```

An at sign is only read as an address when it names a session this process is
really running, so the server verbs that start with one -- `@tell`, `@who` --
are left alone.

**Two streams.** The machine-readable JSON diagnostic lines keep
standard output, unchanged, so a script can parse them while a person watches
the console. The console itself prints to standard error. Send it to
standard output instead with `--console-stream stdout`, or with
`ACDREAM_HEADLESS_CONSOLE_STREAM=stdout`; `--console-stream stderr` restores
the default. The flag wins over the variable.

## Run the launcher from source

```bash
dotnet run --project src/AcDream.Launcher/AcDream.Launcher.csproj -c Release
```

The launcher installs released builds from the GitHub Releases feed. A
source-built launcher will offer to update itself to the latest release; that
is expected.

## Shaders

GLSL sources live in `src/AcDream.App/Rendering/Shaders/`; the committed
SPIR-V beside them is what the client loads. After editing a shader run
`tools/compile-shaders.ps1`, which uses `glslc` from a Vulkan SDK if present
and otherwise the bundled `tools/ShaderCompiler`.
