# Vixen.Input

Stride's device abstraction underneath, Unity's action model on top — which is
[doc 11 § Input system](../../docs/plan/11-editor.md#input-system)'s decision, cashed in.

```csharp
var input = GameInput.Create();          // generated from Assets/GameInput.vxinput
input.Player.Enable();

// polled — what a simulation reads
var move = input.Player.Move.ReadValue<Vector2>();

// event-driven — what a UI listens to
input.Player.Fire.Performed += _ => Shoot();
```

Nobody asks whether `W` is down. That is what makes rebinding, control schemes, local multiplayer
and device hot-swap possible at all, and it is why the consumption model is Unity's rather than
Stride's `Input.IsKeyDown(Keys.W)`.

## What is here

| | |
|---|---|
| `InputControl`, `InputControlPath` | One control on one family of device, and its text form: `<Gamepad>/leftStick/x`. |
| `InputKey`, `MouseControl`, `GamepadControl`, `TouchControl` | The vocabulary a path names. |
| `InputDeviceSet` and the four devices | The frame's device state, fed by the host rather than polled. |
| `InputActions`, `InputActionMap`, `InputAction`, `InputBinding` | The action model, and the asset it comes from. |
| `IInputProcessor` + built-ins | Dead zone, invert, scale, clamp, normalize. |
| `IInputInteraction` + built-ins | Press, hold, tap, slow tap, multi-tap. |
| `InputControlScheme` | Which devices, and therefore which bindings. |
| `InputRebindingOperation` | "Press the key you want", with conflict detection. |
| `VxInputReader` + `InputActionAsset{Reader,Writer}` | The `.vxinput` format, read and written. |
| `InputService` | The devices, the assets, and the one call a frame that advances both. |

## The decisions

**This assembly does not reference `Vixen.Platform`, and that is load-bearing.** `Vixen.Input` is a
`Core/` assembly; `Vixen.Platform` sits above it and `CheckArchitecture` refuses the reference. The
rule exists for `Vixen.Ui`, which consumes this and must stay usable with no platform backend at all
([doc 02](../../docs/plan/02-repository-layout.md) § "Why `Vixen.Ui` does not depend on
`Vixen.Engine`"). So the device set is **fed**:

```csharp
devices.SubmitKey(InputKey.W, pressed: true);
devices.SubmitMouseMove(position, delta);
devices.SubmitGamepadAxis(deviceId, GamepadControl.LeftStickX, 0.8f);
```

and the twenty lines that turn a `PlatformEvent` into those calls live in the host —
`Vixen.App.PlatformInput`. The cost is that `InputKey` is a second copy of `Vixen.Platform.Key`;
`InputKeyTests` holds the two tables against each other member by member, so the cast between them
is checked rather than assumed. The benefit is that every test in this project runs with no platform,
no window and no event loop, and so does a determinism replay from a recorded input log.

**The frame is the unit.** `BeginFrame()` clears the motion deltas, the host submits what happened,
then the actions read. A value read twice in a frame gives the same answer twice — which is what a
fixed-step simulation needs and what an event stream on its own cannot promise. Positions are state
and survive the clear; deltas are per frame and do not, because a mouse delta that kept its last
value would spin the camera forever.

**One binding wins per frame.** A `Button` or `Value` action takes the binding furthest from rest,
not the sum. Without that, a stick and WASD bound to the same action reach a magnitude of two when
both are pushed, and a player who rests a hand on the pad while typing drifts.

**Up is negative** — in the `vector2` composite and in the sticks alike, because the engine's screen
convention has Y increasing downward ([`Conventions.md`](../Vixen.Core.Mathematics/Conventions.md)).
A composite that disagreed with the stick beside it in the same action would send the player the
other way depending on which device they picked up.

**Interactions are per binding, not per action.** An action bound to both a `tap` and a `hold` has to
be able to be half-way through each at once, which one state machine on the action cannot represent.
Processors are the opposite — pure, and shared.

**A binding that names a control this build does not have is not an error.** An asset shared between
a desktop and a phone names controls neither of them both has. It reads zero and says so through
`InputBinding.IsResolved`, so a settings screen can grey it out.

**Local multiplayer is `GamepadSlot`.** Two players load the same asset twice and set a different
slot on each; the bindings are identical and the device they read is not. A path may still name a pad
outright (`<Gamepad>2/south`) for the rare game that means it, but an authored binding should not,
because a `.vxinput` that named pad two would only ever work for player two.

**A disconnected pad keeps its slot** until `RemoveDisconnected()` is called, which the frame loop
never does. Renumbering is what a game must not do in the middle of a match because someone's
batteries ran out.

## The `.vxinput` format

```yaml
name: GameInput
maps:
  - name: Player
    actions:
      - name: Move
        type: value
        controlType: vector2
        bindings:
          - composite: vector2
            groups: Keyboard&Mouse
            parts:
              - part: up
                path: <Keyboard>/w
              - part: down
                path: <Keyboard>/s
              - part: left
                path: <Keyboard>/a
              - part: right
                path: <Keyboard>/d
          - path: <Gamepad>/leftStick
            groups: Gamepad
            processors: stickDeadzone(min=0.125,max=0.925)
controlSchemes:
  - name: Keyboard&Mouse
    devices:
      - device: keyboard
      - device: mouse
  - name: Gamepad
    devices:
      - device: gamepad
```

Ordinary YAML in [doc 08](../../docs/plan/08-asset-pipeline-and-addressables.md)'s dialect, so
`NativeFormatImporter` scans it for asset references like any other authored file.

**Composite parts are nested**, not flattened behind an `isPartOfComposite` flag as Unity's asset
does. The flat form makes every consumer reconstruct the tree and makes a hand-edited file that
reorders two lines mean something different — which a text asset in version control must not have.

**`Assets/` is read by a reader written against nothing but the BCL**, and that is not an accident.
The same code has to run inside the compiler, where `Vixen.Core.Yaml`'s generated type registry does
not exist, so `Vixen.Input.Generators` compiles `Assets/**` into itself by source link. The
alternative is two readers of one format that must agree, which fails as a binding that compiles into
an accessor the runtime then declines to resolve.

## Generated accessors

A `.vxinput` in a project that references `Vixen.Input` becomes a class:

```csharp
public sealed partial class GameInput {
    public const string Source = "...";                  // the document, embedded
    public static GameInput Create();                    // needs no file and no content pipeline
    public GameInput(InputActions actions);              // or wrap one loaded as content
    public PlayerActions Player { get; }
}
```

Renaming an action in the asset is then a compiler error at every use site rather than a string that
resolves to null on the frame the player presses it.

## Rebinding

```csharp
var rebind = new InputRebindingOperation(input.Actions, input.Player.Fire, bindingIndex: 0)
    .WithDevice(InputDeviceKind.Keyboard)
    .Excluding(InputControl.Key(InputKey.Escape))
    .Start();

rebind.Finished += operation => {
    if (operation.State == InputRebindingState.Blocked) {
        Ask($"That is already {operation.Conflicts[0].Action.Path}. Replace it?");
    }
};

services.Input.Rebinding = rebind;      // driven once a frame from there
```

**The actions are off while it listens** — otherwise the key chosen for "jump" also jumps. **A
conflict stops the operation rather than resolving it**: whether to refuse, swap or allow is a game's
decision, so it reports `Blocked` and waits for `ApplyAnyway()` or `Cancel()`.

`SaveBindingOverrides()` writes **only what the player changed**, one line each. A file holding every
binding would freeze their controls at the version of the game they first ran — an action added in a
patch would load with no binding, and an improved default would never reach anyone. A line naming
something that no longer exists is skipped rather than failing the load, for the same reason.

## Where it runs

With `Vixen.Engine`, in `SystemPhase.Input` — the phase that exists for this — through
`InputUpdateSystem`. Without one, `Vixen.App` reads it directly before `OnUpdate`. Either way, once a
frame and before anything that reacts to it.

## What is not here yet

The **action-map editor** and the **input debug panel** ([doc 11](../../docs/plan/11-editor.md) §
Input system) are editor UI. They were owed on the editor shell, and ⚠ **that reason has expired** —
`Vixen.Editor.Ui` is a shell with a command registry, docking and panel registration, and
`Vixen.Editor.App` is a running editor. So these are now two panels nobody has written rather than
two panels with nowhere to go. The model they edit and the live state they show are both public and
both tested.

Also owed: **sensors** (accelerometer, gyroscope), **pen/stylus**, **MIDI** and **custom HID**, which
[doc 11](../../docs/plan/11-editor.md) lists on the device side and which need a platform contract
before they can have an action-side one. `Vixen.Platform` reports none of the four today.
