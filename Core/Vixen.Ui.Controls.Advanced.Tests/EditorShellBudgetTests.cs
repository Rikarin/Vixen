// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Vixen.EditorShell;
using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Doc 09 § Testing's Perf row, as a gate rather than as a number in a table.</summary>
/// <remarks>
///     <para>
///         <b>"Editor-shell benchmark is the gate: 5 panels + viewport + 500-node graph + a 10⁶-row
///         virtualised grid holds the budget."</b> The benchmark that measures it lives in
///         <c>Vixen.Benchmarks.Ui</c> and reports milliseconds; a benchmark cannot fail, so it is not
///         a gate. This is, and it holds the same scene to properties expressed as <i>work</i>.
///     </para>
///     <para>
///         ⚠ <b>Work and not wall-clock, and that is this repository's own rule rather than a
///         preference.</b> A millisecond budget calibrated on an idle machine is its single largest
///         flake source, and — worse here — it does not fail for the reason anybody cares about: a
///         frame that got slower because it realised a million rows and a frame that got slower
///         because the machine was busy print the same number. Elements realised, styles recomputed
///         and draw commands emitted are the same on every machine, and each of them names its
///         defect.
///     </para>
///     <para>
///         ⚠ <b>Every assertion here is two-sided.</b> A ceiling alone is met by a shell that built
///         nothing — an exception swallowed in a panel, a grid told about no items, a graph whose
///         refresh never ran — and "realised fewer than a thousand rows" is most convincingly
///         satisfied by realising none. So each bound has a floor under it, and the floor is what
///         says the fixture is a shell rather than an empty document.
///     </para>
/// </remarks>
public class EditorShellBudgetTests {
    /// <summary>The scene, built once for the whole class.</summary>
    /// <remarks>
    ///     ⚠ <b>Shared, because building it is a second of work</b> — a million items are copied into
    ///     the grid's list, which is inherent and is not what any of this measures. The tests below
    ///     read counters and drive frames; none of them mutates the tree in a way the next would see.
    /// </remarks>
    static readonly EditorShellScene.Scene Shell = EditorShellScene.Build();

    /// <summary>The shell is a shell: all four of the row's parts are there and populated.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted first, and it is the assertion that makes the other three mean anything.</b>
    ///     Every bound below is a ceiling on work, and a document that failed to build any of its
    ///     panels would pass all of them by doing nothing. This is what a benchmark reporting a very
    ///     fast frame could not tell you.
    /// </remarks>
    [Fact]
    public void The_scene_is_the_composition_the_row_names() {
        Assert.Equal(EditorShellScene.Panels, Shell.Docking.Panels.Count);
        Assert.Equal(EditorShellScene.Nodes, Shell.Canvas.Graph.Nodes.Count);
        Assert.Equal(EditorShellScene.Nodes - 1, Shell.Canvas.Graph.Wires.Count);
        Assert.Equal(EditorShellScene.Rows, Shell.Grid.Items.Count);

        // The viewport and the hierarchy are elements rather than counts, so the claim is that they
        // were built and laid out — a zero-sized panel is the shape a broken docking arrangement has.
        Assert.True(Shell.Viewport.Bounds.Width > 0f, "the viewport has no width, so the shell did not lay out");
        Assert.True(Shell.Hierarchy.Rows.Count > 0, "the hierarchy realised no rows");
        Assert.NotNull(Shell.Inspector);
    }

    /// <summary>A million rows realise a viewport's worth of elements and no more.</summary>
    /// <remarks>
    ///     ⚠ <b>The bound is stated against the item count rather than as a constant.</b> "Fewer than
    ///     four hundred" would be a number somebody has to re-derive when the fixture's height
    ///     changes; "fewer than a thousandth of the items" is the property — O(viewport), not
    ///     O(items) — and it stays true at every size the scene could be built at.
    /// </remarks>
    [Fact]
    public void A_million_rows_realise_a_viewport_of_elements() {
        var realised = Shell.Grid.Rows.Count;

        Assert.True(realised > 0, "the grid realised no rows at all, so nothing below is a bound");

        Assert.True(
            realised < EditorShellScene.Rows / 1000,
            $"the grid realised {realised} elements for {EditorShellScene.Rows} items, which is not O(viewport)"
        );
    }

    /// <summary>A settled shell does no work at all, and draws the same frame again.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both claims, and for a while only the first of them was true.</b> <c>Update</c>
    ///         returning <c>false</c> is the behaviour; <c>StylesResolved == 0</c> is the counter
    ///         reading, and it used to be red against a document doing nothing whatever — the early
    ///         return in <c>UiDocument.Update</c> cleared <c>StylesApplied</c> and left
    ///         <c>StylesResolved</c> holding whatever the last <i>real</i> pass resolved, so a settled
    ///         shell reported a few hundred elements cascaded for ever. Fixed under #596, and asserted
    ///         here now that the reading and the behaviour agree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A count of work and not a millisecond, for the reason this whole file exists.</b>
    ///         "Did the frame do anything" is the same answer on an idle laptop and a loaded CI
    ///         runner; "was the frame under 2 ms" is not.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_settled_shell_does_nothing_and_draws_the_same_frame() {
        // Frames until the arrangement stops settling, with a ceiling that is a hang check rather
        // than a budget: a shell that has not settled in ten frames is not slow, it is oscillating.
        var settled = 0;

        for (var i = 0; i < 10 && Shell.Document.Update(); i++) {
            Shell.Document.Draw();
            settled = i + 1;
        }

        Assert.True(settled < 10, "the shell never stopped dirtying itself");

        Shell.Document.Draw();
        var commands = Shell.Document.Drawing.Commands.Count;

        Assert.True(commands > 100, $"the shell emitted {commands} draw commands, which is not a populated shell");

        Assert.False(Shell.Document.Update(), "a settled shell reported work to do");
        Assert.Equal(0, Shell.Document.StylesApplied);
        Assert.Equal(0, Shell.Document.StylesResolved);
        Assert.Equal(0, Shell.Document.ContainerScopesEntered);

        Shell.Document.Draw();
        Assert.Equal(commands, Shell.Document.Drawing.Commands.Count);
    }

    /// <summary>And one row of the hierarchy changing costs a cascade of tens, not of thousands.</summary>
    /// <remarks>
    ///     ⚠ <b>The interaction is the frame an application pays and the settled one is not.</b>
    ///     <c>Benchmarks/Vixen.Benchmarks.Ui/README.md</c> records what this looked like when nobody
    ///     was measuring it: one class on one row of 8 001 elements cost a full cascade — 9.5 ms and
    ///     8.87 MB, 41× the settled frame — because <c>StyleUpdater</c>, whose whole purpose is to
    ///     narrow exactly this, had no production caller. The bound here is against the shell's own
    ///     element count rather than a constant, so it stays a statement about <i>incrementality</i>
    ///     when the fixture grows.
    /// </remarks>
    [Fact]
    public void Selecting_one_row_restyles_a_neighbourhood_and_not_the_shell() {
        while (Shell.Document.Update()) {
            Shell.Document.Draw();
        }

        var elements = Count(Shell.Document.Root);
        var row = Shell.Hierarchy.Rows[0];

        row.AddClass("marked");

        Assert.True(Shell.Document.Update(), "adding a class dirtied nothing");
        Shell.Document.Draw();

        var resolved = Shell.Document.StylesResolved;

        Assert.True(resolved > 0, "the class change resolved no styles at all, so nothing was restyled");

        Assert.True(
            resolved < elements / 4,
            $"one class on one row cascaded {resolved} of {elements} elements, which is a full pass"
        );

        row.RemoveClass("marked");

        while (Shell.Document.Update()) {
            Shell.Document.Draw();
        }
    }

    /// <summary>And one row of scroll costs a cascade of the rows it rebound, not of the shell.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Virtualisation that re-cascades the document is virtualisation in name only</b>,
    ///         and that is what #598 measured: 590 KB of a 591 KB scrolled frame was
    ///         <c>Restyle</c>, with <c>StylesResolved</c> reading the shell's whole element count.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The element count is asserted unchanged, and that is the half #598 got
    ///         wrong.</b> It read the cold pass as rows being realised and released. They are not —
    ///         <c>DataGrid.Realise</c> pools its rows and parks the surplus, so the shell holds the
    ///         same 561 elements before a scroll and after it, on every frame of one. What bought
    ///         the cold pass was the rebinding: an inline <c>top</c> on each recycled row and a
    ///         label assignment in each cell, both of which went through <c>UiDocument.Invalidate</c>.
    ///         Keeping that equality here is what stops this test from being satisfied by a grid
    ///         that stopped recycling.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against the rows realised rather than a constant, and with a floor.</b> A
    ///         ceiling alone is met most convincingly by a scroll that cascaded nothing at all —
    ///         which is exactly what a grid whose <c>Scrolled</c> hook stopped firing would report.
    ///         The two-row baseline before the measured scroll is not cosmetic either:
    ///         <c>DataGrid.Overscan</c> is two, so the first two rows of scroll genuinely rebind
    ///         nothing and would satisfy the ceiling by doing no work.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Scrolling_one_row_restyles_the_rows_it_rebound_and_not_the_shell() {
        var height = Shell.Grid.RowHeight;

        // Past the overscan, so that the measured scroll is one that genuinely moves the window.
        Shell.Grid.Scroller.ScrollTop = height * 8f;

        while (Shell.Document.Update()) {
            Shell.Document.Draw();
        }

        var elements = Count(Shell.Document.Root);
        var realised = Shell.Grid.Rows.Count;

        Shell.Grid.Scroller.ScrollTop += height;

        Assert.True(Shell.Document.Update(), "a one-row scroll dirtied nothing, so the grid did not rebind");
        Shell.Document.Draw();

        var resolved = Shell.Document.StylesResolved;

        Assert.Equal(elements, Count(Shell.Document.Root));
        Assert.True(resolved > 0, $"the scroll cascaded nothing, so {realised} rows were rebound without restyling");

        Assert.False(
            Shell.Document.LastPassWasCold,
            $"a one-row scroll took a cold pass over the whole document — {resolved} of {elements} elements"
        );

        Assert.True(
            resolved <= realised * 4,
            $"a one-row scroll cascaded {resolved} elements for {realised} realised rows, of {elements} in the "
            + "shell, which is a document-wide pass rather than a row-wide one"
        );

        Shell.Grid.Scroller.ScrollTop = 0f;

        while (Shell.Document.Update()) {
            Shell.Document.Draw();
        }
    }

    /// <summary>And the composition costs a bounded number of elements, not one per datum.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that makes the whole row a claim about a <i>framework</i>.</b> Five panels,
    ///     a 500-node graph and a million-row table are 1 000 500 pieces of data; what the tree holds
    ///     has to be proportional to what is on screen instead, or "the editor is the
    ///     application-platform proof" is a statement about a document that cannot be opened. A
    ///     ceiling of ten thousand is roughly twenty times what is realised today, which is loose
    ///     enough to survive a panel being added and tight enough that one element per datum — or one
    ///     per graph node, which is the near miss — fails it.
    /// </remarks>
    [Fact]
    public void The_whole_shell_holds_a_bounded_number_of_elements() {
        var elements = Count(Shell.Document.Root);

        Assert.True(elements > 200, $"the shell holds {elements} elements, which is not a populated shell");
        Assert.True(elements < 10_000, $"the shell holds {elements} elements for {EditorShellScene.Rows} rows");
    }

    /// <summary>
    ///     The instrument the budget below is read through: a busy neighbour cannot move this
    ///     thread's allocation counter, not even by collecting.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Verify the instrument first, and this is the one question #992 asks that no
    ///         amount of re-running the budget can answer.</b> That issue's whole case is that the
    ///         red is not a proof — "8 120 bytes over ten frames is real allocation <i>somewhere in
    ///         the process</i>, and whatever it reads is a thread-local counter that a test class
    ///         running in parallel beside it can move". If that were true the bound below would be a
    ///         gate whose failure means nothing, and no number of green runs would show it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is false twice over, and the second half is the one that had not been
    ///         measured.</b> A neighbour thread's allocations land in that thread's own allocation
    ///         context and cannot reach this one — which was argued from the API's contract. But a
    ///         neighbour's <em>collection</em> is not confined to a thread at all: a GC retires every
    ///         allocation context on the heap, this one included, and "the accounting jumps when a
    ///         busy neighbour forces a gen-0" is exactly the shape that would fail under load and
    ///         pass alone. So it is measured rather than reasoned about, and the answer is zero
    ///         across every window in which collections were observed.
    ///     </para>
    ///     <para>
    ///         <b>Ordered by work, never by time.</b> The window closes when a fixed number of gen-0
    ///         collections have been <em>observed</em> — not after a sleep, which is the calibration
    ///         this repository files every flake under — and the iteration ceiling is a hang check
    ///         rather than a bound, which its own message says.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing is asserted inside the measured window</b>, for the reason the budget
    ///         test records: a passing <c>Assert</c> is still a call this loop cannot afford to be
    ///         wrong about, and a failing one allocates its message. The readings go into an array
    ///         made before the window and are read afterwards.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_neighbours_allocation_and_collection_cannot_move_this_threads_counter() {
        // Enough collections that a per-collection jump could not hide, and few enough that the
        // neighbour is a neighbour rather than the test.
        const int Collections = 8;

        // ⚠ A hang check and not a budget: the neighbour below forces a gen-0 on every pass, so ten
        // thousand windows without eight of them means the thread never started, not that the machine
        // is slow.
        const int Ceiling = 10_000;

        var readings = new long[Ceiling];
        var observed = new int[Ceiling];
        var running = true;
        var windows = 0;

        var neighbour = new Thread(() => {
            while (Volatile.Read(ref running)) {
                for (var i = 0; i < 2_000; i++) {
                    _ = new byte[1024];
                }

                GC.Collect(0, GCCollectionMode.Forced, blocking: false);
            }
        }) { IsBackground = true };

        // Warmed, so that nothing in the window below is a first call.
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = GC.CollectionCount(0);

        neighbour.Start();

        try {
            var start = GC.CollectionCount(0);

            while (windows < Ceiling) {
                var collections = GC.CollectionCount(0);
                var before = GC.GetAllocatedBytesForCurrentThread();

                // The measured thread's own work, which allocates nothing by construction — the
                // point is that its counter stays put while the neighbour churns the heap.
                Thread.SpinWait(20_000);

                readings[windows] = GC.GetAllocatedBytesForCurrentThread() - before;
                observed[windows] = GC.CollectionCount(0) - collections;
                windows++;

                if (GC.CollectionCount(0) - start >= Collections) {
                    break;
                }
            }
        } finally {
            Volatile.Write(ref running, false);
            neighbour.Join();
        }

        var collected = 0;
        var moved = 0;

        for (var i = 0; i < windows; i++) {
            collected += observed[i];

            if (readings[i] != 0) {
                moved++;
            }
        }

        // ⚠ First, because it is the premise. A run in which the neighbour never collected has
        // measured the quiet case and proved nothing about the loud one — the vacuous pass this
        // whole test exists to make impossible.
        Assert.True(
            collected >= Collections,
            $"only {collected} gen-0 collection(s) happened inside {windows} measured window(s), so "
            + "the neighbour never got going and this asserted nothing about a busy machine"
        );

        Assert.True(
            moved == 0,
            $"{moved} of {windows} windows saw this thread's allocation counter move while it "
            + $"allocated nothing and a neighbour forced {collected} collections. "
            + "GC.GetAllocatedBytesForCurrentThread is therefore NOT immune to a busy machine, and "
            + $"the zero bound in {nameof(A_settled_frame_allocates_nothing)} is a gate whose red "
            + "proves nothing — fix that before reading its next failure (#992)."
        );
    }

    /// <summary>A settled frame allocates nothing at all — the advanced set included.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Bytes are deterministic where microseconds are not</b>, so this is the one part of
    ///         doc 00's budget a gate can hold directly. <c>GC.GetAllocatedBytesForCurrentThread</c>
    ///         counts what the frame asked for, identically on an idle laptop and a loaded runner,
    ///         and it is the number <c>DocumentBenchmarks</c> caught going from zero to 40 bytes per
    ///         element within an hour of being recorded — a boxed enumerator in the draw walk that no
    ///         timing would have separated from noise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Zero, and it used to be a ceiling of eight kilobytes because the shell settled at
    ///         504 bytes.</b> #597 filed that gap as "a settled editor-shell frame allocates where a
    ///         plain document does not", and the answer was three more boxed enumerators of exactly
    ///         <c>UiElement.PaintOrder</c>'s kind: an icon walking <c>PathBuilder.Segments</c>, the
    ///         node minimap walking <c>NodeGraph.Nodes</c> twice, and the wire layer walking
    ///         <c>NodeGraph.Wires</c> — 64, 80 and 40 bytes, once per element per frame, on a document
    ///         nothing had changed in. Every one of those collections is typed
    ///         <c>IReadOnlyList&lt;T&gt;</c>, which is what makes a <c>foreach</c> over it box.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A ceiling would not have caught any of them, and that is the argument for the
    ///         zero.</b> 504 bytes is four per cent of an eight-kilobyte bound; the gate that held
    ///         that bound was green for the whole time the defect existed. Nothing between "the frame
    ///         asked the allocator for something" and "the frame asked for a lot" is a property worth
    ///         stating — a settled frame doing no work has no reason to allocate a single byte, so
    ///         the honest bound is the one that goes red the first time it does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The floor under it is <see cref="A_settled_shell_does_nothing_and_draws_the_same_frame" />,
    ///         and without it this is met by a shell that draws nothing.</b> Zero bytes is what a
    ///         broken document reports too; that test is what says this one is measuring a frame that
    ///         emits several hundred draw commands.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which frame it measures is the whole of #703, and <c>while (Update()) Draw();</c>
    ///         is not the warm-up it reads as.</b> The body runs only on iterations where
    ///         <c>Update</c> returned <c>true</c>, so a document that is <i>already</i> settled — and
    ///         <c>EditorShellScene.Build</c> ends with one <c>Update</c> and one <c>Draw</c>, so this
    ///         one always is — settles in zero iterations and never draws. The first measured frame
    ///         was therefore the <b>second draw of this list's life</b>, and it reported 479 280
    ///         bytes. See <see cref="The_second_draw_of_a_list_pays_for_its_comparison_snapshot" />
    ///         for what those bytes are; the fix here is that the warm-up draws unconditionally, so
    ///         that what this measures is the third draw and after.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"A neighbour moved the counter" is refuted, and the two halves it was one
    ///         question in are separated instead.</b> #992 saw this red once in a loaded worktree and
    ///         green alone, and read the byte count as the noisy half —
    ///         <c>GC.GetAllocatedBytesForCurrentThread</c> "is a thread-local counter that a test
    ///         class running in parallel beside it can move". It is not: it reports the calling
    ///         thread's own allocation context, and a test on another thread cannot add a byte to it.
    ///         Three process-wide channels that <i>could</i> have made these frames do work were
    ///         probed against this scene and each cost it nothing — ten
    ///         <see cref="Vixen.Ui.Reactive.Signal{T}" /> writes interleaved with the ten measured frames (which bump
    ///         the non-thread-static <c>ReactiveGraph.Epoch</c> every time), ten
    ///         <c>Strings.Use</c> catalogue swaps, and both together: 0 bytes each.
    ///         <c>UiDocument.Fonts</c> is per document, <c>EdgePool</c> is <c>[ThreadStatic]</c>, and
    ///         the Ui stack's only <c>ArrayPool&lt;T&gt;.Shared</c> is in a control this scene does
    ///         not build.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the half of that refutation which had only been argued is measured now.</b> A
    ///         neighbour's <em>collection</em> is not confined to a thread the way its allocations
    ///         are — a GC retires every allocation context, this one included — so "the accounting
    ///         jumps when a busy neighbour forces a gen-0" was still a live reading of a failure seen
    ///         under load and never alone.
    ///         <see cref="A_neighbours_allocation_and_collection_cannot_move_this_threads_counter" />
    ///         is that experiment kept: a thread that allocates nothing reads exactly zero across
    ///         every window in which a neighbour forced collections. So the counter is sound, this
    ///         bound's red is a proof, and #992's 8 120 bytes were bytes this thread really
    ///         allocated. The bound is not widened, for the second time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What was genuinely unproved is that the measured frames were settled at all.</b>
    ///         <see cref="Shell" /> is a static shared with every other test in this class, the loop
    ///         discarded what <c>Update</c> returned, and a frame that had work to do allocates for
    ///         the most ordinary reason there is — so the failure named the draw walk with confidence
    ///         in a case where the draw walk may be innocent. Both are asserted now: the frames did
    ///         no work, <i>and</i> they cost nothing. A red says which.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the bytes are counted per frame rather than across ten, because the sum
    ///         cannot tell a per-frame object from a one-off and the message asserts that it can.</b>
    ///         "Something on the draw walk is asking the allocator for a per-frame object" is one
    ///         reading of a non-zero total; a single frame paying once for a cache, a pool that
    ///         missed, or a buffer that grew is the other, and it is the reading that fits a failure
    ///         seen under load and never alone. #992's 8 120 bytes over ten frames is either 812 a
    ///         frame or 8 120 once, and nothing recorded says which. The breakdown costs nothing to
    ///         keep — <c>GC.GetAllocatedBytesForCurrentThread</c> allocates no more inside the loop
    ///         than outside it, and the array it fills is rented from nowhere and made before the
    ///         measurement starts — so the next red names the shape as well as the size.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_settled_frame_allocates_nothing() {
        while (Shell.Document.Update()) {
            Shell.Document.Draw();
        }

        // ⚠ Unconditional, and outside the loop rather than inside it. The loop above draws only on
        // the frames that had something to settle, so on an already-settled document it draws
        // nothing at all — and the very next draw is the one that grows `DrawList`'s previous-frame
        // buffers to the size of the frame. That growth is a one-off of the list, not of the frame,
        // and measuring across it is what made this test red.
        Shell.Document.Draw();

        // Ten frames, because a per-frame allocation of one object would otherwise be a number small
        // enough to read as measurement noise — which, being a count of bytes rather than of
        // microseconds, it never is.
        const int Frames = 10;

        // ⚠ Counted rather than asserted, because the assertion belongs outside the measured region:
        // an `Assert` that fails allocates its message, and one that passes still has to be a call
        // this loop can afford to be wrong about. An int is free.
        var worked = 0;

        // Made before the measurement, for the same reason, and written to by index inside it —
        // storing a long into a `long[]` is not an allocation, so keeping the breakdown is free.
        var cost = new long[Frames];

        for (var i = 0; i < Frames; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            if (Shell.Document.Update()) {
                worked++;
            }

            Shell.Document.Draw();

            cost[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var allocated = 0L;
        var frames = 0;

        foreach (var bytes in cost) {
            allocated += bytes;

            if (bytes > 0) {
                frames++;
            }
        }

        // ⚠ First, because it is the premise of the sentence below it. Bytes bought by a frame that
        // had layout to redo are not this test's subject, and reporting them as the draw walk's is
        // how a gate comes to name the wrong thing under load.
        Assert.True(
            worked == 0,
            $"{worked} of {Frames} frames reported work to do, so they were not settled frames and "
            + $"the {allocated} bytes they cost are not this test's claim — something outside the "
            + "measured loop dirtied the shell"
        );

        // ⚠ The shape before the size. Every frame paying is the draw walk; one frame paying is a
        // one-off — a cache filled, a pool that missed, a buffer grown — and the two want opposite
        // investigations. Saying which is the only thing #992's report could not do.
        Assert.True(
            allocated == 0,
            $"{Frames} settled frames of the shell allocated {allocated} bytes between them, and "
            + $"{frames} of the {Frames} paid: [{string.Join(", ", cost)}]. "
            + (frames == 1
                ? "One frame alone is a one-off rather than a per-frame object — look for something "
                + "warmed, pooled or grown on the first pass through, not for a boxed enumerator."
                : "Every frame paying is something on the draw walk asking the allocator per frame "
                + "— a boxed enumerator over a collection typed as an interface is what it has been "
                + "every time.")
        );
    }

    /// <summary>And the one draw that does allocate is buying the list's previous-frame snapshot.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The 479 KB #703 found is not on the draw walk at all, and it is not #597's kind of
    ///         defect.</b> <c>DrawList.BeginFrame</c> keeps the finished frame for comparison, which is
    ///         what lets <c>EndFrame</c> answer "did the drawing change" without re-walking anything.
    ///         The two buffers are a double buffer and it swaps them, so on the <b>second</b> draw of a
    ///         list the buffer swapped in is the empty one it started with and it grows once to its
    ///         frame's size. Every draw after that reuses capacity and allocates nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The swap did not remove this allocation, which #750 predicted it would, and left
    ///         alone it <i>tripled</i> it.</b> The copy it replaced sized its destination in one
    ///         <c>AddRange</c>; a buffer filled by <c>Add</c> doubles its way up instead and pays the
    ///         geometric series — 1 471 296 bytes here against the 479 280 that was measured. The
    ///         <c>EnsureCapacity(previous.Count)</c> in <c>DrawList.Swap</c> is what buys the single
    ///         allocation back, and this bound is what would notice if it went away. What the swap
    ///         removed is the per-<i>frame</i> half-megabyte <c>memcpy</c>, which no allocation counter
    ///         could see at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the number is closed form rather than a magic constant</b>, and asserting it as
    ///         one is what separates this from a per-frame object of the same size: the shell's
    ///         1 389 commands and 1 181 path segments come to 477 596 bytes of arrays, which is
    ///         99.6 % of the 479 280 that was measured. A per-element or per-frame allocation would
    ///         have no reason to land inside a bound derived from <c>sizeof</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Its own scene, and that is not incidental.</b> The property is about a
    ///         <c>DrawList</c> that has been drawn exactly once, which the shared <see cref="Shell" />
    ///         stops being the moment any other test in this class draws it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_second_draw_of_a_list_pays_for_its_comparison_snapshot() {
        // Built here rather than shared: `EditorShellScene.Build` ends with one `Update` and one
        // `Draw`, so this document has been drawn exactly once and the next draw is the second.
        var scene = EditorShellScene.Build();
        var drawing = scene.Document.Drawing;

        Assert.False(scene.Document.Update(), "the freshly built scene had not settled, so the count below moves");

        var commands = drawing.Commands.Count;
        var segments = drawing.Segments.Count;

        // The floor, and without it every bound below is met by a document that drew nothing.
        Assert.True(commands > 100, $"the shell emitted {commands} draw commands, which is not a populated shell");

        var snapshot = (commands * Unsafe.SizeOf<DrawCommand>())
            + (drawing.Glyphs.Count * Unsafe.SizeOf<PositionedGlyph>())
            + (segments * Unsafe.SizeOf<PathSegment>())
            + (drawing.Boxes.Count * Unsafe.SizeOf<BoxStyle>())
            + (drawing.Masks.Count * Unsafe.SizeOf<UiMask>());

        var before = GC.GetAllocatedBytesForCurrentThread();

        scene.Document.Draw();

        var second = GC.GetAllocatedBytesForCurrentThread() - before;

        // Five array headers and the odd rounding, and nothing that scales with the walk. A boxed
        // enumerator per element — the defect #597 closed and the one this test's neighbour would
        // read as the same number — is 1 389 of them, which does not fit here.
        Assert.True(
            second <= snapshot + 4096,
            $"the second draw allocated {second} bytes where its comparison snapshot is {snapshot}, so the "
            + "difference is something proportional to the walk rather than the one-off this pins"
        );

        // And the third draw and every one after it is free, which is what makes the second a
        // property of the list's life rather than of the frame.
        before = GC.GetAllocatedBytesForCurrentThread();

        scene.Document.Draw();
        scene.Document.Draw();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    static int Count(UiElement element) {
        var total = 1;

        foreach (var child in element.Children) {
            total += Count(child);
        }

        return total;
    }
}
