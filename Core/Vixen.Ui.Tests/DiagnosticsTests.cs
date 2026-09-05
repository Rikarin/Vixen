// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>What a debug overlay may read about a document — doc 13's blocked panel, unblocked.</summary>
/// <remarks>
///     <para>
///         <b>An aggregator, so most of what is asserted here is that it aggregates.</b> Every
///         counter but the regions was already published by the pass that computes it; what
///         <see cref="UiDiagnostics" /> adds is one place to read them from and one recording that
///         did not exist — <c>MarkDirty</c> and <c>RaiseCommandsInvalidated</c> recorded that
///         something changed and never what or where.
///     </para>
///     <para>
///         ⚠ <b>The instrument is checked before the readings.</b>
///         <see cref="UiDiagnostics.RecordsRegions" /> is the difference between "nothing was
///         invalidated" and "nobody was recording", and every region test here skips rather than
///         passes when it is false — a suite that went green in a build with the recording compiled
///         out would be the exact defect this repository keeps meeting.
///     </para>
/// </remarks>
public class DiagnosticsTests {
    static UiDocument Document() {
        var document = new UiDocument(200f, 100f);

        document.Load("root { width: 200px; height: 100px; } box { width: 40px; height: 20px; }");

        return document;
    }

    static void Settle(UiDocument document) {
        for (var i = 0; i < 16 && document.Update(); i++) {
            document.Draw();
        }
    }

    /// <summary>One frame, which is where the regions of the pass it runs are readable.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="Settle" />, and the difference is the point.</b> Settling calls
    ///     <c>Update</c> until it reports no work, and the call that reports none is the one that
    ///     clears the regions — because a frame that did nothing was invalidated by nothing. So a
    ///     reading is taken from the frame that did the work, which is also when an overlay drawing a
    ///     dirty-region highlight is drawn.
    /// </remarks>
    static void Frame(UiDocument document) {
        document.Update();
        document.Draw();
    }

    [Fact]
    public void A_settled_document_reports_no_work_and_no_regions() {
        using var document = Document();

        document.Root.Add("box");
        Settle(document);

        var diagnostics = document.Diagnostics;

        Assert.False(document.Update(), "the document is not settled, so this measures a frame that did work");
        Assert.Equal(0, diagnostics.StylesResolved);
        Assert.Equal(0, diagnostics.StylesApplied);
        Assert.True(diagnostics.Settled);

        // ⚠ Empty on a settled frame rather than still showing the last real pass's boxes, which is
        // the failure the counters beside it carry a paragraph about.
        Assert.Empty(diagnostics.DirtyRegions.ToArray());
        Assert.Equal(0, diagnostics.RegionsRecorded);
    }

    [Fact]
    public void A_class_a_state_and_an_inline_write_are_recorded_apart() {
        Assert.SkipWhen(!UiDiagnostics.RecordsRegions, "Compiled out; needs DEBUG or VIXEN_UI_DIAGNOSTICS.");

        using var document = Document();
        var box = document.Root.Add("box");

        Settle(document);

        box.AddClass("lit");
        box.SetStyle("top", "4px");
        box.State |= ElementState.Hover;

        // The pass that consumes them is what turns the recording into a reading, exactly as it is
        // what zeroes the counters — so the regions are read after `Update`, not before it.
        Frame(document);

        var regions = document.Diagnostics.DirtyRegions.ToArray();

        Assert.Equal(3, document.Diagnostics.RegionsRecorded);
        Assert.Contains(regions, region => region.Kind == UiInvalidationKind.Class);
        Assert.Contains(regions, region => region.Kind == UiInvalidationKind.Inline);
        Assert.Contains(regions, region => region.Kind == UiInvalidationKind.State);

        // ⚠ The box as it was, which is the region that has to be repainted — and it is a real box
        // rather than the zero an unlaid-out element would report.
        foreach (var region in regions) {
            Assert.Equal(40f, region.Bounds.Width, 0.5f);
            Assert.Equal(20f, region.Bounds.Height, 0.5f);
            Assert.True(region.Bounds.Width > 0f, "the region is empty, so nothing would be highlighted");
        }
    }

    [Fact]
    public void A_cold_invalidation_is_recorded_as_the_document_itself() {
        Assert.SkipWhen(!UiDiagnostics.RecordsRegions, "Compiled out; needs DEBUG or VIXEN_UI_DIAGNOSTICS.");

        using var document = Document();

        document.Root.Add("box");
        Settle(document);

        document.Invalidate();
        Frame(document);

        var region = Assert.Single(document.Diagnostics.DirtyRegions.ToArray());

        Assert.Equal(UiInvalidationKind.Document, region.Kind);
        Assert.Equal(200f, region.Bounds.Width, 0.5f);
        Assert.Equal(100f, region.Bounds.Height, 0.5f);
    }

    [Fact]
    public void The_boxes_nest_and_say_where_the_padding_went() {
        using var document = new UiDocument(200f, 100f);

        document.Load(
            "root { width: 200px; height: 100px; } "
            + "box { width: 40px; height: 20px; margin: 5px; border-width: 2px; padding: 3px; }"
        );

        var box = document.Root.Add("box");
        Settle(document);

        var model = document.Diagnostics.BoxOf(box);

        // ⚠ Content-box sizing, which is CSS's initial value and *not* Yoga's — this assembly's
        // README records the four places the two disagree, and this is one of them. So the declared
        // 40×20 is the content box and the border box is 40 + two 3px paddings + two 2px borders.
        Assert.Equal(40f, model.Content.Width, 0.01f);
        Assert.Equal(46f, model.Padding.Width, 0.01f);
        Assert.Equal(50f, model.Border.Width, 0.01f);
        Assert.Equal(60f, model.Margin.Width, 0.01f);

        Assert.Equal(20f, model.Content.Height, 0.01f);
        Assert.Equal(26f, model.Padding.Height, 0.01f);
        Assert.Equal(30f, model.Border.Height, 0.01f);
        Assert.Equal(40f, model.Margin.Height, 0.01f);

        // And they are concentric, which is the half a wrong sign gets away with on width alone.
        Assert.True(model.Margin.X < model.Border.X, "the margin box does not contain the border box");
        Assert.True(model.Border.X < model.Padding.X, "the padding box is not inside the border box");
        Assert.True(model.Padding.X < model.Content.X, "the content box is not inside the padding box");
    }

    [Fact]
    public void The_element_under_a_point_is_the_one_a_pointer_would_talk_to() {
        using var document = Document();
        var box = document.Root.Add("box");

        Settle(document);

        Assert.True(document.Diagnostics.TryDescribe(10f, 10f, out var element, out var model));
        Assert.Same(box, element);
        Assert.Equal(box.Bounds, model.Border);

        // Off the end of everything: no element, and the caller is told so rather than handed the root.
        Assert.False(document.Diagnostics.TryDescribe(-40f, -40f, out var missing, out _));
        Assert.Null(missing);
    }

    [Fact]
    public void Reading_the_diagnostics_allocates_nothing() {
        using var document = Document();
        var box = document.Root.Add("box");

        Settle(document);

        // Warm: the first read of anything here touches the layout results and the JIT.
        var warm = document.Diagnostics;
        _ = warm.LayoutNodes + warm.StylesResolved + warm.RegionsRecorded;
        _ = warm.BoxOf(box);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 100; i++) {
            var diagnostics = document.Diagnostics;

            _ = diagnostics.LayoutNodes;
            _ = diagnostics.StylesResolved;
            _ = diagnostics.StylesApplied;
            _ = diagnostics.SettlingPasses;
            _ = diagnostics.Settled;
            _ = diagnostics.LastPassWasCold;
            _ = diagnostics.RegionsRecorded;
            _ = diagnostics.DirtyRegions.Length;
            _ = diagnostics.BoxOf(box);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // ⚠ Zero, not a ceiling. A panel that is on for minutes at a time in the frame it is
        // diagnosing would otherwise be measuring itself — the trap #597 is about one level up,
        // where three boxed `IReadOnlyList<T>` enumerators cost a settled frame 504 bytes and a
        // ceiling of eight kilobytes stayed green throughout.
        Assert.True(
            allocated == 0,
            $"a hundred reads of the diagnostics allocated {allocated} bytes, so something on the "
            + "read path is boxing — a span returned as an interface is what it has been every time"
        );
    }
}
