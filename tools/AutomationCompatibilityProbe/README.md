# Automation compatibility probe

This is an offline evidence tool. It compares the existing MossTank readers with
the Rynth meta and loot parsers and writers on caller-supplied files. It does not
start a client or write to any input file.

The build links parser source and references `RynthCore.LootSdk` from a separate
RynthSuite checkout. Set the source tree at build time when it is not at the
default sibling location:

```powershell
dotnet build tools/AutomationCompatibilityProbe/AutomationCompatibilityProbe.csproj `
  -p:RynthSuiteRoot=C:\path\to\RynthSuite
```

The selected source root is compiled into assembly metadata. The runtime report
records the checkout revision and dirty state, the current checkout's parser
source hashes, and the exact hashes of the executing probe, LootSdk, and MossTank
assemblies. The binary hashes identify the code that actually ran; source hashes
describe the checkout at report time and can differ if files changed after build.

Use repeatable `--met`, `--af`, and `--loot` arguments. `--output` writes JSON
evidence to a caller-selected path. `--verify-detectors` modifies isolated
temporary copies and records whether malformed inputs were rejected.

For an opt-in connected consumer, `--emit-utl` writes an unchanged round trip,
and `--emit-controlled-utl` with `--controlled-item-pattern` writes a temporary
profile with one clearly labelled item-name-pattern Keep rule prepended. The probe
parses the emitted file with MossTank and verifies that the requested insertion
is the only semantic change. These files are parser evidence until a separate
runtime trial consumes them.

RynthSuite is an external build dependency and is not copied into this tool.
Its MIT license is reproduced in `THIRD-PARTY-NOTICES.txt`.
