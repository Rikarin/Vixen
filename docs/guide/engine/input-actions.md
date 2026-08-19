---
title: Input actions
slug: engine/input-actions
kind: guide
area: Engine
summary: Naming what the player is doing rather than which key they pressed — a .vxinput asset of maps and actions, the one phase it is updated in, and the frame model every polled flag depends on.
api: [T:Vixen.Input.InputService, T:Vixen.Input.InputActions, T:Vixen.Input.InputActionMap, T:Vixen.Input.InputAction, T:Vixen.Input.InputActionContext, T:Vixen.Input.InputActionPhase, T:Vixen.Input.InputActionType, T:Vixen.Input.InputControlType, T:Vixen.Input.InputSettings, T:Vixen.Engine.Input.InputUpdateSystem]
tags: [input, actions, keyboard, gamepad, mouse, bindings]
since: 0.1
status: preview
related: [engine/binding-controls, engine/players-and-possession, engine/character-movement]
---

## What it is

An action is a name for something the player is doing — `Move`, `Jump`, `Fire` — and a set of
bindings that can produce it. They live in a `.vxinput` asset as maps of actions, and at run time as
four objects:

| Type | What it is |
|---|---|
| `InputService` | The one thing a frame updates. Owns the devices and the loaded assets |
| `InputActions` | One loaded asset: its maps, its control schemes, its binding overrides |
| `InputActionMap` | A named group that is enabled or disabled as a unit — `Player`, `Menu`, `Vehicle` |
| `InputAction` | The name itself: a phase, a value, three events, and the polled `Was…ThisFrame` flags |

`Vixen.Input` deliberately references nothing platform-shaped. It is handed device state by the host
and asked for values; it never polls an OS, which is what makes a whole game's input testable by
submitting keystrokes to an `InputDeviceSet`.

## What it is for

Anything a player does on purpose, when you want it under a name rather than under a key code:
rebinding, gamepad and keyboard from one code path, a `Menu` map that switches off the `Player` map,
and analogue values that arrive already dead-zoned.

You do not want it for text entry or for the editor's own UI — `Vixen.Ui` has its own keyboard layer
and uses only this package's `InputKey` enum, not its actions. You also do not want it for something
that is not a player decision: a replay, an AI, or a network peer produces intent directly, which is
why [players and possession](players-and-possession.md) puts an interface between the two.

## Using it

### The frame model, which every polled flag depends on

Three things happen in order, and only the middle one is yours:

1. `InputService.BeginFrame()` — clears the *deltas* (mouse and touch motion), keeps the positions.
   `VixenApplication` calls it before draining the platform's event queue.
2. The host submits events into `InputDeviceSet` as they arrive. Two mouse moves in one frame **add**;
   they do not replace.
3. `InputService.Update(time)` — resolves every enabled action. With an engine this is
   `InputUpdateSystem`, in `SystemPhase.Input`; without one, the application calls it inline.

⚠ **`WasPressedThisFrame` and its siblings are cleared at the top of `Update`, not at the end of the
frame.** They are true from that call until the next one. A system that reads them *before*
`SystemPhase.Input` sees last frame's answer, and a system that reads them in a fixed step sees the
same press once per sub-step. Put anything that polls after the update:

```csharp no-compile="the two attributes, out of the system that carries them"
[UpdateInGroup(SystemPhase.Input)]
[UpdateAfter(typeof(InputUpdateSystem))]
```

`Samples/13-ThirdPersonShooter`'s `MouseCaptureSystem` is exactly that shape, and it is the one to
copy.

### The asset

A narrow YAML subset — block mappings, block sequences, scalars, `#` comments. **Anchors, aliases,
tags, flow collections and tabs are refused** with a parse error naming the line, because a dialect
that quietly accepts half of YAML is one nobody can predict.

```yaml
name: SampleInput
version: 1

maps:
  - name: Player
    actions:
      - name: Move
        type: value
        controlType: vector2
        bindings:
          - composite: vector2
            name: WASD
            groups: Keyboard&Mouse
            parts:
              - part: up
                path: <Keyboard>/w
              # down, left, right…
          - path: <Gamepad>/leftStick
            groups: Gamepad
            processors: stickDeadzone(min=0.125,max=0.925)
```

`Core/Vixen.Input.Tests/Assets/SampleInput.vxinput` is that file in full, and
`Samples/13-ThirdPersonShooter/Assets/Input/GameInput.vxinput` is a game's.

⚠ **A misspelled control path is not an error. It is an action that reads zero for ever.** The reader
checks the document's shape, not the meaning of a `path:` — an unresolved binding simply never
produces a value, and unknown processor and interaction names are dropped at construction with no
diagnostic at all. When an action does nothing, suspect the spelling before the wiring:
`<Gamepad>/leftStick` is the stick and `<Gamepad>/leftStickPress` is the button under it;
`<Keyboard>/26` is not `w`.

### Loading one

Two routes exist, and the difference between them is whether a renamed action is a compiler error:

- **`InputActions.Load(text, name)`**, then `Enable()`, then `InputService.Add`. Lookups are strings
  and a typo is a `KeyNotFoundException` at run time. This is what `Samples/13` does.
- **The generator.** `Vixen.Input.Generators` turns a `.vxinput` in `AdditionalFiles` into a partial
  class with a property per map and per action, so `Input.Player.Move` binds at compile time. The
  targets file globs `.vxinput` automatically for projects that consume the package.

⚠ **Nothing in this repository outside `Vixen.Input.Tests` uses the generated accessor**, including
the sample whose asset says it will. Both routes work; only the first is demonstrated, so treat the
sample as a description of the string route and not as advice.

### Types, phases, and the value that comes out

`InputActionType` picks the default interaction — what "the action happened" means:

| `type:` | Fires |
|---|---|
| `button` | Started **and** Performed on the frame the magnitude crosses the press point; Canceled on release |
| `value` | Started+Performed the first frame the value leaves exactly zero, Performed on every change after, Canceled at exactly zero again |
| `passThrough` | Only on change, with no press point anywhere in it |

⚠ **`value` uses "not zero", not the press point.** A stick nudged to 0.05 has already performed a
`value` action and has not touched a `button` one.

`InputActionPhase` is `Disabled`, `Waiting`, `Started`, `Performed`, `Canceled` — and:

⚠ **`InputAction.Phase` is never `Canceled`.** A cancel returns the action to `Waiting`; the
`Canceled` member exists as the `Phase` field of the `InputActionContext` handed to the event. Poll
`WasCanceledThisFrame`, or subscribe. Testing `Phase == InputActionPhase.Canceled` is a condition that
cannot be true, and it fails silently.

`InputControlType` — `Button`, `Axis`, `Vector2` — decides how many dimensions the value has, and it
is the *action's* property, not the binding's. An `axis` action bound to a stick gets `|X|` as its
magnitude with the Y still sitting in `Value.Y`, unread.

⚠ **The polled flags use one global press point and the events use the binding's.**
`IsPressed`, `WasPressedThisFrame` and `ReadValue<bool>()` all compare against
`InputSettings.DefaultPressPoint` (0.5). A binding written `press(pressPoint=0.2)` raises `Performed`
at 0.2 while `IsPressed` is still false — the event and the poll genuinely disagree, on purpose, and
mixing them in one feature is how a trigger ends up firing twice.

### ⚠ The most actuated binding wins, and nothing is summed

An action with a keyboard composite and a stick takes whichever reads highest this frame;
`ActiveBinding` is the one it took — useful for showing the right glyph. Two bindings held at once do
not add up, and a release while another binding is still down does **not** cancel: the action stays
`Performed` for as long as anything holds it.

### Gamepads, slots and disconnection

`<Gamepad>/south` means *the pad this asset is paired to* — `InputActions.GamepadSlot`, which is how
one asset per player works. An explicitly indexed path (`<Gamepad>2/south`) names the second pad and
ignores the pairing.

⚠ **A disconnected pad keeps its slot until `InputDeviceSet.RemoveDisconnected()` is called**, so
player two's controller coming back finds their slot where they left it. Disconnection also clears the
held state, which is what stops a dying pad leaving a character sprinting for ever.

### What is built here and not yet reached

Honest, because all of it looks wired from the outside:

- **`InputRebindingOperation`** — press-a-key rebinding with settle time, conflict detection and
  device filtering — is complete and has no caller outside its own tests. The `.vxinput` editor
  records keys with a listener of its own, keyboard only.
- **`SaveBindingOverrides` / `LoadBindingOverrides`** produce and consume a small line-based format
  (`Player/Fire[0]=<Keyboard>/space`) and nothing in the engine persists it, so a game that wants
  rebinding to survive a restart writes that part itself. Loading skips lines naming actions that no
  longer exist and returns the count applied.
- **Control schemes** are parsed and never activated: every shipped path runs with
  `ActiveControlScheme` null, which means every binding is live at once. `AutoSwitchControlSchemes` is
  off by default, and switching resets every action rather than merely re-filtering them.

## Examples

**Loading an asset and reading it**, which is the whole of what a game does with this. The action
lookup is by `Map/Action`; a bare name is searched across every map.

```csharp compile
using Vixen.Core.Mathematics;
using Vixen.Input;

public static class PlayerInput {
    public static InputActions Load(InputService input, string document) {
        var actions = InputActions.Load(document, "GameInput");

        // Enable before Add, or the first frame reads a set of disabled actions.
        actions.Enable();
        input.Add(actions);

        return actions;
    }

    /// <summary>Screen conventions: up is negative Y, which a movement intent has to flip.</summary>
    public static Vector2 Move(InputActions actions) => actions.FindAction("Player/Move").ReadVector2();

    public static bool Jumped(InputActions actions) =>
        actions.TryFindAction("Player/Jump", out var jump) && jump.WasPressedThisFrame;
}
```

⚠ **`FindAction` throws `KeyNotFoundException` and `TryFindAction` does not.** Which one to use is a
statement about the asset: an action the game cannot run without should throw at load, where the
message names the asset, rather than read zero on the frame the player presses it.

**Subscribing instead of polling.** The context carries the value that caused the event, so a handler
never has to go back and read the action — which matters because the events fire *during*
`InputService.Update`, before anything else in the phase has run.

```csharp compile
using System;
using Vixen.Input;

public static class Firing {
    public static IDisposable OnFire(InputAction fire, Action<float> shoot) {
        void Handler(InputActionContext context) => shoot(context.Magnitude);

        fire.Performed += Handler;

        return new Unsubscribe(() => fire.Performed -= Handler);
    }

    sealed class Unsubscribe(Action stop) : IDisposable {
        public void Dispose() => stop();
    }
}
```

**Switching maps rather than filtering keys.** A pause menu enables `Menu` and disables `Player`;
nothing else has to know:

```csharp compile
using Vixen.Input;

public static class Menus {
    public static void Pause(InputActions actions, bool paused) {
        if (paused) {
            actions["Player"].Disable();
            actions["Menu"].Enable();
        } else {
            actions["Menu"].Disable();
            actions["Player"].Enable();
        }
    }
}
```

Disabling a map sets every action in it to `InputActionPhase.Disabled` and drops whatever was
half-finished, which is the point: a jump held across the pause does not fire when the game resumes.

## See also

- [Binding controls, and rebinding them](binding-controls.md) — the layer below: what a path names,
  what a composite does with its parts, and the rebinding operation nothing drives yet.
- [Players and possession](players-and-possession.md) — `ActionPlayerInput`, the adapter from an
  action map to a pawn's `MoveIntent`, and the Y flip between the two conventions.
- [Character movement](character-movement.md) — what that intent drives.
- `Core/Vixen.Input/README.md` — the binding resolver, the processors and the interaction set in
  detail.
