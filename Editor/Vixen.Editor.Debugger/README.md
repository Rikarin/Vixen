# Vixen.Editor.Debugger

The stepping half of [doc 20's B4](../../docs/plan/20-editor-parity.md#b4--diagnostics): a frame
debugger over a captured command stream, a remote inspector that attaches to a running build, and a
device manager over whatever can be deployed to — plus [doc 16's](../../docs/plan/16-networking.md#diagnostics)
network panel, which is a reader for two diagnostics models `Vixen.Net` already had.

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
| `NetworkTable`, `NetworkReport` | The panel-side model over `BandwidthLedger` — internal: scalars in one, columns in five. |
| `NetworkTrend`, `NetworkLink` | The panel-side model of the link — internal: a ring of samples in one, the reading of a session in the other. |
| `FrameDebuggerView`, `RemoteInspectorView`, `DeviceManagerView`, `NetworkView` | The panels. Three of the four are `.vxml`; `RemoteInspectorView` is the one still in C#. |

### The three markup panels, and what each one had to answer

`NetworkView.vxml` was the first and is the *live* one — see [the network panel](#the-network-panel)
below. The other two arrived with doc 36 § F7's later waves and neither is a translation.

⚠ **`FrameDebuggerView.vxml` was blocked on `ref` and on nothing else.** Its tree is a `TreeView`
filled by adding `TreeNode`s, which has no tag to write, so the panel needs the object back —
`ref="@Tree"` is that. What did change is the state pane: `KeyValueList.Row`/`Trim` pooling is now a
keyed `@for` over an immutable snapshot, which is the same reuse expressed as data. And `Source` and
`Unavailable` are signal-backed, which **fixed a bug rather than moving one**: both are assigned by
`DiagnosticsModule` after `panel.Add<T>()` returns, and the C# computed `CaptureButton.Disabled` once
in `OnCreated` — so a host that could take a capture got a permanently greyed Capture button.

⚠ **`DeviceManagerView.vxml` split a `Restate` that was doing three unrelated things.** It refilled
the grid, set two `Disabled` flags and wrote the status line, and it ran on every selection change —
so clicking a device called `DataGrid.SetItems`, which clears the selection, and the row lost its
highlight in the same frame the buttons lit up for it. Measured before the port: `Select(0)` left
`Devices.Selection.Count == 0` with `manager.Selected` set; after, both agree. The grid now follows
`DeviceManager.Changed`, which a selection has never raised, and the buttons follow signals.

⚠ **`DeviceManager` is signal-backed *additively*, which is the shape a live model takes here.** Three
`CollectionSignal`s and a `Signal` behind the four properties, which are unchanged because a
`CollectionSignal<T>` already is an `IReadOnlyList<T>` — and `Changed` is still raised by every path
that raised it, because the grid's `SetItems` is a method no attribute can bind and every imperative
caller has to go on working.

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

## The network panel

[Doc 16](../../docs/plan/16-networking.md#diagnostics) asks for a bandwidth panel and a packet
inspector. Both already existed as *models* — `BandwidthLedger` answers "what is eating my thirty
kilobits" four ways and `SnapshotInspector` takes a packet apart without applying any of it — and
neither had a reader outside a console dump in `Samples/08`. `NetworkView.vxml` is that reader.

⚠ **Nothing on `Vixen.Net` was widened to build it, and that was the thing to check first.** Every
number the panel draws is a property those two types already expose. What the panel needed from
outside was not data but *pointers* at it: a ledger, a registry and the newest snapshot's bytes, all
three pulled through delegates the host sets — because a panel factory runs again on every reopen,
so anything pushed into a panel outlives the panel it was pushed into.

⚠ **`ReplicationServer` does not keep the last snapshot and the panel does not ask it to.** It writes
each connection's into a caller's buffer and forgets it; which connection's bytes are worth looking at
is a question only a game can answer. `GameServer.LastSnapshot` in `Samples/08` is a game holding on
to one for exactly this purpose.

⚠ **It is the first *live* panel in the editor written in markup, and the shape is deliberate.** A
snapshot panel takes a `Signal<T>` and is done; a live one has to answer what drives it and what stops
it doing that work sixty times a second. Here: `UiDocument.Ticked` drives it — time from outside, so a
test holds it still with `UiTest.Advance` — throttled to four hertz; a four-field fingerprint decides
whether a reading would differ before five dictionaries are walked and sorted; and there is no
revision counter, because the summary is a record of *scalars* whose signal genuinely refuses an equal
value and the five tables are objects that hold signals, so their `@for` keys survive for the life of
the panel and each row keeps or loses its region on its own value.

This is also the first `.vxml` in this project, which is why the `.csproj` gained the markup
generator: the `Vixen.Ui.targets` import that compiles it has been here since the sheet was moved out
of a `const string`, with only its `.vcss` half doing anything.

### The link graph

A third pane, over a third source: round trip and jitter over the last thirty seconds, drawn as a
strip of bars per measurement.

⚠ **The measurement already existed; the history did not.** `RoundTripEstimator` is an RFC 6298
filter and `NetworkMetrics` publishes both of its numbers as gauges — but a filter and a gauge are
both *now*, and a graph is a claim about the past. So the panel keeps a ring, `NetworkTrend`, in this
assembly. It is not beside the estimator because nothing else wants it: the meter's own remarks say
rates are the collector's job and are "deliberately not computed here", and a ring in `Vixen.Net`
would be a second in-process time series paid for by every dedicated server whether or not anybody is
looking. What `Vixen.Net` gained instead is a *measurement* — `TransportLoss`, four counters every
server benefits from and a meter publishes — and still no time series, which is the line the two
sides of this are drawn along.

⚠ **The ring is drawn as a ring, and that is what makes it cheap.** A scrolling chart shifts every
sample one place left on every reading, so all hundred and twenty `@for` keys change and all hundred
and twenty regions are rebuilt, four times a second. Here a sample stays in the slot it was written
to: a reading changes one slot's value and moves the `newest` class from one bar to the next, so
three elements are rebuilt. The scale is snapped to a 1–2–5 ladder for the same reason and for a
better one — a chart whose axis moves on every reading cannot be read at all.

⚠ **The two loss lanes are two, because the two directions are known by different evidence.**
`ITransport.Loss` is four cumulative totals from the session's own transport, and the panel
differences them into shares of one interval's traffic: **resent** is `Retransmitted` over `Sent`,
which is an *upper bound* on outbound loss — one lost datagram resent three times counts three, and a
lost acknowledgement resends one that arrived — and **lost inbound** is `Missing` over `Expected`,
which is loss that happened, because the far end's sequence numbers are consecutive and a gap that
has left the acknowledgement window is a datagram that never came. Naming both of them "loss" would
be the panel claiming the first one is the second.

⚠ **And a transport that counts nothing gets two lanes and a sentence.** `ITransport.Loss` is null on
one that cannot count datagrams — an in-process transport has none to count — and a pair of lanes
flat along the bottom would say the link is clean, which is the state this must never invent. Nothing
is wired for any of it: a `NetworkSession` holds the transport it runs on, so the panel asks it.

## The theme

**The sheet is `DebuggerTheme.vcss`, a file beside the loader**, embedded by the `**/*.vcss` glob in
`Vixen.Ui.targets` and read back by `DebuggerTheme.Css`. It was the smallest in the editor — most of what it used to say became a control's job when the state pane became a
`KeyValueList`. It was a `const string` until it was moved out byte for byte, and
`DebuggerTheme.Utilities` stays a constant because a build step generates it.

⚠ **This project imports two different `.targets` for two different sheets.**
`Vixen.Editor.Ui.Styling.targets` brings the *generated* utility sheet; `Vixen.Ui.targets` brings the
`**/*.vcss` glob that embeds the *hand-authored* one. Dropping either leaves a build that compiles.

Licensed under Apache-2.0.
