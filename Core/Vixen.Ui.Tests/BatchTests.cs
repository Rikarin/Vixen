// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Grouping a frame's commands into runs that can be drawn as one.</summary>
public class BatchTests {
    static readonly Gen<DrawCommandKind> Kinds = Gen.OneOfConst(
        DrawCommandKind.Rectangle,
        DrawCommandKind.Border,
        DrawCommandKind.Text,
        DrawCommandKind.Path,
        DrawCommandKind.PathStroke,
        DrawCommandKind.ClipPush,
        DrawCommandKind.ClipPop
    );

    /// <summary>Commands with only the fields batching reads filled in.</summary>
    /// <remarks>
    ///     Everything else is left at zero deliberately. A generator that varied the geometry would
    ///     be generating differences the batcher is supposed to ignore, and a batcher that started
    ///     splitting on them would still pass.
    /// </remarks>
    static readonly Gen<DrawCommand> Commands =
        from kind in Kinds
        from font in Gen.Int[0, 2]
        from rule in Gen.OneOfConst(PathFillRule.NonZero, PathFillRule.EvenOdd)
        select new DrawCommand(kind, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f) { Font = font, FillRule = rule };

    static List<DrawBatch> Batch(params DrawCommand[] commands) {
        var batches = new List<DrawBatch>();
        DrawBatcher.Build(commands, batches);

        return batches;
    }

    static DrawCommand Of(DrawCommandKind kind, int font = 0, PathFillRule rule = PathFillRule.NonZero) =>
        new(kind, 0f, 0f, 0f, 0f, Color4.White, 0f, 0f) { Font = font, FillRule = rule };

    [Fact]
    public void The_batches_cover_every_command_exactly_once_and_in_order() {
        Commands.Array[0, 40].Sample(commands => {
            var batches = new List<DrawBatch>();
            DrawBatcher.Build(commands, batches);

            // The property the whole design rests on: a consumer walks the batches alone, so an
            // uncovered command is a thing that silently does not get drawn and an overlapping one is
            // a thing drawn twice.
            var next = 0;
            foreach (var batch in batches) {
                Assert.Equal(next, batch.First);
                Assert.True(batch.Count > 0);
                next += batch.Count;
            }

            Assert.Equal(commands.Length, next);
        });
    }

    [Fact]
    public void Every_command_in_a_batch_belongs_to_it() {
        Commands.Array[0, 40].Sample(commands => {
            var batches = new List<DrawBatch>();
            DrawBatcher.Build(commands, batches);

            foreach (var batch in batches) {
                for (var i = batch.First; i < batch.First + batch.Count; i++) {
                    var command = commands[i];

                    var expected = command.Kind switch {
                        DrawCommandKind.Rectangle or DrawCommandKind.Border => BatchKind.Geometry,
                        DrawCommandKind.Text => BatchKind.Text,
                        DrawCommandKind.Path => BatchKind.PathFill,
                        DrawCommandKind.PathStroke => BatchKind.PathStroke,
                        _ => BatchKind.Clip
                    };

                    Assert.Equal(expected, batch.Kind);

                    if (batch.Kind == BatchKind.Text) {
                        Assert.Equal(batch.Font, command.Font);
                    }

                    if (batch.Kind == BatchKind.PathFill) {
                        Assert.Equal(batch.FillRule, command.FillRule);
                    }
                }
            }
        });
    }

    [Fact]
    public void No_two_neighbouring_batches_could_have_been_one() {
        Commands.Array[0, 40].Sample(commands => {
            var batches = new List<DrawBatch>();
            DrawBatcher.Build(commands, batches);

            for (var i = 1; i < batches.Count; i++) {
                var (before, after) = (batches[i - 1], batches[i]);

                // Maximality, which is the only thing making this batching rather than a rename of
                // the command list. A clip is the exception and is allowed to neighbour a clip,
                // because a state change is not a draw and two of them are two of them.
                var mergeable = before.Kind == after.Kind
                    && before.Font == after.Font
                    && before.FillRule == after.FillRule
                    && before.Kind != BatchKind.Clip;

                Assert.False(mergeable);
            }
        });
    }

    [Fact]
    public void Two_runs_of_the_same_kind_are_not_moved_together() {
        var batches = Batch(
            Of(DrawCommandKind.Text, font: 1),
            Of(DrawCommandKind.Rectangle),
            Of(DrawCommandKind.Text, font: 1)
        );

        // ⚠ Three batches, not two, and this is the point rather than a limitation. Batching
        // everywhere else means sorting draws by material, which works because a depth buffer decides
        // what ends up in front. A user interface has no depth buffer — order *is* the answer — so
        // merging the two text runs across the panel between them draws the text over the panel that
        // was meant to cover it.
        Assert.Equal(
            [BatchKind.Text, BatchKind.Geometry, BatchKind.Text],
            batches.Select(static batch => batch.Kind)
        );
    }

    [Fact]
    public void Neighbours_of_the_same_kind_become_one() {
        var batches = Batch(
            Of(DrawCommandKind.Rectangle),
            Of(DrawCommandKind.Border),
            Of(DrawCommandKind.Rectangle)
        );

        // Rectangles and borders are grouped on the argument that both are signed-distance quads.
        // ⚠ That grouping is a guess at a renderer that does not exist yet — phase 5's render feature
        // is what will know — and everything else here holds whatever it turns out to be.
        var batch = Assert.Single(batches);
        Assert.Equal(BatchKind.Geometry, batch.Kind);
        Assert.Equal(3, batch.Count);
    }

    [Fact]
    public void Two_fonts_are_two_batches() {
        var batches = Batch(Of(DrawCommandKind.Text, font: 0), Of(DrawCommandKind.Text, font: 1));

        // A different face is a different atlas, so it cannot be the same draw however adjacent the
        // two runs are.
        Assert.Equal(2, batches.Count);
        Assert.Equal([0, 1], batches.Select(static batch => batch.Font));
    }

    [Fact]
    public void Two_fill_rules_are_two_batches() {
        var batches = Batch(
            Of(DrawCommandKind.Path, rule: PathFillRule.NonZero),
            Of(DrawCommandKind.Path, rule: PathFillRule.EvenOdd)
        );

        // ⚠ One of these punches its hole and the other does not, so merging them fills in every
        // counter in an icon set — and the two paths would look identical in the buffer while doing
        // it, which is what makes it hard to see.
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void A_filled_path_and_a_stroked_one_are_not_the_same_draw() {
        var batches = Batch(Of(DrawCommandKind.Path), Of(DrawCommandKind.PathStroke));

        // Tessellating an interior and tessellating an outline are different work over the same
        // points, which is also why they are two commands rather than one with a flag.
        Assert.Equal([BatchKind.PathFill, BatchKind.PathStroke], batches.Select(static batch => batch.Kind));
    }

    [Fact]
    public void A_clip_breaks_a_batch_and_never_joins_one() {
        var batches = Batch(
            Of(DrawCommandKind.Rectangle),
            Of(DrawCommandKind.ClipPush),
            Of(DrawCommandKind.ClipPop),
            Of(DrawCommandKind.Rectangle)
        );

        // Four batches: the two rectangles cannot merge across a clip that changes what either of
        // them would cover, and the two clips do not merge with each other either — a state change
        // that arrives as part of a draw is one somebody applies once.
        Assert.Equal(
            [BatchKind.Geometry, BatchKind.Clip, BatchKind.Clip, BatchKind.Geometry],
            batches.Select(static batch => batch.Kind)
        );

        Assert.All(batches.Where(static batch => batch.Kind == BatchKind.Clip), static batch =>
            Assert.Equal(1, batch.Count));
    }

    [Fact]
    public void Nothing_to_draw_is_no_batches() {
        Assert.Empty(Batch());
    }

    [Fact]
    public void A_frame_that_drew_the_same_thing_does_no_batching_work() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            box { width: 10px; height: 10px; background-color: #ffffff; }
        """);

        document.Root.Add("box");
        document.Update();
        document.Draw();

        var batched = document.Drawing.Batched;
        Assert.Single(document.Drawing.Batches);

        // Behind the diff rather than beside it: batching walks every command in the interface, and
        // a frame that drew the same thing has the same batches by construction. The cached command
        // buffer the version exists to protect keeps its batches with it.
        Assert.False(document.Draw());
        Assert.Equal(batched, document.Drawing.Batched);
    }

    [Fact]
    public void A_real_document_batches_what_it_can() {
        using var document = new UiDocument(100f, 100f);

        document.Load("""
            root { width: 100px; height: 100px; }
            box { width: 10px; height: 10px; background-color: #ffffff;
                  border-top-width: 1px; border-top-color: #000000; border-style: solid; }
            panel { width: 50px; height: 50px; overflow: hidden; }
        """);

        document.Root.Add("box");
        document.Root.Add("box");

        var panel = document.Root.Add("panel");
        panel.Add("box");

        document.Update();
        document.Draw();

        // Two boxes are four commands and one batch; the panel's clip is its own; the box inside is
        // another; the pop is another. Reached through the whole chain rather than through a
        // hand-built command list, because a batcher tested only against commands nobody emits is a
        // batcher tested against a guess about what the builder does.
        Assert.Equal(
            [BatchKind.Geometry, BatchKind.Clip, BatchKind.Geometry, BatchKind.Clip],
            document.Drawing.Batches.Select(static batch => batch.Kind)
        );

        Assert.Equal(4, document.Drawing.Batches[0].Count);
    }
}
