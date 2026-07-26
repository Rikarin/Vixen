# Log event ids

Every `[LoggerMessage]` in the engine carries a stable numeric `EventId`. This file is the register.

**Why this exists.** A number in a support ticket, a crash dump or a player's log is greppable and
survives the message text being reworded. Without a register, ids get picked at random, collide, and
stop meaning anything — which is the state most projects are in, because allocating them is a
five-minute job that is easy to skip until it is too late to fix.

## Rules

- **An id is permanent.** Once shipped it never changes meaning and is never reused, even if the log
  line it named is deleted. Retire the range entry instead, so an old log still decodes.
- **The message text may change freely.** The id is the contract; the wording is not.
- **The level may change.** A warning that turns out to be noise can become `Debug` without a new id.
- **Add the entry in the same commit as the log line.** A register updated later is a register that
  is wrong.
- **`0` means unassigned** and is what an un-annotated call site gets. Anything logging with event 0
  in a shipping build is a bug.

## Ranges

Allocated per subsystem, a thousand apiece, so a subsystem never has to come back for more and an id
identifies its origin on sight.

| Range | Subsystem | Status |
|---|---|---|
| 1 000 – 1 999 | `Vixen.Core.*` — services, assets of the foundation, allocators | reserved |
| 2 000 – 2 999 | `Vixen.Graphics`, backends | **in use** |
| 3 000 – 3 999 | `Vixen.Shaders`, Raven integration | reserved |
| 4 000 – 4 999 | `Vixen.Rendering`, `Vixen.Rendering.PostFx` | reserved |
| 5 000 – 5 999 | `Vixen.Ecs`, `Vixen.Engine` | reserved |
| 6 000 – 6 999 | `Vixen.Assets`, content pipeline | reserved |
| 7 000 – 7 999 | `Vixen.Ui.*` | reserved |
| 8 000 – 8 999 | `Vixen.Platform.*` | reserved |
| 9 000 – 9 999 | `Vixen.Physics`, `Vixen.Audio`, `Vixen.Animation`, `Vixen.Input` | reserved |
| 10 000 – 10 999 | `Vixen.Net.*` | reserved |
| 11 000 – 11 999 | `Vixen.Editor.*` | reserved |
| 12 000 – 12 999 | `Vixen.Raven` — the compiler's own diagnostics are `RVNxxxx`, not these | reserved |
| 13 000 – 13 999 | `Vixen.App` — the host and the app heads | **in use** |

## Allocated ids

### `Vixen.Graphics` and its backends

| Id | Level | Message | Since |
|---|---|---|---|
| 2001 | Warning | The Vulkan validation layers were asked for and are not installed | 0.1.0 |
| 2002 | Warning | The validation layer was found but would not load; the instance was created without it | 0.1.0 |

### `Vixen.App` — the host

| Id | Level | Message | Since |
|---|---|---|---|
| 13001 | Information | `Vixen {Variant} on {Platform}, {Workers} workers.` | 0.1.0 |
| 13002 | Warning | `No window: {Reason}` — the desktop platform was wanted and headless was used | 0.1.0 |
| 13003 | Warning | `LOOSE CONTENT — reading from {Path} instead of bundles.` (docs/plan/17 Q5b) | 0.1.0 |
| 13004 | Warning | `Unrecognised engine argument {Argument} — it was ignored.` | 0.1.0 |
| 13005 | Information | `Stopping after {Frames} frames.` | 0.1.0 |
| 13006 | Critical | `The frame loop threw and the application is stopping.` | 0.1.0 |

Every other range is still reserved rather than allocated: the subsystems that will log have not been
written, and the ranges exist so that when they are, nobody has to invent a numbering scheme under
deadline.

<!--
    Format, once entries start arriving:

    | Id | Level | Message | Since |
    |---|---|---|---|
    | 2001 | Warning | `Effect {EffectName} permutation {Key} fell back after {Ms} ms` | 0.1.0 |
-->
