// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Benchmarks.Ui;

/// <summary>
///     A whole frame of a real document — the cascade, the font sizes, the layout style, flexbox and
///     the draw list — at the scale doc 14 sets as Phase 4's exit criterion.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="LayoutBenchmarks" /> measures the flexbox engine and nothing else, which is
///         the right thing to measure when the question is about layout. It is the wrong thing to
///         measure against <b>"UI frame under 2 ms with 5 000 elements and zero steady-state
///         allocation"</b>, because three of the four passes in that sentence are above the layout
///         tree: a selector match, a style resolve, a length conversion and a draw-list walk are
///         where a frame of a themed interface actually goes.
///     </para>
///     <para>
///         ⚠ <b><see cref="SteadyFrame" /> is the one the criterion is about.</b> A cold frame is
///         paid once; what an application does sixty times a second is this — a document nothing
///         changed in, which must cost a walk that finds nothing to do and must allocate nothing at
///         all. <c>[MemoryDiagnoser]</c> is what makes the second half of the claim checkable rather
///         than asserted.
///     </para>
///     <para>
///         Built out of real controls rather than bare elements, because that is what the number is
///         a claim about: five thousand <c>div</c>s with no rules matching them would measure a
///         cascade that has nothing to cascade.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class DocumentBenchmarks {
    UiDocument document = null!;
    UiElement toggled = null!;
    int frame;

    /// <summary>How many controls the document holds.</summary>
    /// <remarks>
    ///     Five thousand is doc 14's number. The two either side are there so the shape of the curve
    ///     is visible: a pass that is linear and a pass that is not look the same at one point.
    /// </remarks>
    [Params(500, 5_000, 20_000)]
    public int Controls { get; set; }

    [GlobalSetup]
    public void Setup() {
        document = new UiDocument(1600f, 1000f);
        ControlTheme.Install(document);

        document.Load(
            """
            root { width: 1600px; height: 1000px; flex-direction: column; flex-wrap: wrap; }
            .row { flex-direction: row; align-items: center; gap: 6px; padding: 2px; }
            .row.marked { background-color: #ff0000; }
            """
        );

        // Rows of five, which is roughly the density of an inspector — a label, a field and the
        // chrome around them.
        for (var i = 0; i < Controls / 5; i++) {
            var row = document.Root.Add<Panel>();
            row.AddClass("row");

            row.Add<TextBlock>().Text = "Label";

            var button = row.Add<Button>();
            button.Label = "Go";

            var check = row.Add<CheckBox>();
            check.Label = "On";
        }

        toggled = document.Root.Children[document.Root.Children.Count / 2];

        // Out of the way of the measurement: the first pass builds every computed style and every
        // layout node, and a benchmark that included it would be measuring start-up.
        document.Update();
        document.Draw();
    }

    [GlobalCleanup]
    public void Cleanup() => document.Dispose();

    /// <summary>Nothing changed: what leaving the interface on the screen costs.</summary>
    /// <remarks>
    ///     ⚠ Both passes, because both run every frame. <c>Update</c> returns immediately on a clean
    ///     document; <c>Draw</c> does not — it walks the tree, rebuilds the command list and diffs it
    ///     against the last one, which is what makes a static interface re-submit a cached buffer.
    ///     Measuring only the first would report a frame cost of nothing and be wrong by the whole
    ///     draw walk.
    /// </remarks>
    [Benchmark(Baseline = true)]
    public bool SteadyFrame() {
        document.Update();
        return document.Draw();
    }

    /// <summary>One row's class toggled: what a hover or a selection costs.</summary>
    /// <remarks>
    ///     The frame an application actually spends its time in. It restyles one element, relays out
    ///     what that moved, and redraws — and the gap between this and
    ///     <see cref="SteadyFrame" /> is the price of a single interaction.
    /// </remarks>
    [Benchmark]
    public bool OneRowChanged() {
        if (frame++ % 2 == 0) {
            toggled.AddClass("marked");
        } else {
            toggled.RemoveClass("marked");
        }

        document.Update();
        return document.Draw();
    }

    /// <summary>Everything dirty: a stylesheet reload, or the first frame.</summary>
    /// <remarks>
    ///     Paid on a theme change and at start-up, and never in a running frame. It is here as the
    ///     ceiling the other two are measured against.
    /// </remarks>
    [Benchmark]
    public bool ColdFrame() {
        document.Resize(1600f, 1000f + (frame++ % 2));

        document.Update();
        return document.Draw();
    }
}
