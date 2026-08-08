# Vixen.Editor.Debugger

The stepping half of [doc 20's B4](../../docs/plan/20-editor-parity.md#b4--diagnostics): a frame
debugger over a captured command stream, a remote inspector that attaches to a running build, and a
device manager over whatever can be deployed to.

## What is here

| | |
|---|---|
| `CapturedCommand`, `CaptureCommandKind` | One RHI call, flat and backend-neutral. |
| `CaptureNode`, `FrameCapture` | The stream as a tree of passes and groups, and as a list to replay. |
| `DrawState` | Everything bound at a point in the stream, rebuilt by replaying the prefix. |
| `NullFrameCapture` | The adapter from `Vixen.Graphics.Null`'s recorder. The only file that knows a backend exists. |
| `InspectorProtocol` | Doc 13's remote-inspector wire format: a kind byte, then fields. |
| `RemoteInspectorClient` | The editor's half of the conversation, over any `ITransport`. |
| `DeviceManager`, `IDeviceProvider` | What a build can be deployed to. |
| `FrameDebuggerView`, `RemoteInspectorView`, `DeviceManagerView` | The panels. |

## Stepping

```csharp
var capture = NullFrameCapture.From(device.Recorder!, "editor frame");

var draw = capture.NextWork(0);             // draws and dispatches, not every command
var state = capture.StateAt(draw!.Value);   // pipeline, descriptor sets, buffers, viewport
```

⚠ **Stepping moves between draws, not between commands.** A frame is a few thousand calls and forty
of them per draw are binds; a step that advanced one command would take forty presses to reach the
next thing that put a pixel anywhere.

⚠ **State is replayed from the start rather than snapshotted per call.** A frame is a walk over an
array of structs, which is microseconds; a snapshot per draw would be a copy of the whole state
vector held for as long as the capture is open — megabytes of the editor's heap for a real frame.

⚠ **An unbalanced stream is tolerated rather than refused.** A capture taken from a frame that threw
halfway through has a pass that never ended, and that capture is exactly the one somebody needs to
look at.

### What a capture cannot give yet

Doc 13 wants stepping to draw N to **present what the frame had drawn by then**, which needs a
device that actually executed the calls. `Vixen.Graphics.Null` is the engine's only recording path
and it has the state, not the pixels — so the panel says so rather than showing an empty image
somebody would read as a black render target. A Vulkan command-stream hook arrives as a second
adapter beside `NullFrameCapture`, not as a change to the panel.

## The remote inspector

[Doc 13](../../docs/plan/13-diagnostics.md) calls this "how mobile and console debugging actually
happens". The protocol carries what that section asks for and no more: browse the live hierarchy,
read and **write** component values, live counters, and trigger a verb.

```csharp
var client = new RemoteInspectorClient(transport);

client.Attach();                                     // greets, then fetches the tree
client.Poll(delta);                                  // once a frame — nothing arrives outside this
client.SetValue(entity, "Transform.Position", "1 2 3");
```

⚠ **Nothing is delivered outside `Poll`.** That is `ITransport`'s own contract and this keeps it: the
entity tree is rebuilt on the frame thread, so the panel reading it never needs a lock.

⚠ **A version mismatch is a state, not an exception.** An editor attached to last week's build is
the ordinary case on a device; half-reading its messages would show an empty tree that looks exactly
like a build with no entities in it.

⚠ **The format is hand-written rather than JSON**, because the far end is a phone on a phone's
uplink. Every field is a length-prefixed string or a fixed-width little-endian number, which is a
reader in forty lines on both sides — and a truncated message is refused rather than read past,
because a length prefix taken on trust is an index off the end of a buffer in the tool somebody
attached *because* something was already going wrong.

### What is not here

Discovery and pairing. Which transport reaches which device is `Vixen.Net`'s question and the
editor's choice — a protocol that opened its own socket would be a second answer to it — and the
runtime half of the protocol is doc 13's and is not written. `Vixen.Editor.Debugger.Tests` contains
a `FakeBuild` written only against `InspectorProtocol`'s readers and writers, which is the shape a
player's implementation takes.

## The device manager

The list, the statuses, the selection and the hand-off to the remote inspector. What is *not* here
is anything that knows how to find an Android phone — that is `adb` — or a console, which is a
vendor SDK. Both are one `IDeviceProvider` each, and a panel listing the local machine and saying so
is a truer state than one that pretends to scan.

Deploy is here now that there is a build behind it, and it is a **request** rather than a call, for
exactly the reason attaching is: what "deploy" means differs per kind of device — this machine is a
publish and a launch, a phone is `adb install`, a console is the vendor's own tooling — and a
debugger assembly that picked one would be a panel that could only deploy to that one. It raises
`DeployRequested`; `Vixen.Editor.App` answers it with doc 20's B7 build.

⚠ **Which devices can be deployed to is asked rather than assumed.** `CanDeploy` returns a sentence
or null, and unset means *nothing* can be — a panel with no build settings behind it must not offer a
button that would silently do nothing. The kinds this editor cannot install to say which tool is
missing, which is the same rule the greyed menu lines follow: the tool that would find a device is
the tool that would install to it, so a phone nothing can discover is necessarily a phone nothing can
deploy to.

⚠ **`Deploying` and `Running` are states no provider can report**, so `DeviceManager.Mark` exists to
say them. Discovery answers "is it there"; whether a build is on its way to it is a fact about what
the editor is doing. Without it the two would be enum members no code could ever produce, and a row
would read Available while a publish was running.

## The theme

**The sheet is `DebuggerTheme.vcss`, a file beside the loader**, embedded by the `**/*.vcss` glob in
`Vixen.Ui.targets` and read back by `DebuggerTheme.Css`. At 70 lines it is the smallest in the
editor — most of what it used to say became a control's job when the state pane became a
`KeyValueList`. It was a `const string` until it was moved out byte for byte, and
`DebuggerTheme.Utilities` stays a constant because a build step generates it.

⚠ **This project imports two different `.targets` for two different sheets.**
`Vixen.Editor.Ui.Styling.targets` brings the *generated* utility sheet; `Vixen.Ui.targets` brings the
`**/*.vcss` glob that embeds the *hand-authored* one. Dropping either leaves a build that compiles.

Licensed under Apache-2.0.
