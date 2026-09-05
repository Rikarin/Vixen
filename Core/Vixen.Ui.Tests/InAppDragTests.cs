// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>A drag that starts inside the application and is negotiated by what it passes over.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about <i>which element</i> and <i>in what order</i>, never about
///     how long anything took.</b> The drag is driven by written-down pointer events exactly as
///     <see cref="GestureTests" /> drives the recogniser, so a busy machine changes nothing.
/// </remarks>
public class InAppDragTests {
    static UiDocument Laid() {
        var document = new UiDocument(400f, 300f);

        document.Load("""
            root { width: 400px; height: 300px; flex-direction: row; }
            .pane { width: 100px; height: 100px; flex-shrink: 0; }
            .leaf { width: 100px; height: 100px; flex-shrink: 0; }
        """);

        return document;
    }

    static PointerEvent At(PointerAction action, float x, float y, int milliseconds = 0) =>
        new() {
            Action = action,
            X = x,
            Y = y,
            PointerId = 0,
            Button = PointerButton.Primary,
            Timestamp = TimeSpan.FromMilliseconds(milliseconds)
        };

    static DataObject Row(string name) {
        var data = new DataObject();
        data.SetText(name);

        return data;
    }

    /// <summary>A drop target hears enter, over and drop, and the leaf inside it hears none of them.</summary>
    /// <remarks>
    ///     This is the whole reason <see cref="UiElement.AllowDrop" /> exists. The element under the
    ///     pointer while a drag crosses a row is whichever label or icon the layout put there, and
    ///     enter/leave raised on each of them would arrive dozens of times crossing one row.
    /// </remarks>
    [Fact]
    public void A_drag_is_addressed_to_the_nearest_ancestor_that_allows_drops() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        var leaf = pane.Add("div", classNames: "leaf");
        pane.AllowDrop = true;
        document.Update();

        var stages = new List<DragOverStage>();
        pane.AddHandler<DragOverEvent>((_, args) => stages.Add(args.Stage));

        var onLeaf = 0;
        leaf.AddHandler<DragOverEvent>((_, _) => onLeaf++);

        DropEvent? dropped = null;
        pane.AddHandler<DropEvent>((_, args) => dropped = args);

        document.BeginDrag(source, Row("track"), DropEffect.Move | DropEffect.Copy);
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));
        document.Dispatch(At(PointerAction.Moved, 160f, 50f));
        document.Dispatch(At(PointerAction.Released, 160f, 50f));

        Assert.Equal([DragOverStage.Entered, DragOverStage.Moved, DragOverStage.Moved, DragOverStage.Left], stages);
        Assert.Equal(0, onLeaf);
        Assert.NotNull(dropped);
        Assert.Equal("track", dropped.Data.Text);
        Assert.Same(source, dropped.DragSource);
        Assert.Equal(DropEffect.Move, dropped.Effect);
    }

    /// <summary>Move beats copy, and the target may narrow it to copy.</summary>
    [Fact]
    public void A_target_narrows_the_effect_and_the_drop_carries_what_it_chose() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        pane.AllowDrop = true;
        document.Update();

        var offered = new List<DropEffect>();
        pane.AddHandler<DragOverEvent>((_, args) => {
            offered.Add(args.Effect);
            args.Effect = DropEffect.Copy;
        });

        DropEvent? dropped = null;
        pane.AddHandler<DropEvent>((_, args) => dropped = args);

        var session = document.BeginDrag(source, Row("track"), DropEffect.Move | DropEffect.Copy);
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));

        // ⚠ The event arrives already accepting rather than already refusing, unlike the DOM's
        // `dragover`. `AllowDrop` is the opt-in here; a second one would mean a target that said it
        // was a target and silently was not.
        Assert.Equal(DropEffect.Move, Assert.Single(offered));
        Assert.Equal(DropEffect.Copy, session.Effect);

        document.Dispatch(At(PointerAction.Released, 150f, 50f));
        Assert.NotNull(dropped);
        Assert.Equal(DropEffect.Copy, dropped.Effect);
    }

    /// <summary>A target that refuses this payload gets no drop and stays a target.</summary>
    [Fact]
    public void A_target_that_answers_none_is_not_dropped_on() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        pane.AllowDrop = true;
        document.Update();

        pane.AddHandler<DragOverEvent>((_, args) => args.Effect = DropEffect.None);

        var drops = 0;
        pane.AddHandler<DropEvent>((_, _) => drops++);

        document.BeginDrag(source, Row("track"));
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));

        Assert.Same(pane, document.CurrentDrag?.Target);

        document.Dispatch(At(PointerAction.Released, 150f, 50f));
        Assert.Equal(0, drops);
        Assert.Null(document.CurrentDrag);
    }

    /// <summary>Leaving one target and entering another is a matched pair, in that order.</summary>
    [Fact]
    public void Crossing_from_one_target_to_another_leaves_before_it_enters() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var first = document.Root.Add("div", classNames: "pane");
        var second = document.Root.Add("div", classNames: "pane");
        first.AllowDrop = true;
        second.AllowDrop = true;
        document.Update();

        var order = new List<string>();
        first.AddHandler<DragOverEvent>((_, args) => order.Add("first:" + args.Stage));
        second.AddHandler<DragOverEvent>((_, args) => order.Add("second:" + args.Stage));

        document.BeginDrag(source, Row("track"));
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));
        document.Dispatch(At(PointerAction.Moved, 250f, 50f));

        Assert.Equal(["first:Entered", "first:Left", "second:Entered"], order);
    }

    /// <summary>The source's own capture does not decide where the drag is.</summary>
    /// <remarks>
    ///     ⚠ <b>The trap this suite exists for.</b> A source captures the pointer so it keeps
    ///     receiving moves once the cursor has left it — that is what makes a drag work at all — and
    ///     everything else positional in the document asks the capture first. A drop target chosen
    ///     that way is the source, forever, and the drag can never land anywhere.
    /// </remarks>
    [Fact]
    public void A_captured_pointer_still_finds_the_target_it_is_over() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        pane.AllowDrop = true;
        document.Update();

        document.CapturePointer(source);

        document.BeginDrag(source, Row("track"));
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));

        Assert.Same(pane, document.CurrentDrag?.Target);
    }

    /// <summary>Cancelling tells the target it lost the drag, and drops nothing.</summary>
    [Fact]
    public void Cancelling_leaves_the_target_and_drops_nothing() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        pane.AllowDrop = true;
        document.Update();

        var stages = new List<DragOverStage>();
        pane.AddHandler<DragOverEvent>((_, args) => stages.Add(args.Stage));

        var drops = 0;
        pane.AddHandler<DropEvent>((_, _) => drops++);

        document.BeginDrag(source, Row("track"));
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));

        Assert.True(document.CancelDrag());
        Assert.False(document.CancelDrag());
        Assert.Null(document.CurrentDrag);
        Assert.Equal([DragOverStage.Entered, DragOverStage.Left], stages);

        document.Dispatch(At(PointerAction.Released, 150f, 50f));
        Assert.Equal(0, drops);
    }

    /// <summary>A target removed mid-drag is forgotten rather than raised on.</summary>
    [Fact]
    public void A_target_that_leaves_the_tree_mid_drag_is_forgotten() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        var inner = pane.Add("div", classNames: "leaf");
        inner.AllowDrop = true;
        document.Update();

        var stages = new List<DragOverStage>();
        inner.AddHandler<DragOverEvent>((_, args) => stages.Add(args.Stage));

        document.BeginDrag(source, Row("track"));
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));
        Assert.Same(inner, document.CurrentDrag?.Target);

        // The subtree goes, not the target itself — which is the case a reference test misses.
        pane.Remove();
        document.Update();

        Assert.Null(document.CurrentDrag?.Target);
        Assert.Equal(DropEffect.None, document.CurrentDrag?.Effect);
        Assert.Equal([DragOverStage.Entered], stages);
    }

    /// <summary>A drag over nothing that allows drops reaches nobody and drops nowhere.</summary>
    [Fact]
    public void A_drag_over_a_tree_with_no_drop_target_drops_nothing() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        var pane = document.Root.Add("div", classNames: "pane");
        document.Update();

        var seen = 0;
        pane.AddHandler<DragOverEvent>((_, _) => seen++);
        pane.AddHandler<DropEvent>((_, _) => seen++);

        document.BeginDrag(source, Row("track"));
        document.Dispatch(At(PointerAction.Moved, 150f, 50f));
        document.Dispatch(At(PointerAction.Released, 150f, 50f));

        Assert.Equal(0, seen);
        Assert.Null(document.CurrentDrag);
    }

    /// <summary>A drag that allows nothing is not a drag.</summary>
    [Fact]
    public void A_drag_that_allows_no_effect_is_refused_at_the_start() {
        using var document = Laid();
        var source = document.Root.Add("div", classNames: "pane");
        document.Update();

        Assert.Throws<ArgumentException>(() => document.BeginDrag(source, Row("track"), DropEffect.None));
    }

    /// <summary>An OS drop with no session still reads through <see cref="DropEvent.Data" />.</summary>
    /// <remarks>
    ///     ⚠ <b>The point of materialising it.</b> Nothing in the platform layer knows a
    ///     <see cref="DataObject" /> exists, so a handler written against <c>Data</c> would see an
    ///     empty one on every file dragged out of Finder if this were a field a producer had to set.
    /// </remarks>
    [Fact]
    public void An_operating_system_drop_reads_as_a_data_object_nothing_filled_in() {
        using var document = Laid();
        var pane = document.Root.Add("div", classNames: "pane");
        document.Update();

        DropEvent? seen = null;
        pane.AddHandler<DropEvent>((_, args) => seen = args);

        document.Dispatch(new DropEvent { X = 10f, Y = 10f, Files = ["/tmp/a.png"] });

        Assert.NotNull(seen);
        Assert.Equal("/tmp/a.png", Assert.Single(seen.Data.Files));
        Assert.Null(seen.Data.Text);
        Assert.Null(seen.DragSource);
        Assert.Equal([DataFormats.FileUrl], seen.Data.Formats);
    }
}

/// <summary>The payload a source offers and a target picks a representation out of.</summary>
public class DataObjectTests {
    /// <summary>Formats come back in the order they were offered, which is the source's preference.</summary>
    [Fact]
    public void Formats_keep_the_order_they_were_offered_in() {
        var data = new DataObject();
        data.Set("vixen.asset-id", 42);
        data.SetText("Rock");
        data.SetFiles(["/tmp/rock.mat"]);

        Assert.Equal(["vixen.asset-id", DataFormats.Text, DataFormats.FileUrl], data.Formats);
    }

    /// <summary>Revising a representation keeps its position rather than demoting it.</summary>
    [Fact]
    public void Offering_the_same_format_twice_replaces_it_in_place() {
        var data = new DataObject();
        data.SetText("first");
        data.Set("vixen.asset-id", 42);
        data.SetText("second");

        Assert.Equal([DataFormats.Text, "vixen.asset-id"], data.Formats);
        Assert.Equal("second", data.Text);
    }

    /// <summary>A format asked for as the wrong type is the same answer as one never offered.</summary>
    [Fact]
    public void A_format_read_as_the_wrong_type_is_not_found_rather_than_throwing() {
        var data = new DataObject();
        data.Set("vixen.asset-id", 42);

        Assert.True(data.Has("vixen.asset-id"));
        Assert.False(data.TryGet<string>("vixen.asset-id", out var text));
        Assert.Null(text);
        Assert.True(data.TryGet<int>("vixen.asset-id", out var id));
        Assert.Equal(42, id);
    }

    /// <summary>Nothing offered is empty rather than null.</summary>
    [Fact]
    public void An_empty_payload_answers_empty() {
        var data = new DataObject();

        Assert.Empty(data.Formats);
        Assert.Empty(data.Files);
        Assert.Null(data.Text);
        Assert.False(data.Has(DataFormats.Text));
    }
}
