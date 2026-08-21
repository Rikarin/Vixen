---
title: The network panel
slug: editor/network-panel
kind: guide
area: Editor
summary: Where a session's bandwidth is going, how the link is behaving over time, and what is inside one snapshot — an editor panel over the BandwidthLedger, SnapshotInspector, RoundTripEstimator and transport loss counters a game already has.
api: [T:Vixen.Editor.Debugger.NetworkView, T:Vixen.Net.Diagnostics.BandwidthLedger, T:Vixen.Net.Diagnostics.BandwidthEntry, T:Vixen.Net.Diagnostics.SnapshotInspector, T:Vixen.Net.Diagnostics.SnapshotContents, T:Vixen.Net.Diagnostics.SnapshotRecord]
tags: [editor, diagnostics, networking, bandwidth, latency, vxml]
since: 0.2
status: preview
related: [editor/index, editor/writing-a-plugin, ui/markup-panels, engine/measuring-loss]
---

## What it is

**Tools ▸ Network.** Three panes over three things the engine already computes:

* **Where the bandwidth went** — the five breakdowns
  [`BandwidthLedger`](/docs/api/vixen.net.diagnostics/bandwidthledger) keeps: by component type, by
  *field* of one, by remote call, by networked object, and by connection. Each row is a name, what it
  cost, a bar against the dearest row in its column, and the mean cost of one. Above them are the
  totals — the rate in kbit/s, what has been accounted for, how many records went as a difference
  rather than whole, and how many remote calls there were.
* **The link, over the last thirty seconds** — round trip and jitter as
  [`RoundTripEstimator`](/docs/api/vixen.net.time/roundtripestimator) smooths them, one strip of bars
  per measurement, sampled from every player in a
  [`NetworkSession`](/docs/api/vixen.net.sessions/networksession) — and, when the session's transport
  counts datagrams, what was resent and what was lost coming in. Above each strip is the newest
  reading and the scale it is drawn against; above them all is the worst round trip and the worst
  jitter anybody in the session has.
* **The last snapshot** — one packet run through
  [`SnapshotInspector`](/docs/api/vixen.net.diagnostics/snapshotinspector) and applied to nothing: the
  tick, the size, the removals, and one line per record saying which object, which component, whether
  it was a difference and from which baseline, and how many bits it took.

`NetworkView` is the panel. It measures nothing: every number on it is a property the ledger, the
inspector, the estimator or the transport already exposes. The one thing it *keeps* is history — a
ring of samples for the graph, because a filter and a running total are both "now" and a graph is a
claim about the past. That ring lives in the editor, for the reasons under
[Where the history lives](#where-the-history-lives).

## What it is for

"Thirty kilobits a second" is not actionable and "the rotation of a `NetworkTransform` is forty per
cent of it" is. The ledger has been able to answer that since it was written; what was missing was a
place to read the answer without stopping the game to print it to a console — which is what
`Samples/08`'s `Attribution` does, and what this panel is the same five calls of.

Three of its readings are worth opening it for on their own:

* **Sent as a difference.** A snapshot full of whole records is a snapshot whose baselines are being
  lost — an acknowledgement path that has stopped, or a capture ring too short for the round trip —
  and it costs several times what it should. The bar is the ratio; a whole record in the packet pane
  is coloured, so a packet full of them is visible without reading a number.
* **By field.** A field a game never changes still costs its "unchanged" bit in every record. The
  field column is what tells you a component is carrying a field this game does not use, which is a
  decision to make rather than a number to watch.
* **Jitter, next to round trip.** A steady 200 ms link is easy and a link swinging between 40 and
  90 ms is not, which is the argument `RoundTripEstimator` is written around — the interpolation
  buffer and the tick lead are sized from the *variance*. The jitter strip is where a link that has
  started swinging is visible before anybody has worked out why the game feels wrong.

**The tables are not for watching a number move**, and read at four hertz for that reason:
everything in them is a running total over `Elapsed`, so a faster refresh shows the same numbers
moving in their last digit on a table that re-sorts under the cursor. **The graph is the opposite** —
it is only ever about movement, and every one of those same readings puts a bar on it.

## Using it

### Pointing it at something

The editor runs no session of its own, so the panel starts empty and says so. Whatever *is* running
one hands the module what it has — the same shape every diagnostics panel takes, and for the same
reason: a panel factory runs again on every reopen, so the module holds the values and the panel
pulls them.

```csharp no-compile="the host side; `diagnostics` is the live DiagnosticsModule and `server` the game's own object"
diagnostics.NetworkLedger = server.Ledger;        // BandwidthLedger, attached to the replication server
diagnostics.NetworkRegistry = server.Registry;    // names the component types inside a packet
diagnostics.NetworkSnapshot = server.LastBytes;   // the newest snapshot, as it went on the wire
diagnostics.NetworkSession = server.Session;      // NetworkSession — round trip, jitter, and loss
```

The loss lanes take no fourth line: a session holds the transport it runs on, and a transport that
counts datagrams is asked. See [The two loss lanes](#the-two-loss-lanes).

All of them are independent. A ledger with no registry shows the breakdowns and says the packet pane
has no capture; a registry with no ledger shows a decoded packet over an empty summary; a *client*
has a session and no ledger at all — the ledger is attached to a replication server and a client is
not one — and gets the graph over an empty state.

⚠ **`ReplicationServer` does not keep the snapshot and this does not ask it to.** It writes each
connection's snapshot into a caller's buffer and forgets it, because which connection's bytes are
worth looking at is a question only the game can answer. `GameServer.LastSnapshot` in `Samples/08` is
a game holding on to one for exactly this purpose, and is the shape the third line above takes.

### Attaching a ledger in the first place

Nothing in the engine attaches one, because an always-on dictionary increment per record is the
game's decision and not the engine's:

```csharp no-compile="a fragment; `replication` and `router` are the game's own"
var ledger = new BandwidthLedger();

replication.Ledger = ledger;
router.Ledger = ledger;

// Once a tick, next to the capture — the totals are only a rate because something says how long.
ledger.Advance(delta);
```

### The three controls

| | |
|---|---|
| **Refresh** | Takes a reading now rather than waiting for the interval. |
| **Reset** | `BandwidthLedger.Reset` — so the next reading is of what happens *next*. A rate averaged across an hour of idling hides the thirty seconds that matter. |
| **Per object** | `BandwidthLedger.TrackObjects`. Off by default and it stays that way: the other four tables are bounded by how many component types and remote calls a game declares, and this one by how many objects exist — the number the game exists to make large. |

### Reading the graph

Each strip is **a ring of a hundred and twenty samples**, one per reading, which at four hertz is
thirty seconds. It does **not** scroll: a sample stays in the slot it was written to and the sweep —
the one coloured bar — is the newest. Once the ring is full the sweep wraps and starts writing over
the oldest, the way a monitor trace does. There is a reason beyond the look, under
[The ring is drawn as a ring](#the-ring-is-drawn-as-a-ring).

The number on the right of each lane's heading is the **scale**, snapped to a round 1–2–5 value above
the tallest bar in the ring rather than set to it exactly. A bar is a fraction of that scale, so two
bars in the same lane are comparable and a lane whose peak has just fallen out of the ring does not
silently re-draw everything at a different size.

⚠ **A lane with nothing in it is not drawn, and neither is a graph nothing has measured.** An
estimator with no samples reports a round trip of zero, and a flat line along the bottom is a picture
of a perfect link — the opposite of "nobody has measured this". So the graph says which of the four
is true: no session was supplied, none is running, nobody is connected, or nobody connected has had a
ping come back yet.

⚠ **The first two of those are a trap worth knowing about**, because a host assigns the delegates
*after* `Build` returns. "No session supplied" and "no session running" produce the same numbers —
nothing — so a panel that read its own `Session` property to tell them apart would have been written
once, against a property that was still null, and would never be told the host had arrived. Whether a
source was supplied is therefore part of the reading, not read off the panel.

### The two loss lanes

A session whose transport counts datagrams gets two more lanes, and they are two rather than one
because the two directions are known by different evidence — the whole of that argument is in
[measuring packet loss](../engine/measuring-loss.md), and the short form is:

| Lane | What it is | What it means |
|---|---|---|
| **resent** | `Retransmitted` over `Sent`, for the interval | An **upper bound** on outbound loss. One lost datagram resent three times counts three, and a lost *acknowledgement* resends one that arrived. |
| **lost inbound** | `Missing` over `Expected`, for the interval | Loss that **happened**: sequences the far end numbered that never reached this process, on every channel including the unreliable ones. |

Both are **shares of one interval's traffic** and neither is a running total. The transport publishes
four cumulative counters on purpose — a total that has already been divided cannot be re-aggregated
across a fleet, which is `NetworkMetrics`'s rule — so the division belongs to whoever has two
readings, and on this pane that is the ring. A lane that divided the *totals* would still be reading
five per cent long after the link went clean.

Give it a session on a transport that counts nothing — an in-process one, which is what a session
with no socket in it is — and there are two lanes and a line saying why. A pair of lanes flat along
the bottom would claim a clean link, and a transport that cannot count datagrams has not told anybody
it lost none.

### An empty column is parked, an absent ledger is not

A column with nothing in it is hidden rather than removed, because an empty column here is not a claim
about the game: the object table is empty because the box above is unticked, and the call table
because nothing called anything. An absent *ledger* is the opposite and is drawn as an empty state —
a table of zeroes would read as a game sending nothing, which is the bug somebody would have opened
the panel to find.

## Examples

`NetworkView.vxml` is worth reading as the live-panel counterpart to
[`StatisticsView`](../ui/markup-panels.md)'s snapshot one. Three things differ, and all three are
about a model that moves on its own:

**The clock is the document's.**

```csharp no-compile="Editor/Vixen.Editor.Debugger/NetworkView.vxml, the @code block"
partial void OnComposed() {
    clocked = Root.Document;
    clock = (_, now) => Advance(now);
    clocked.Ticked += clock;

    Take();
}
```

`UiDocument.Ticked` is time arriving from outside, which is what makes a live panel something a test
can hold still — `UiTest.Advance` is the whole of the harness. The document is held in a field rather
than reached for through `Root` when letting go, because `UiElement.Document` throws once the element
is removed and unmounting is exactly when that is true.

**A reading is only taken when one would differ.**

```csharp no-compile="the guard; `taken` is the previous fingerprint"
static (long Bits, long Count, TimeSpan Elapsed, bool Objects) Fingerprint(BandwidthLedger? ledger) =>
    ledger is null
        ? (-1, -1, TimeSpan.MinValue, false)
        : (ledger.TotalBits, ledger.TotalCount, ledger.Elapsed, ledger.TrackObjects);
```

Four field reads against five dictionary walks and five sorts. It is exact rather than approximate:
every table is a partition of the same records, so there is no way to move a bit from one component
to another without moving the total.

**Two kinds of `@for` key, in one file.**

```vxml no-compile="Editor/Vixen.Editor.Debugger/NetworkView.vxml"
@for (var table in Tables) {
    <network-table key="@table" class="@Parked(table)">
        <network-heading>@table.Heading</network-heading>

        @for (var entry in table.Entries) {
            <network-row key="@entry">
                <network-name>@table.Short(entry.Name)</network-name>
                <ProgressBar Value="@Share(entry, table)" />
            </network-row>
        }
    </network-table>
}
```

The outer loop keys on the **object**, because a `NetworkTable` holds a signal — five of them are made
once and never replaced, so those keys survive for the life of the panel and what changes underneath
them is `Entries`. The inner loop keys on the **value**, because a `BandwidthEntry` is immutable data:
change what a component cost and the key changes, the old region goes, and a new row is built with the
new number in it.

⚠ **And the consequence one step further.** A surviving row's bindings run once and then only when
something they *read* changes. `table.Short(name)` reads the namespace shared by the column, so that
store is a signal too — a plain field would leave a surviving row showing a name with the *previous*
reading's prefix cut off it, which is not a stale name but a wrong one.

There is no revision counter anywhere in the file, which is the thing this design exists to avoid: the
summary is a record of scalars whose signal genuinely refuses an equal value, so an idle server
produces no notification at all.

### Where the history lives

The ring is in the **editor**, beside the panel that draws it, and not in `Vixen.Net` beside the
estimator. Three reasons, in the order they decided it:

1. **Nothing else wants it.** `RoundTripEstimator` is a running filter and `NetworkMetrics` publishes
   its two numbers as gauges; both are *now*, on purpose. A ring beside the estimator would be paid
   for by every dedicated server whether or not an editor is attached.
2. **It would be arguing with the metrics pipeline.** `NetworkMetrics`'s own remarks say rates are
   the collector's job and are "deliberately not computed here", because a metric that has already
   been differenced cannot be re-aggregated across three servers. An in-process time series is
   exactly that differencing, done in the place that said it would not.
3. **A ring is not a measurement.** `Vixen.Net` gained a vocabulary for loss — `TransportLoss` and
   `ITransport.Loss`, which is a *measurement* every server benefits from and a meter publishes — and
   it still has no time series in it. The distinction is the one the first two points are about: what
   belongs down there is what is true whether or not anybody is looking.

The type is `NetworkTrend`, internal, in `NetworkReport.cs` with the rest of the panel's model.

### The ring is drawn as a ring

A scrolling chart shifts every sample one place left on every reading. Every `@for` key therefore
changes, so every region is rebuilt — a hundred and twenty elements, four times a second, for as long
as the panel is open.

A ring does not shift. A sample stays in the slot it was written to, so a reading changes **one**
slot's value and moves the sweep class from one bar to the next: three regions, not a hundred and
twenty. The scale is snapped to a 1–2–5 ladder to keep that true — a scale that followed the peak
exactly would change on nearly every reading, and every bar's height is a fraction of it.

That is asserted rather than asserted-to: the test compares element *instances* across a reading,
because instance identity is the only thing that tells a surviving region from a rebuilt one.

```csharp no-compile="Editor/Vixen.Editor.Debugger.Tests/NetworkViewTests.cs"
// Every bar except the one that was the sweep — it loses the class, so its value changes and
// its region goes with it.
for (var slot = 0; slot < before.Length - 1; slot++) {
    Assert.Same(before[slot], after[slot]);
}
```

⚠ **And the key rule read at the loop rather than at the row.** `Lanes` is two lanes or four,
depending on whether the session's transport counts datagrams — so the `@for`'s own *source* changes,
and a source read off a plain field is a loop that reconciles once and never again. It reads `Link`, which
is a signal, for that reason. The first two rounds of these tests did not catch that, because every
one of them held its source still for its whole length; a live panel never does, and three tests now
move it while the panel is open.

## See also

* [Panels in markup](../ui/markup-panels.md) — `@inherits`, `ref`, and the `@for` key rule in full
* [`BandwidthLedger`](/docs/api/vixen.net.diagnostics/bandwidthledger) — the five tables and why they are counted in bits
* [`SnapshotInspector`](/docs/api/vixen.net.diagnostics/snapshotinspector) — reading a packet without applying it
* [`RoundTripEstimator`](/docs/api/vixen.net.time/roundtripestimator) — the RFC 6298 filter behind both time lanes, and why the variance is the number that matters
* [Measuring packet loss](../engine/measuring-loss.md) — the four counters the loss lanes are differenced from, and what each direction can honestly claim
* [Writing a plugin](writing-a-plugin.md) — `AddPanel`, `AddCommand`, and what a module joins together
