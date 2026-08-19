---
title: Binding controls, and rebinding them
slug: engine/binding-controls
kind: guide
area: Engine
summary: What a control path names, how a composite turns four keys into a vector, and the rebinding operation that listens for a key — built, tested, and not yet wired into any screen.
api: [T:Vixen.Input.InputControl, T:Vixen.Input.InputControlPath, T:Vixen.Input.InputDeviceKind, T:Vixen.Input.InputDeviceSet, T:Vixen.Input.InputBinding, T:Vixen.Input.InputBindingPart, T:Vixen.Input.InputCompositeKind, T:Vixen.Input.InputControlScheme, T:Vixen.Input.InputBindingConflict, T:Vixen.Input.InputRebindingOperation, T:Vixen.Input.InputRebindingState]
tags: [input, bindings, rebinding, gamepad, keyboard, control-scheme]
since: 0.1
status: preview
related: [engine/input-actions]
---

## What it is

The half of [input actions](input-actions.md) below the action: what a `path:` in a `.vxinput` names,
how several controls combine into one value, and how to let a player change one at run time.

| Type | What it is |
|---|---|
| `InputControl` | One physical control: a device kind, a code, and an index. A `readonly record struct` |
| `InputControlPath` | The text form both ways — `<Keyboard>/w` ⇄ `InputControl` |
| `InputBinding` | One `- path:` entry of an action: its parts, processors, interactions and groups |
| `InputBindingPart` | One named leg of a composite — `up`, `down`, `negative`, `modifier` |
| `InputCompositeKind` | Which composite, and therefore what the legs mean |
| `InputControlScheme` | A named set of device requirements a binding can belong to |
| `InputRebindingOperation` | Listen for a control, wait for it to settle, check conflicts, apply |
| `InputDeviceSet` | Where device state is submitted and read; the thing every path resolves against |

## What it is for

Three jobs that all need the same vocabulary: authoring a binding in an asset, showing the player
what a binding currently is, and letting them change it.

You do not need any of it to *read* an action — that is the action's own value, and going through a
control is how a game ends up with keyboard-only movement it did not intend.

## Using it

### A path is a device, a control, and sometimes an index

```
<Keyboard>/w        <Gamepad>/leftStick        <Gamepad>2/south        <Mouse>/delta
```

Parsing is case-insensitive and formatting is camelCase, and the two round-trip: what
`InputControlPath.Format` writes, `TryParse` reads back to the same `InputControl`.
`InputControlPath.Describe` is the human form, for a bindings screen.

Facts that catch people:

- **An index of 0 means "the pad this asset is paired to"** — `InputActions.GamepadSlot`. `<Gamepad>2`
  is the second pad absolutely, and ignores the pairing.
- **`<Gamepad>/leftStick` is the stick; the button under it is `leftStickPress`.** Binding a button
  action to the stick reads its magnitude instead, which looks like an over-sensitive button.
- **Numbers are not key codes.** `<Keyboard>/26` does not mean `w` and is refused. Bare digits are the
  number row: `<Keyboard>/1` is `Number1`.
- **Unity's spellings are accepted** where they differ — `buttonSouth` for `south`, `<Mouse>/left`
  for `primary`, and `pointer`, `joystick` and `touchscreen` as device aliases.

⚠ **Every X/Y axis pair sits at consecutive values in the control enums, and both the parser and the
formatter depend on it.** A control added between two halves of a pair breaks two-dimensional paths
rather than failing to compile; a test guards the ordering for exactly that reason.

### A composite is what turns four keys into a direction

`InputCompositeKind` decides what the parts mean:

| Kind | Parts | Value |
|---|---|---|
| `Vector2` | `up`, `down`, `left`, `right` | `(right − left, down − up)` |
| `Axis1D` | `positive`, `negative` | `positive − negative` |
| `ButtonWithModifiers` | `button`, plus any number of `modifier` | The button, gated on every modifier being held |
| `None` | — | The binding's own single path |

⚠ **Up is negative.** `<Keyboard>/w` bound to `up` yields `(0, -1)`, and so does the d-pad's up. It is
a screen convention, not a mistake — and it is why `ActionPlayerInput` negates the Y it copies into a
`MoveIntent`, which is [the one seam where the two conventions meet](players-and-possession.md).

Each part is clamped to `[0, 1]` on its own before the subtraction, so a trigger bound to `up` can
only ever contribute magnitude, never a reversed direction — and opposing parts held together cancel
to zero rather than fighting.

⚠ **A composite bound to an action of the wrong `controlType` is a load-time diagnostic and not a
failure**, so a `vector2` composite on a `button` action loads and reads a magnitude nobody expected.

### Processors run in the order they are written

`processors: stickDeadzone(min=0.125,max=0.925),invert(x=true)` — each transforms the value the
previous one produced. The dead zone is **radial on the magnitude and rescales rather than clips**, so
a stick at 0.1 reads zero and one at 1.0 still reads 1.0, with everything between stretched across the
remaining range. On a one-dimensional binding it degenerates to the axial form with the sign kept.

⚠ **An unknown processor or interaction name is dropped in silence** — no diagnostic, at load or at
run time. `stickDeadZone` (capital Z) is not `stickDeadzone`, and the difference between them is a
stick that never centres.

### Groups and control schemes

A binding's `groups:` names the schemes it belongs to; `InputControlScheme` names the devices a scheme
needs. `IsSupportedBy` answers whether the machine has them, and `Uses` whether a scheme involves a
device family.

⚠ **A binding with no groups belongs to every scheme, and no active scheme means every binding is
live.** That second one is the shipped state everywhere in this tree — nothing calls
`SetControlScheme`, and `AutoSwitchControlSchemes` is off by default — so keyboard and gamepad
bindings are all resolved at once and the most actuated one wins.

### Rebinding: listen, settle, check, apply

`InputRebindingOperation` is the whole flow. It is constructed against one binding — and one part of
it, for a composite — configured fluently, started, and then driven by `InputService`, which calls its
`Update` for you when you assign it to `InputService.Rebinding`.

`InputRebindingState` is the state machine: `Idle` → `Listening` → `Completed`, or `Blocked` when the
control is already bound elsewhere, or `Canceled`.

| Knob | Default | Why it is what it is |
|---|---|---|
| `Threshold` | 0.7 | Higher than the press point, so a brushed stick is not captured as a binding |
| `SettleTime` | 0.05 s | The control has to be seen on **two separate `Update` calls** this far apart — the first sighting only starts the clock |
| `DisableActionsWhileListening` | true | Otherwise the key being bound also fires the action it is being bound to |
| `AllowConflicts` | false | A conflict stops at `Blocked` and reports; it never silently overwrites |
| `ConflictScheme` | null | Narrows the conflict search to one scheme, since two schemes may use the same key legitimately |

⚠ **`Blocked` raises `Finished` and is not terminal.** The operation is waiting for the player to
answer "that key is already used for Fire — use it anyway?", so it stays listening-adjacent with the
actions still disabled. `ApplyAnyway()` takes it to `Completed`, `Cancel()` to `Canceled`, and
`InputService` clears its `Rebinding` only on those two — a screen that treats `Finished` as "done"
leaves the game with its input switched off.

⚠ **An override applies immediately, with no frame boundary**, because applying it re-resolves the
binding. `InputActions.SaveBindingOverrides` then writes only the differences, one line each
(`Player/Fire[0]=<Keyboard>/space`), and `LoadBindingOverrides` skips lines naming anything that no
longer exists and returns how many it applied.

⚠ **None of this has a caller yet.** No screen in the engine, the editor or the samples drives an
`InputRebindingOperation` — the `.vxinput` editor records keys with a listener of its own, keyboard
only, with no settle time and no conflict check. The operation and its tests are the specification for
the screen somebody writes; nothing about it is stubbed, and nothing about it is exercised in a real
frame either.

## Examples

**A rebinding, from the button the player clicked to the binding being live.**

```csharp compile
using Vixen.Input;

public static class Rebinds {
    /// <summary>Starts listening for a replacement for one binding of one action.</summary>
    public static InputRebindingOperation Begin(InputService input, InputActions actions, InputAction action) {
        // `.WithDevice(InputDeviceKind.Keyboard)` chains on here when the screen is per-device —
        // without it, a player configuring the keyboard who rests a thumb on a pad gets the pad.
        var operation = new InputRebindingOperation(actions, action, bindingIndex: 0)
            .Excluding(InputControl.Key(InputKey.Escape));

        operation.Finished += Done;

        // The service drives Update while this is set, and clears it when the operation ends.
        input.Rebinding = operation.Start();

        return operation;
    }

    static void Done(InputRebindingOperation operation) {
        if (operation.State != InputRebindingState.Blocked) {
            return;
        }

        // Blocked is a question, not a failure: the actions are still disabled until it is answered.
        foreach (var conflict in operation.Conflicts) {
            _ = conflict.Action.Path;
        }

        operation.ApplyAnyway();
    }
}
```

**Showing the player what a binding currently is.** `EffectivePath` is the override if there is one
and the authored path otherwise, so a bindings screen never has to ask which:

```csharp compile
using Vixen.Input;

public static class BindingLabels {
    public static string Describe(InputBinding binding) {
        if (binding.IsComposite) {
            // A composite has no single control; each leg has its own.
            return string.Join(" / ", binding.Parts.Select(part => InputControlPath.Describe(part.Control)));
        }

        return binding.IsResolved
            ? InputControlPath.Describe(binding.Control)
            : $"unbound ({binding.EffectivePath})";
    }
}
```

⚠ **`IsResolved` false is the misspelling detector**, and it is the only one there is: an unresolved
binding reads zero for ever and says nothing. A bindings screen that prints `EffectivePath` when it
cannot resolve one is how a typo in an asset gets found.

**Driving devices directly**, which is what a test does and what the platform bridge does for real.
`InputDeviceSet` takes submissions and answers `ReadValue`; `Actuated` is what the rebinding operation
watches.

```csharp compile
using Vixen.Input;

public static class Fakes {
    public static bool PressesW(InputDeviceSet devices) {
        devices.BeginFrame();
        devices.SubmitKey(InputKey.W, true);

        return devices.ReadValue(InputControl.Key(InputKey.W)) > 0f;
    }
}
```

⚠ **`Actuated` returns a list it reuses between calls** — copy anything you keep past the next call.
It also excludes mouse motion and position, so resting a hand on the mouse cannot end a rebind.

## See also

- [Input actions](input-actions.md) — the layer above: maps, actions, phases and the frame model.
- [Players and possession](players-and-possession.md) — where an action map becomes a pawn's intent,
  and the Y flip that the composite convention forces.
