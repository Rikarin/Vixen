// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a document which has been disposed says so instead of taking the process with it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The failure this replaces destroyed the evidence that would have identified
///         it.</b> <c>UiDocument.Layout</c> is a <c>LayoutTree</c> and a <c>LayoutTree</c> is four
///         <c>NativeArray</c>s. Disposing it frees them and zeroes its capacity but leaves the
///         struct fields holding the freed pointers — so the next <c>CreateNode</c> grows from a
///         capacity of nought, finds the arrays non-empty, copies out of memory that is no longer
///         ours and frees it a second time. The allocator aborts. No managed exception, no message,
///         no stack: the run ends with <c>SIGABRT</c> and stdout stops mid-sentence.
///     </para>
///     <para>
///         ⚠ <b>And the minute of silence in front of it belongs to the test runner, not to the
///         document.</b> The abort is immediate — a standalone process dies inside a millisecond of
///         the call. What waits is <c>xunit.runner.visualstudio</c>'s
///         <c>TestProjectConfiguration.CrashDetectionSinkTimeoutOrDefault</c>, 60&#160;000&#160;ms,
///         before the adapter gives up on the dead test host and prints "Catastrophic failure: Test
///         process crashed with exit code 134". A minute of nothing followed by an abort reads
///         identically to a deadlock, to a native crash in the RHI and to a host timeout, and it has
///         already cost one debugging cycle spent in the wrong subsystem.
///     </para>
///     <para>
///         ⚠ <b>Which is why no test here performs the abort, even to check that it no longer
///         happens.</b> Written the obvious way — dispose, then <c>Add</c> an element — a test proves
///         the fix today and, on the day it regresses, does not fail: it kills the run, after a
///         minute, with no test name attached to it. So every call below is made in a form that
///         throws before it reaches the layout store, and the exception <i>type</i> carries the
///         assertion. A lost guard is then a red line about an exception type rather than a dead
///         process. See
///         <see cref="Creating_an_element_on_a_disposed_document_throws_rather_than_aborting" />.
///     </para>
///     <para>
///         The one exception is disposing twice, which cannot be probed without doing it. It is
///         fenced behind an <c>Update</c> assertion instead, so removing the <c>disposed</c> field
///         is caught before the second <c>Dispose</c> runs; only a deliberate rewrite of
///         <c>Dispose</c> that keeps the field and drops its early return gets past that.
///     </para>
///     <para>
///         ⚠ The abort would stop being reachable at all if <c>LayoutTree.Dispose</c> cleared its
///         four <c>NativeArray</c> fields rather than leaving them holding freed pointers — a
///         disposed store would then grow a fresh set instead of copying out of dead memory. That is
///         the deeper fix and it belongs to <c>Vixen.Ui.Layout</c>; this is the guard in front of it.
///     </para>
/// </remarks>
public class DocumentLifetimeTests {
    static UiDocument Disposed() {
        var document = new UiDocument(800f, 600f);
        document.Load(".box { width: 10px; height: 20px; }");
        document.Root.Add("div", null, "box");
        document.Update();
        document.Dispose();

        return document;
    }

    /// <summary>
    ///     ⚠ <b>The passes and the loads, which are what a host calls every frame.</b>
    /// </summary>
    /// <remarks>
    ///     None of these can abort on their own — the layout store refuses a node it no longer holds
    ///     — so they are safe to assert directly. They are here because an
    ///     <c>ArgumentOutOfRangeException</c> naming an internal node id is not an answer to "why did
    ///     my panel stop updating"; <c>ObjectDisposedException</c> is.
    /// </remarks>
    [Fact]
    public void The_passes_refuse_a_disposed_document() {
        var document = Disposed();

        Assert.Throws<ObjectDisposedException>(() => document.Update());
        Assert.Throws<ObjectDisposedException>(() => document.Tick(TimeSpan.FromSeconds(1)));
        Assert.Throws<ObjectDisposedException>(() => document.Draw());
        Assert.Throws<ObjectDisposedException>(() => document.Load("div { width: 1px; }"));
        Assert.Throws<ObjectDisposedException>(() => document.LoadOnce(new object(), "div { width: 1px; }"));
        Assert.Throws<ObjectDisposedException>(() => document.ReloadStyles(0, "div { width: 1px; }"));
    }

    /// <summary>
    ///     ⚠ <b>And the surface calls, which is where hot reload arrives.</b>
    /// </summary>
    /// <remarks>
    ///     A host keeps its windows keyed by surface and hears about a document being rebuilt through
    ///     an event. The window that has not been told yet is the one that calls <c>Resize</c> on the
    ///     old document, and a resize on a released store is the same silence as everything else.
    /// </remarks>
    [Fact]
    public void The_surface_calls_refuse_a_disposed_document() {
        var document = Disposed();
        var primary = document.Primary;

        Assert.Throws<ObjectDisposedException>(() => document.Resize(900f, 700f));
        Assert.Throws<ObjectDisposedException>(() => document.Resize(primary, 900f, 700f, 2f));
        Assert.Throws<ObjectDisposedException>(() => document.RemoveSurface(primary));
        Assert.Throws<ObjectDisposedException>(() => document.SurfaceOf(document.Root));
        Assert.Throws<ObjectDisposedException>(() => document.Draw(primary));
    }

    /// <summary>
    ///     ⚠ <b>And the three mutations, which is where a stale reference to an element arrives.</b>
    /// </summary>
    /// <remarks>
    ///     An element outlives the document that held it — a component keeps its host, a captured
    ///     drag keeps its target — so a caller holding one and putting it somewhere is the ordinary
    ///     way a disposed document is reached without anybody meaning to.
    /// </remarks>
    [Fact]
    public void The_tree_mutations_refuse_a_disposed_document() {
        var document = new UiDocument(800f, 600f);
        var first = document.Root.Add("div");
        var second = document.Root.Add("div");
        document.Update();
        document.Dispose();

        Assert.Throws<ObjectDisposedException>(() => document.Move(first, 1));
        Assert.Throws<ObjectDisposedException>(() => document.Remove(first));
        Assert.Throws<ObjectDisposedException>(() => document.Reparent(first, second));
    }

    /// <summary>
    ///     ⚠ <b>Creating an element is the call that actually aborted, and it is asked in the one
    ///     form that cannot abort.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Adopt</c> is the seam both <c>Create</c> overloads and every <c>UiElement.Add</c>
    ///         come through, and it allocates a layout node — the growth that copies out of the freed
    ///         arrays and frees them again. So <c>document.Root.Add("div")</c> is the plainest
    ///         reproduction there is and the one assertion that must not be written: with the guard
    ///         gone it does not fail, it takes the whole run with it, and the run then sits silent
    ///         for the runner's minute before saying so.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So each call is made with an argument its own next line would refuse.</b> A null
    ///         element and an owner belonging to another document both throw before anything reaches
    ///         the layout store, which makes the exception <i>type</i> the whole assertion:
    ///         <c>ObjectDisposedException</c> means the guard ran, and
    ///         <c>ArgumentNullException</c> or <c>ArgumentException</c> means it did not. Either way
    ///         the process survives to report it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Creating_an_element_on_a_disposed_document_throws_rather_than_aborting() {
        var document = Disposed();

        // ⚠ `Adopt` rather than `Create` or `Add`, because it is the only one of the three that
        // takes the element as an argument — and a null one is refused on the line below the guard.
        Assert.Throws<ObjectDisposedException>(() => document.Adopt(null!, "div", document.Root));

        // ⚠ And an owner from another document, which `CreateSurface` refuses immediately after its
        // own guard. `elsewhere` is live, so nothing here depends on two disposed documents.
        using var elsewhere = new UiDocument(100f, 100f);
        Assert.Throws<ObjectDisposedException>(() => document.CreateSurface(400f, 300f, 1f, elsewhere.Root));
    }

    /// <summary>
    ///     ⚠ <b>And disposing twice is the plainest way to reach the double free of all.</b>
    /// </summary>
    /// <remarks>
    ///     A document inside a <c>using</c> that is also disposed by the host it was handed to is an
    ///     ordinary arrangement rather than a mistake, and <c>IDisposable</c> promises this is
    ///     allowed. It was not: the second call handed the same four addresses back to the allocator.
    ///     Fenced behind the same <c>Update</c> assertion, for the same reason.
    /// </remarks>
    [Fact]
    public void Disposing_twice_is_allowed() {
        var document = Disposed();

        Assert.Throws<ObjectDisposedException>(() => document.Update());
        document.Dispose();
        document.Dispose();
    }

    /// <summary>
    ///     ⚠ <b>A live document is not affected by any of the above.</b>
    /// </summary>
    /// <remarks>
    ///     The guard is a field read at the entry points, and the assertion worth writing about it is
    ///     that the ordinary path still works — a check placed one call too deep would have made
    ///     every second <c>Update</c> throw, and every test in this assembly would say so at once.
    ///     This is here so that the failure is one named test rather than the whole suite.
    /// </remarks>
    [Fact]
    public void A_live_document_is_untouched_by_the_guard() {
        using var document = new UiDocument(800f, 600f);
        document.Load(".box { width: 10px; height: 20px; }");

        var box = document.Root.Add("div", null, "box");
        document.Update();
        document.Tick(TimeSpan.FromSeconds(1));
        document.Draw();

        Assert.Equal(10f, box.Width, 0.001f);
    }
}
