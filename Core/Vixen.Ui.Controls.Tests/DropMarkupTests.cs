// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Drag and drop reached from markup, which is the authoring path it had never been used on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The four target names were registered and nothing wrote one.</b>
///         <c>on:dragenter</c>, <c>on:dragover</c>, <c>on:dragleave</c> and <c>on:drop</c> are in
///         <c>BuildContext</c>'s subscription table, and a sweep of every <c>.vxml</c> in the
///         repository — samples, editor panels and this project's own fixtures — found no use of any
///         of them. The C# side of the drop model is thoroughly tested; the half an application is
///         actually told to write was a table entry nobody had exercised.
///     </para>
///     <para>
///         ⚠ <b>What that hides is specific rather than theoretical.</b> A name absent from the
///         table is an <c>on:</c> the binder rejects, so the failure mode of a wrong arm is not a
///         wrong handler — it is a handler that is never called, on a page that compiles. Both legs
///         are driven here: the operating system's drop, which is hit-tested and bubbles like a
///         wheel, and the in-app drag, which additionally has to find an
///         <see cref="UiElement.AllowDrop" /> element.
///     </para>
/// </remarks>
public class DropMarkupTests {
    /// <summary>A box for the element a drag starts from, which markup does not give it.</summary>
    const string Source = "drag-source { width: 40px; height: 40px; }";

    static DropSheet Mount(ControlFixture fixture) {
        var sheet = new DropSheet();

        BuildContext.BuildInto(sheet, fixture.Document, fixture.Document.Root);
        fixture.Update();

        return sheet;
    }

    static (float X, float Y) Middle(UiElement element) {
        var bounds = element.Bounds;

        return (bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
    }

    /// <summary>A file dragged out of Finder reaches a handler written in markup.</summary>
    [Fact]
    public void A_file_dropped_on_the_window_reaches_an_on_drop_written_in_markup() {
        using var fixture = new ControlFixture();

        var sheet = Mount(fixture);
        var (x, y) = Middle(sheet.Zone);

        var reached = fixture.Document.Dispatch(
            new DropEvent { X = x, Y = y, Files = ["/tmp/albedo.png", "/tmp/normal.png"] }
        );

        Assert.NotNull(reached);
        Assert.Equal(["/tmp/albedo.png", "/tmp/normal.png"], sheet.Files);
    }

    /// <summary>A drag begun inside the application enters, moves over, and is put down.</summary>
    /// <remarks>
    ///     ⚠ The source is an element of its own rather than the zone, because a drag whose source
    ///     and target are the same element never crosses a boundary and would leave
    ///     <see cref="DropSheet.Enters" /> at whatever it started as while looking like it worked.
    /// </remarks>
    [Fact]
    public void An_in_app_drag_raises_enter_over_and_drop_on_the_markup_handlers() {
        using var fixture = new ControlFixture(css: Source);

        var sheet = Mount(fixture);

        var source = fixture.Document.Root.Add("drag-source");
        fixture.Update();

        var data = new DataObject();
        data.SetText("a row");

        var (sourceX, sourceY) = Middle(source);
        fixture.Press(sourceX, sourceY);
        fixture.Document.BeginDrag(source, data);

        var (x, y) = Middle(sheet.Zone);
        fixture.MovePointer(x, y);

        Assert.Equal(1, sheet.Enters);
        Assert.Equal(0, sheet.Leaves);
        Assert.Equal(DropEffect.Copy, fixture.Document.CurrentDrag?.Effect);

        fixture.MovePointer(x + 4f, y + 4f);
        Assert.True(sheet.Moves > 0);

        fixture.Release(x + 4f, y + 4f);

        Assert.Equal("a row", sheet.Payload);
        Assert.Null(fixture.Document.CurrentDrag);
    }

    /// <summary>Leaving the zone again is reported once, not on every move outside it.</summary>
    [Fact]
    public void A_drag_that_crosses_back_out_reports_one_leave() {
        using var fixture = new ControlFixture(css: Source);

        var sheet = Mount(fixture);

        var source = fixture.Document.Root.Add("drag-source");
        fixture.Update();

        var (sourceX, sourceY) = Middle(source);
        fixture.Press(sourceX, sourceY);
        fixture.Document.BeginDrag(source, new DataObject());

        var (x, y) = Middle(sheet.Zone);
        fixture.MovePointer(x, y);
        Assert.Equal(1, sheet.Enters);

        var bounds = sheet.Zone.Bounds;
        fixture.MovePointer(bounds.X + bounds.Width + 40f, bounds.Y + bounds.Height + 40f);
        fixture.MovePointer(bounds.X + bounds.Width + 60f, bounds.Y + bounds.Height + 60f);

        Assert.Equal(1, sheet.Leaves);

        fixture.Document.CancelDrag();
    }
}
