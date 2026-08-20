---
title: The network panel
slug: editor/network-panel
kind: guide
area: Editor
summary: Where a session's bandwidth is going and what is inside one snapshot, as an editor panel over the BandwidthLedger and SnapshotInspector a game already has — three delegates from the host and nothing new measured.
api: [T:Vixen.Editor.Debugger.NetworkView, T:Vixen.Net.Diagnostics.BandwidthLedger, T:Vixen.Net.Diagnostics.BandwidthEntry, T:Vixen.Net.Diagnostics.SnapshotInspector, T:Vixen.Net.Diagnostics.SnapshotContents, T:Vixen.Net.Diagnostics.SnapshotRecord]
tags: [editor, diagnostics, networking, bandwidth, vxml]
since: 0.2
status: preview
related: [editor/index, editor/writing-a-plugin, ui/markup-panels]
---

## What it is

**Tools ▸ Network.** Two panes over two things `Vixen.Net.Diagnostics` already computes:

* **Where the bandwidth went** — the five breakdowns
  [`BandwidthLedger`](/docs/api/vixen.net.diagnostics/bandwidthledger) keeps: by component type, by
  *field* of one, by remote call, by networked object, and by connection. Each row is a name, what it
  cost, a bar against the dearest row in its column, and the mean cost of one. Above them are the
  totals — the rate in kbit/s, what has been accounted for, how many records went as a difference
  rather than whole, and how many remote calls there were.
* **The last snapshot** — one packet run through
  [`SnapshotInspector`](/docs/api/vixen.net.diagnostics/snapshotinspector) and applied to nothing: the
  tick, the size, the removals, and one line per record saying which object, which component, whether
  it was a difference and from which baseline, and how many bits it took.

`NetworkView` is the panel. It measures nothing: every number on it is a property those two types
expose, and nothing in `Vixen.Net` was widened to build it.

## What it is for

"Thirty kilobits a second" is not actionable and "the rotation of a `NetworkTransform` is forty per
cent of it" is. The ledger has been able to answer that since it was written; what was missing was a
place to read the answer without stopping the game to print it to a console — which is what
`Samples/08`'s `Attribution` does, and what this panel is the same five calls of.

Two of its rows are worth opening it for on their own:

* **Sent as a difference.** A snapshot full of whole records is a snapshot whose baselines are being
  lost — an acknowledgement path that has stopped, or a capture ring too short for the round trip —
  and it costs several times what it should. The bar is the ratio; a whole record in the packet pane
  is coloured, so a packet full of them is visible without reading a number.
* **By field.** A field a game never changes still costs its "unchanged" bit in every record. The
  field column is what tells you a component is carrying a field this game does not use, which is a
  decision to make rather than a number to watch.

**What it is not for** is watching a number move. It reads at four hertz, deliberately: everything in
it is a running total over `Elapsed`, so a faster refresh shows the same numbers moving in their last
digit on a table that re-sorts under the cursor.

## Using it

### Pointing it at something

The editor runs no session of its own, so the panel starts empty and says so. Whatever *is* running
one hands the module three things — the same shape every diagnostics panel takes, and for the same
reason: a panel factory runs again on every reopen, so the module holds the values and the panel
pulls them.

```csharp no-compile="the host side; `diagnostics` is the live DiagnosticsModule and `server` the game's own object"
diagnostics.NetworkLedger = server.Ledger;        // BandwidthLedger, attached to the replication server
diagnostics.NetworkRegistry = server.Registry;    // names the component types inside a packet
diagnostics.NetworkSnapshot = server.LastBytes;   // the newest snapshot, as it went on the wire
```

All three are independent. A ledger with no registry shows the breakdowns and says the packet pane
has no capture; a registry with no ledger shows a decoded packet over an empty summary.

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

## See also

* [Panels in markup](../ui/markup-panels.md) — `@inherits`, `ref`, and the `@for` key rule in full
* [`BandwidthLedger`](/docs/api/vixen.net.diagnostics/bandwidthledger) — the five tables and why they are counted in bits
* [`SnapshotInspector`](/docs/api/vixen.net.diagnostics/snapshotinspector) — reading a packet without applying it
* [Writing a plugin](writing-a-plugin.md) — `AddPanel`, `AddCommand`, and what a module joins together
