// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.EditorShell;

namespace Vixen.Benchmarks.Ui;

/// <summary>The composition doc 09 § Testing names as the gate, measured.</summary>
/// <remarks>
///     <para>
///         <b>"5 panels + viewport + 500-node graph + a 10⁶-row virtualised grid holds the doc 00
///         budget"</b>, and <b>"per the decided audience order, the editor — not a sample — is the
///         application-platform proof"</b>. <see cref="DocumentBenchmarks" /> measures five thousand
///         controls of the <i>same</i> shape, which is the right fixture for doc 14's Phase 4
///         criterion and the wrong one for this: what the row is about is the interaction between
///         four subsystems that each pass alone.
///     </para>
///     <para>
///         ⚠ <b>This is the measurement and it is not the gate.</b> A benchmark reports; it cannot
///         fail. The gate is <c>Vixen.Ui.Controls.Advanced.Tests.EditorShellBudgetTests</c>, which
///         holds the same scene to properties expressed as <i>work</i> — elements realised, styles
///         cascaded, draw commands emitted — because a millisecond budget calibrated on an idle
///         machine is this repository's largest flake source, and because a frame that is slow
///         because it realised a million rows and one that is slow because the machine is busy print
///         the same number. What this adds is the number a budget is written in, and the allocation,
///         which no counter in the gate can see.
///     </para>
///     <para>
///         ⚠ <b>The scene is the gate's own file, linked rather than copied.</b> A benchmark and a
///         gate that build "the same" shell out of two sources stop describing the same shell within
///         a month, and nothing goes red when they do.
///     </para>
///     <para>
///         ⚠ <b><see cref="Setup" /> builds the shell once and every benchmark reuses it</b>, which
///         is why there is no cold-build case here: constructing it copies a million items into the
///         grid's list, and that cost is the fixture's rather than the framework's. A cold
///         <i>frame</i> is measured instead, by invalidating the document that already exists.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class EditorShellBenchmarks {
    EditorShellScene.Scene shell = null!;
    bool marked;

    [GlobalSetup]
    public void Setup() {
        shell = EditorShellScene.Build();

        // Settled before anything is timed, with a ceiling that is a hang check and not a budget.
        for (var i = 0; i < 16 && shell.Document.Update(); i++) {
            shell.Document.Draw();
        }
    }

    /// <summary>The frame an application spends sixty times a second: nothing changed.</summary>
    /// <remarks>
    ///     ⚠ <b>The allocation is what to read here, not the time.</b> <c>Update</c> returns
    ///     immediately on a clean document, so the time is the draw walk and the frame diff — and the
    ///     claim worth holding is that a settled editor allocates nothing at all, which is the one
    ///     <c>DocumentBenchmarks</c> found had quietly stopped being true within an hour of being
    ///     recorded.
    /// </remarks>
    [Benchmark(Baseline = true)]
    public int SteadyFrame() {
        shell.Document.Update();
        shell.Document.Draw();

        return shell.Document.Drawing.Commands.Count;
    }

    /// <summary>And the frame it actually pays: one row of the hierarchy changed.</summary>
    /// <remarks>
    ///     Toggled rather than set, so the benchmark is idempotent across iterations — a fixture that
    ///     added a class it never removed would measure the first iteration and a no-op thereafter,
    ///     which reads as an implausibly fast interaction rather than as a broken benchmark.
    /// </remarks>
    [Benchmark]
    public int RowSelected() {
        var row = shell.Hierarchy.Rows[0];

        if (marked) {
            row.RemoveClass("marked");
        } else {
            row.AddClass("marked");
        }

        marked = !marked;

        shell.Document.Update();
        shell.Document.Draw();

        return shell.Document.Drawing.Commands.Count;
    }

    /// <summary>Scrolling the million-row grid, which is where virtualisation is paid for.</summary>
    /// <remarks>
    ///     ⚠ <b>The one interaction whose cost is <i>supposed</i> to be independent of the item
    ///     count.</b> A grid that realised rows lazily but kept them would look identical here on the
    ///     first frame and diverge on the thousandth, which is why this scrolls by a row each
    ///     iteration rather than jumping once.
    /// </remarks>
    [Benchmark]
    public int GridScrolled() {
        shell.Grid.Scroller.ScrollTop += 24f;

        shell.Document.Update();
        shell.Document.Draw();

        return shell.Grid.Rows.Count;
    }

    /// <summary>A cold frame: everything restyled and relaid out, which is a theme reload.</summary>
    [Benchmark]
    public int ColdFrame() {
        shell.Document.Invalidate();
        shell.Document.Update();
        shell.Document.Draw();

        return shell.Document.Drawing.Commands.Count;
    }

    [GlobalCleanup]
    public void Cleanup() => shell.Document.Dispose();
}
