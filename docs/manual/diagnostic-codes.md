# Diagnostic codes

Every diagnostic the tools emit in the MSBuild format carries a stable `VXnnnn` code. This file is
the register.

**Why this exists.** MSBuild recognises `file: error CODE: text` and nothing else — without a code a
line is prose in a build log rather than an entry in the IDE's error list. And once shipped, a code is
what somebody searches for two years later, long after the sentence beside it has been reworded. This
is the same argument the [log event register](log-events.md) makes, for the same reason.

## Rules

- **A code is permanent.** Once shipped it never changes meaning and is never reused.
- **The message text may change freely.** The code is the contract; the wording is not.
- **The severity may change.** A warning that turns out to be noise can become information without a
  new code.
- **Add the entry in the same commit as the diagnostic.** A register updated later is a register that
  is wrong.
- **Information-level output carries no code**, deliberately. It is not an error-list entry, and
  dressing it as one puts "this project has no addressable assets" in a CI failure summary.

## Ranges

| Range | Subsystem | Status |
|---|---|---|
| VX1000 – VX1999 | Asset import — importers, the pipeline, the asset database | **in use** |
| VX2000 – VX2999 | Content build — the plan, the packer, the catalog | **in use** |
| VX3000 – VX3999 | Shaders and Raven integration (Raven's own are `RVNxxxx`) | reserved |
| VX4000 – VX4999 | UI markup and styling — VXML, VCSS | reserved |
| VX8000 – VX8999 | Platform packaging — APK, iOS bundle, `wwwroot` | **in use** |
| VX9000 – VX9999 | The tools themselves — invocation, environment | **in use** |

## Allocated

| Code | Meaning | Emitted by |
|---|---|---|
| `VX1001` | An importer said something about an asset. | `vixen import`, `vixen content build` |
| `VX1002` | The asset database found something while scanning. | `vixen import` |
| `VX2001` | The build plan found something: an address nothing can resolve, a group nothing defines, a dependency that would not be packed. | `vixen content build` |
| `VX2002` | The content builder found something while packing. | `vixen content build` |
| `VX8001` | A pinned native binary has not been restored. | `MoltenVK.targets`, on an iOS build |
| `VX8002` | A restored native archive exports none of the entry points it was linked for. | `MoltenVK.targets`, on an iOS build |
| `VX9001` | The tool could not run: no project where one was expected, or an unusable argument. | every command |

Severity is not part of the code. `VX1001` is an error, a warning or information depending on what the
importer said, because the importer is what knows.
