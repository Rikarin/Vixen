// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Xunit;

namespace Vixen.Ai.Diagnostics.Tests;

/// <summary>
///     doc 37 § Part 5's query preview — Unreal's testing pawn, minus the pawn — asserted with no
///     window.
/// </summary>
public class QueryPreviewTests {
    [Fact]
    public void EverySurvivingPointGetsAMarkerAndTheWinnerGetsARing() {
        var results = Run(out var world);

        using (world) {
            var draw = new DebugDraw();
            var drawn = QueryPreview.Draw(draw, results);

            Assert.Equal(results.Count, drawn);
            Assert.True(draw.Count > 0, "the preview drew no geometry.");
            Assert.Equal(results.Surviving, draw.TextCount);
        }
    }

    /// <summary>
    ///     ⚠ Filtered points are drawn crossed out rather than dropped. "Why is my query returning
    ///     nothing" is answered by seeing where the points were and that every one of them was
    ///     rejected — a preview that only drew survivors would answer it with an empty screen.
    /// </summary>
    [Fact]
    public void TheRejectedPointsAreDrawnAndCanBeTurnedOff() {
        var results = Run(out var world);

        using (world) {
            Assert.True(results.Count > results.Surviving, "nothing was rejected, so there is nothing to check.");

            var withRejects = new DebugDraw();
            var without = new DebugDraw();

            QueryPreview.Draw(withRejects, results);
            QueryPreview.Draw(without, results, new() { Rejected = false, Scores = true, Winner = true });

            Assert.True(withRejects.Count > without.Count, "turning the rejects off drew the same amount.");
        }
    }

    /// <summary>⚠ <c>default</c> is the quiet style and <c>new()</c> is the usual one.</summary>
    [Fact]
    public void AZeroedStyleStillDrawsBecauseTheDefaultIsSubstituted() {
        var results = Run(out var world);

        using (world) {
            var draw = new DebugDraw();

            Assert.Equal(results.Count, QueryPreview.Draw(draw, results, default));
            Assert.Equal(QueryPreviewStyle.DefaultSize, QueryPreviewStyle.Default.Extent);
            Assert.Equal(QueryPreviewStyle.DefaultSize, default(QueryPreviewStyle).Extent);
            Assert.False(default(QueryPreviewStyle).Rejected);
        }
    }

    [Fact]
    public void AnEmptyRunDrawsNothing() {
        var draw = new DebugDraw();

        Assert.Equal(0, QueryPreview.Draw(draw, new QueryResults()));
        Assert.Equal(0, draw.Count);
    }

    /// <summary>A ring around the origin, half of it filtered out and the rest scored by distance.</summary>
    static QueryResults Run(out World world) {
        world = new World("query-preview");

        var context = new AgentContext(world, new(3, 1, world.Id), new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

        var query = new EnvironmentQuery(
            Symbol.Intern("ring"),
            [QueryGenerators.Circle(5f, 8)],
            new QueryTest(QueryTests.From("east", static (in AgentContext c, in QueryOrigin o, in QueryPoint p) => p.Position.X)) {
                Purpose = QueryTestPurpose.Filter,
                Floor = 0f
            },
            new QueryTest(QueryTests.Distance(QueryDistanceFrom.Querier)) { Maximum = 10f }
        );

        var results = new QueryResults { Detailed = true };

        query.Run(in context, new(Vector3.Zero, Vector3.Zero), results);

        return results;
    }
}
