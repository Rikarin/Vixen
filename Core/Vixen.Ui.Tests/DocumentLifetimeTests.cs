// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>That a document which has been disposed says so instead of taking the process with it.</summary>
/// <remarks>
///     <para>
///         <b>The failure this was written against destroyed the evidence that would have identified
///         it.</b> <c>UiDocument.Layout</c> is a <c>LayoutTree</c> and a <c>LayoutTree</c> is four
///         <c>NativeArray</c>s. Disposing it freed them and zeroed its capacity but left the struct
///         fields holding the freed pointers — so the next <c>CreateNode</c> grew from a capacity of
///         nought, found the arrays non-empty, copied out of memory that was no longer ours and
///         freed it a second time. The allocator aborted: no managed exception, no message, no
///         stack, and then <c>xunit.runner.visualstudio</c>'s
///         <c>TestProjectConfiguration.CrashDetectionSinkTimeoutOrDefault</c> — 60&#160;000&#160;ms
///         — before the adapter gave up on the dead host and printed "Catastrophic failure: Test
///         process crashed with exit code 134". A minute of nothing followed by an abort reads
///         identically to a deadlock, and it cost one debugging cycle spent in the wrong subsystem.
///     </para>
///     <para>
///         ⚠ <b>Every call here used to be fenced, and none of them is any more.</b> While the abort
///         was reachable, a test written the obvious way — dispose, then <c>Add</c> an element —
///         proved the fix on the day it was written and, on the day it regressed, did not fail: it
///         killed the run, after a minute, with no test name attached. So each call was made in a
///         form that threw <i>before</i> reaching the layout store — a null element, an owner from
///         another document — and the exception <i>type</i> carried the assertion.
///     </para>
///     <para>
///         <c>LayoutTree.Dispose</c> now clears its four fields and empties its free list, so a
///         disposed store grows a fresh set rather than copying out of dead memory, and the abort is
///         unreachable through any holder rather than only through a guarded document. The plain
///         form is therefore safe to write, and is written: a lost guard here is now a red
///         <c>Assert.Throws</c> naming the call that lost it. The store's own half of the contract
///         is asserted by <c>LayoutTreeTests.A_disposed_tree_can_be_used_again_rather_than_freeing_the_same_memory_twice</c>.
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
    ///     ⚠ <b>Creating an element is the call that actually aborted, and it is now asked plainly.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>Adopt</c> is the seam both <c>Create</c> overloads and every <c>UiElement.Add</c>
    ///         come through, and it allocates a layout node — the growth that used to copy out of the
    ///         freed arrays and free them again. So <c>document.Root.Add("div")</c> is the plainest
    ///         reproduction there is, and it was for exactly that reason the one assertion that could
    ///         not be written: with the guard gone it did not fail, it took the run with it.
    ///     </para>
    ///     <para>
    ///         It is written now. <c>LayoutTree.Dispose</c> clears its four <c>NativeArray</c> fields,
    ///         so the worst a lost guard can do here is create an element against a store that grew
    ///         itself a fresh set — a wrong answer, which <c>Assert.Throws</c> reports as one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Creating_an_element_on_a_disposed_document_throws_rather_than_aborting() {
        var document = Disposed();

        Assert.Throws<ObjectDisposedException>(() => document.Root.Add("div"));
        Assert.Throws<ObjectDisposedException>(() => document.Adopt(new UiElement(), "div", document.Root));

        // `elsewhere` is live, so nothing here depends on two disposed documents.
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
    ///     It is doubly true now — <c>UiDocument.Dispose</c> returns early on its <c>disposed</c>
    ///     field, and <c>LayoutTree.Dispose</c> would free nothing twice even if it did not — so this
    ///     no longer needs the <c>Update</c> assertion that used to fence it.
    /// </remarks>
    [Fact]
    public void Disposing_twice_is_allowed() {
        var document = Disposed();

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
