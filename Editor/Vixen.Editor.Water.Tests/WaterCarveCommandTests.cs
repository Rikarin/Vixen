// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Terrain;
using Vixen.Water;
using Xunit;
using TerrainAsset = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Water.Tests;

/// <summary>
///     The third verb: cutting the bodies into the ground, and hiding what they cut.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>WaterCarveCommand</c> was never constructed by anything but this file's absence.</b>
///         Doc 35 § D5's reserved layer was written and tested in the kernel and no running editor
///         ever reached it — which is also why "Preview the carve" was a flag nothing read: there was
///         never anything in the layer to hide.
///     </para>
///     <para>
///         ⚠ <b>The undo is a regeneration from the old list rather than a stored copy of the
///         layer</b>, so what is asserted is that the ground comes back, not that some bytes were
///         restored. A layer's deltas are sparse chunks over a whole terrain and copying them per
///         entry would put a heightfield on the undo stack for every drag of a width handle.
///     </para>
/// </remarks>
public sealed class WaterCarveCommandTests {
    static TerrainDescription Shape() =>
        TerrainDescription.Default with {
            TileSamples = 128,
            TilesX = 1,
            TilesZ = 1,
            MetresPerQuad = 1f,
            MinHeight = -100f,
            MaxHeight = 100f
        };

    /// <summary>A square lake, three metres deep, sitting on flat ground at ten.</summary>
    static WaterBody Lake(Vector2 low, float side = 24f, float surface = 10f, float depth = 3f) {
        var spline = new Spline(
            Spline.SmoothTangents(
                [
                    new(low.X, surface, low.Y),
                    new(low.X + side, surface, low.Y),
                    new(low.X + side, surface, low.Y + side),
                    new(low.X, surface, low.Y + side)
                ],
                closed: true,
                tension: 1f
            ),
            closed: true
        );

        return new(WaterBodyKind.Lake, spline, defaults: new() { Depth = depth }) {
            SurfaceHeight = surface,
            ShoreFalloff = 2f,
            BedRamp = 4f
        };
    }

    static float HeightAt(TerrainAsset terrain, int x, int z) =>
        terrain.Description.HeightOf(terrain.CompositeAt(x, z));

    /// <summary>Carving cuts the ground, resolves it, and undoing puts it back.</summary>
    [Fact]
    public void A_carve_cuts_the_ground_and_undo_restores_it() {
        using var scene = new Fixture();

        var terrain = new TerrainAsset(Shape(), 10f);
        var untouched = HeightAt(terrain, 52, 52);
        var command = new WaterCarveCommand(terrain, [], [(Lake(new(40f, 40f)), WaterCarveProfile.Default)]);

        scene.Document.Stack.Execute(command);

        Assert.Equal(7f, HeightAt(terrain, 52, 52), 1);
        Assert.Single(command.Current);

        // ⚠ Resolved, not merely invalidated. The kernel flags the tiles it touched and stops there,
        // so a carve that did not resolve is deltas in a layer and a viewport still drawing the old
        // ground — the feature would look like it did nothing.
        Assert.Equal(0, terrain.DirtyTileCount);

        Assert.True(scene.Document.Stack.Undo());

        Assert.Equal(untouched, HeightAt(terrain, 52, 52), 3);
        Assert.Empty(command.Current);
        Assert.Equal(0, terrain.DirtyTileCount);
    }

    /// <summary>Hiding the reserved layer takes the bed back out of the ground.</summary>
    /// <remarks>
    ///     <b>This is what the carve preview <em>is</em></b> — <c>WaterModule.ShowCarve</c> sets this
    ///     flag on the layer <see cref="WaterCarve.LayerOf" /> names and re-resolves, and the ground an
    ///     author sculpted comes back without a single delta being thrown away.
    /// </remarks>
    [Fact]
    public void Hiding_the_reserved_layer_shows_the_ground_the_author_sculpted() {
        using var scene = new Fixture();

        var terrain = new TerrainAsset(Shape(), 10f);
        var untouched = HeightAt(terrain, 52, 52);

        scene.Document.Stack.Execute(
            new WaterCarveCommand(terrain, [], [(Lake(new(40f, 40f)), WaterCarveProfile.Default)])
        );

        var layer = WaterCarve.LayerOf(terrain);

        Assert.NotEqual(untouched, HeightAt(terrain, 52, 52), 3);

        layer.IsVisible = false;
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(untouched, HeightAt(terrain, 52, 52), 3);

        // And the deltas are still there, which is the whole of "non-destructive".
        Assert.False(layer.IsEmpty);

        layer.IsVisible = true;
        terrain.InvalidateAll();
        terrain.Resolve();

        Assert.Equal(7f, HeightAt(terrain, 52, 52), 1);
    }

    /// <summary>A project and a document, for the one thing a command needs: a stack to run on.</summary>
    sealed class Fixture : IDisposable {
        readonly World world;
        readonly EditorProject project;
        readonly string root;

        public Fixture() {
            root = Path.Combine(Path.GetTempPath(), "vixen-water-carve", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));

            project = new(new ProjectPaths(root));
            project.Open();

            world = new("Water carve");
            Document = new(project, world, AssetId.Empty, "Scene");
        }

        public SceneDocument Document { get; }

        public void Dispose() {
            world.Dispose();

            try {
                if (Directory.Exists(root)) {
                    Directory.Delete(root, recursive: true);
                }
            } catch (IOException) {
                // A temporary directory something else still has open.
            }
        }
    }
}
