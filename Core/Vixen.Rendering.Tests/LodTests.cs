// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Level of detail — docs/plan/06 § Geometry and materials.
/// </summary>
/// <remarks>
///     A LOD group is several render objects, and this feature does nothing but decide which of them
///     a view gets to see. That makes the whole of it assertable through the visibility bitset, with
///     no meshes involved: the question "which level is showing" is exactly "which bits survived".
/// </remarks>
public class LodTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    // --- Fixture ------------------------------------------------------------

    static Effect Compiled(EffectKey key) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ]
        };

    sealed class AlwaysCompiles : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderView Camera { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required LodRenderFeature Lods { get; init; }
        public required BufferHandle Vertices { get; init; }

        public void Dispose() => System.Dispose();
    }

    Harness Build() {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };
        var lods = new LodRenderFeature();

        meshes.Add(materials);
        meshes.Add(lods);
        system.AddFeature(meshes);
        effects.AddProvider(new AlwaysCompiles());

        var fov = MathF.PI / 3f;
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(fov, 1f, 0.1f, 10000f);

        var camera = new RenderView("camera") {
            Stages = opaque.Mask,
            Position = Vector3.Zero,
            Frustum = new(view * projection),
            ScreenHeightScale = 1f / MathF.Tan(fov * 0.5f)
        };

        system.SetViews([camera]);

        return new() {
            System = system,
            Opaque = opaque,
            Camera = camera,
            Meshes = meshes,
            Materials = materials,
            Lods = lods,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    /// <summary>Adds one level of a group, all levels sharing a position and a size.</summary>
    static RenderObjectId AddLevel(Harness h, Vector3 at, int group, int level, float radius = 1f) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(at, radius), Stages = h.Opaque.Mask, FeatureIndex = h.Meshes.Index }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("Lit"));
        h.Lods.Assign(h.System, id, group, level);
        return id;
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Selection ----------------------------------------------------------

    /// <summary>
    ///     A group near the camera shows its most detailed level and hides the others.
    /// </summary>
    /// <remarks>
    ///     Visibility, not a draw-call filter. Selection happens after culling and before sorting, so
    ///     a level that is not chosen never reaches a stage's list — which is what makes it cost
    ///     nothing downstream rather than being skipped in the draw loop.
    /// </remarks>
    [Fact]
    public void The_nearest_group_shows_its_finest_level() {
        using var h = Build();
        var group = h.Lods.Add([0.5f, 0.1f]);

        var near = new Vector3(0f, 0f, -2f);
        var fine = AddLevel(h, near, group, 0);
        var middle = AddLevel(h, near, group, 1);
        var coarse = AddLevel(h, near, group, 2);

        h.System.Draw();

        Assert.True(h.System.Visibility.IsVisible(h.Camera.Index, fine));
        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, middle));
        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, coarse));
        Assert.Equal(0, h.Lods.LevelOf(group, h.Camera.Index));
    }

    /// <summary>A group far enough away shows its coarsest level.</summary>
    [Fact]
    public void A_distant_group_shows_its_coarsest_level() {
        using var h = Build();
        var group = h.Lods.Add([0.5f, 0.1f]);

        var far = new Vector3(0f, 0f, -500f);
        var fine = AddLevel(h, far, group, 0);
        var coarse = AddLevel(h, far, group, 2);

        h.System.Draw();

        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, fine));
        Assert.True(h.System.Visibility.IsVisible(h.Camera.Index, coarse));
        Assert.Equal(2, h.Lods.LevelOf(group, h.Camera.Index));
    }

    /// <summary>Moving away walks the group down its levels in order.</summary>
    [Fact]
    public void Moving_away_walks_down_the_levels() {
        using var h = Build();
        var group = h.Lods.Add([0.5f, 0.1f]);

        var levels = new[] {
            AddLevel(h, Vector3.Zero, group, 0),
            AddLevel(h, Vector3.Zero, group, 1),
            AddLevel(h, Vector3.Zero, group, 2)
        };

        var seen = new List<int>();

        foreach (var distance in (ReadOnlySpan<float>)[2f, 8f, 40f]) {
            foreach (var id in levels) {
                h.System.Objects[id].Bounds = new(new Vector3(0f, 0f, -distance), 1f);
            }

            h.System.Draw();
            seen.Add(h.Lods.LevelOf(group, h.Camera.Index));
        }

        Assert.Equal([0, 1, 2], seen);
    }

    // --- Hysteresis ---------------------------------------------------------

    /// <summary>
    ///     A group sitting exactly on a threshold does not change level every frame.
    /// </summary>
    /// <remarks>
    ///     The difference between LOD that works and LOD that flickers. A level change is a different
    ///     mesh and usually a different silhouette, so an object drifting across a boundary swapping
    ///     back and forth is far more visible than the detail the switch was protecting. Ten frames
    ///     of a camera breathing across the boundary, and the level is decided once.
    /// </remarks>
    [Fact]
    public void A_group_on_a_threshold_does_not_flicker() {
        using var h = Build();
        var group = h.Lods.Add([0.5f]);

        var levels = new[] { AddLevel(h, Vector3.Zero, group, 0), AddLevel(h, Vector3.Zero, group, 1) };

        // The distance at which the screen height is exactly the threshold.
        var boundary = h.Camera.ScreenHeightScale / 0.5f;
        var seen = new HashSet<int>();

        for (var frame = 0; frame < 10; frame++) {
            var distance = boundary + (frame % 2 == 0 ? -0.001f : 0.001f);

            foreach (var id in levels) {
                h.System.Objects[id].Bounds = new(new Vector3(0f, 0f, -distance), 1f);
            }

            h.System.Draw();
            seen.Add(h.Lods.LevelOf(group, h.Camera.Index));
        }

        Assert.Single(seen);
    }

    /// <summary>Hysteresis holds the level, it does not freeze it.</summary>
    /// <remarks>
    ///     The other direction, and the reason the previous test means something: a feature that
    ///     never changed level would pass that one and never show a coarse mesh at all.
    /// </remarks>
    [Fact]
    public void A_clear_move_past_the_threshold_does_change_level() {
        using var h = Build();
        var group = h.Lods.Add([0.5f]);

        var levels = new[] { AddLevel(h, Vector3.Zero, group, 0), AddLevel(h, Vector3.Zero, group, 1) };
        var boundary = h.Camera.ScreenHeightScale / 0.5f;

        Move(h, levels, boundary * 0.5f);
        Assert.Equal(0, h.Lods.LevelOf(group, h.Camera.Index));

        Move(h, levels, boundary * 2f);
        Assert.Equal(1, h.Lods.LevelOf(group, h.Camera.Index));
    }

    static void Move(Harness h, RenderObjectId[] levels, float distance) {
        foreach (var id in levels) {
            h.System.Objects[id].Bounds = new(new Vector3(0f, 0f, -distance), 1f);
        }

        h.System.Draw();
    }

    // --- Per view -----------------------------------------------------------

    /// <summary>
    ///     A view that does not do screen-size work sees every level.
    /// </summary>
    /// <remarks>
    ///     What a shadow cascade wants, and the reason `ScreenHeightScale` is zero by default:
    ///     drawing a shadow from a different mesh than its caster makes the shadow stop matching the
    ///     object, and nobody authoring LOD thresholds was thinking about the sun.
    /// </remarks>
    [Fact]
    public void A_view_with_no_screen_scale_keeps_every_level() {
        using var h = Build();
        var group = h.Lods.Add([0.5f, 0.1f]);

        var far = new Vector3(0f, 0f, -500f);
        var fine = AddLevel(h, far, group, 0);
        var coarse = AddLevel(h, far, group, 2);

        var shadow = new RenderView("cascade") {
            Stages = h.Opaque.Mask,
            Position = Vector3.Zero,
            Frustum = h.Camera.Frustum
        };

        h.System.SetViews([h.Camera, shadow]);
        h.System.Draw();

        Assert.True(h.System.Visibility.IsVisible(shadow.Index, fine));
        Assert.True(h.System.Visibility.IsVisible(shadow.Index, coarse));

        // And the camera still chose one, so the two views disagree — which is the point.
        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, fine));
    }

    /// <summary>An object in no group is never hidden.</summary>
    [Fact]
    public void An_object_with_no_group_is_left_alone() {
        using var h = Build();
        h.Lods.Add([0.5f]);

        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, -500f), 1f),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Draw();

        Assert.True(h.System.Visibility.IsVisible(h.Camera.Index, id));
    }

    /// <summary>A culled group costs no selection, and stays culled.</summary>
    [Fact]
    public void A_culled_group_stays_culled() {
        using var h = Build();
        var group = h.Lods.Add([0.5f]);

        // Behind the camera.
        var fine = AddLevel(h, new(0f, 0f, 20f), group, 0);

        h.System.Draw();

        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, fine));
    }

    // --- Authoring ----------------------------------------------------------

    /// <summary>Thresholds must descend, and a group that gets them backwards is refused.</summary>
    /// <remarks>
    ///     An ascending list would select the coarsest level up close, which looks like a bug in the
    ///     renderer rather than in the group — so it is caught where it is written.
    /// </remarks>
    [Fact]
    public void Ascending_thresholds_are_refused() {
        using var h = Build();

        var thrown = Assert.Throws<ArgumentException>(() => h.Lods.Add([0.1f, 0.5f]));

        Assert.Contains("descend", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_has_one_more_level_than_it_has_thresholds() {
        using var h = Build();
        var group = h.Lods.Add([0.5f, 0.1f]);

        Assert.Equal(3, h.Lods.Groups[group].LevelCount);
    }

    // --- Cross-fade ---------------------------------------------------------

    /// <summary>
    ///     During a fade both levels are visible, and their weights sum to one.
    /// </summary>
    /// <remarks>
    ///     Summing to one is what makes a dithered fade look like one object rather than two: a
    ///     material discards a fraction of its pixels by the weight, so the two levels' surviving
    ///     pixels tile the silhouette exactly once. Dither rather than blending, because two
    ///     translucent copies of one object write depth twice and sort against each other.
    /// </remarks>
    [Fact]
    public void Both_levels_are_visible_during_a_fade_and_their_weights_sum_to_one() {
        using var h = Build();
        h.Lods.CrossFadeDuration = 1f;

        var group = h.Lods.Add([0.5f]);
        var fine = AddLevel(h, Vector3.Zero, group, 0);
        var coarse = AddLevel(h, Vector3.Zero, group, 1);
        var boundary = h.Camera.ScreenHeightScale / 0.5f;

        Move(h, [fine, coarse], boundary * 0.5f);
        Assert.Equal(0, h.Lods.LevelOf(group, h.Camera.Index));

        h.Lods.DeltaTime = 0.25f;
        Move(h, [fine, coarse], boundary * 2f);

        Assert.Equal(1, h.Lods.LevelOf(group, h.Camera.Index));
        Assert.Equal(0, h.Lods.FadingFrom(group, h.Camera.Index));

        Assert.True(h.System.Visibility.IsVisible(h.Camera.Index, fine));
        Assert.True(h.System.Visibility.IsVisible(h.Camera.Index, coarse));

        var into = h.Lods.FadeOf(group, h.Camera.Index, 1);
        var outOf = h.Lods.FadeOf(group, h.Camera.Index, 0);

        Assert.Equal(1f, into + outOf, 5);
    }

    /// <summary>The fade runs to completion and then the old level goes.</summary>
    [Fact]
    public void A_fade_finishes_and_leaves_one_level() {
        using var h = Build();
        h.Lods.CrossFadeDuration = 1f;

        var group = h.Lods.Add([0.5f]);
        var fine = AddLevel(h, Vector3.Zero, group, 0);
        var coarse = AddLevel(h, Vector3.Zero, group, 1);
        var boundary = h.Camera.ScreenHeightScale / 0.5f;

        Move(h, [fine, coarse], boundary * 0.5f);

        h.Lods.DeltaTime = 0.4f;
        Move(h, [fine, coarse], boundary * 2f);

        for (var frame = 0; frame < 3; frame++) {
            Move(h, [fine, coarse], boundary * 2f);
        }

        Assert.Equal(-1, h.Lods.FadingFrom(group, h.Camera.Index));
        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, fine));
        Assert.True(h.System.Visibility.IsVisible(h.Camera.Index, coarse));
        Assert.Equal(1f, h.Lods.FadeOf(group, h.Camera.Index, 1));
    }

    /// <summary>With no duration the swap is instant and nothing fades.</summary>
    /// <remarks>
    ///     The default, and not timidity: a fade doubles the draws for every object crossing a
    ///     threshold, and a project whose levels are close enough that hysteresis already hides the
    ///     switch should not pay for it.
    /// </remarks>
    [Fact]
    public void With_no_duration_the_swap_is_instant() {
        using var h = Build();

        var group = h.Lods.Add([0.5f]);
        var fine = AddLevel(h, Vector3.Zero, group, 0);
        var coarse = AddLevel(h, Vector3.Zero, group, 1);
        var boundary = h.Camera.ScreenHeightScale / 0.5f;

        Move(h, [fine, coarse], boundary * 0.5f);
        Move(h, [fine, coarse], boundary * 2f);

        Assert.Equal(-1, h.Lods.FadingFrom(group, h.Camera.Index));
        Assert.False(h.System.Visibility.IsVisible(h.Camera.Index, fine));
        Assert.Equal(1f, h.Lods.FadeOf(group, h.Camera.Index, 1));
    }

    /// <summary>
    ///     A fade turning round mid-way fades out where it was heading, not where it started.
    /// </summary>
    /// <remarks>
    ///     A camera swinging past a threshold and back would otherwise pay the whole duration twice
    ///     and spend it showing the level it is no longer going to.
    /// </remarks>
    [Fact]
    public void An_interrupted_fade_turns_round_rather_than_finishing() {
        using var h = Build();
        h.Lods.CrossFadeDuration = 1f;

        var group = h.Lods.Add([0.5f]);
        var fine = AddLevel(h, Vector3.Zero, group, 0);
        var coarse = AddLevel(h, Vector3.Zero, group, 1);
        var boundary = h.Camera.ScreenHeightScale / 0.5f;

        Move(h, [fine, coarse], boundary * 0.5f);

        h.Lods.DeltaTime = 0.25f;
        Move(h, [fine, coarse], boundary * 2f);
        Move(h, [fine, coarse], boundary * 0.5f);

        Assert.Equal(0, h.Lods.LevelOf(group, h.Camera.Index));
        Assert.Equal(1, h.Lods.FadingFrom(group, h.Camera.Index));
    }

    /// <summary>Only a fading object pushes a weight.</summary>
    /// <remarks>
    ///     Everything else is fully visible, and pushing a constant of 1 for it would be a per-draw
    ///     cost for the objects and the frames that are not fading — which is nearly all of them.
    /// </remarks>
    [Fact]
    public void Only_a_fading_object_pushes_a_weight() {
        using var h = Build();
        h.Lods.CrossFadeDuration = 1f;

        var group = h.Lods.Add([0.5f]);
        var fine = AddLevel(h, Vector3.Zero, group, 0);
        var coarse = AddLevel(h, Vector3.Zero, group, 1);
        var boundary = h.Camera.ScreenHeightScale / 0.5f;

        Move(h, [fine, coarse], boundary * 0.5f);
        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.PushConstants));

        h.Lods.DeltaTime = 0.25f;
        Move(h, [fine, coarse], boundary * 2f);
        Record(h);

        // Both levels drawn, both pushing four bytes at the LOD offset.
        var pushes = device.Recorder.OfKind(RecordedCommandKind.PushConstants).ToArray();

        Assert.Equal(2, pushes.Length);
        Assert.All(pushes, push => Assert.Equal((68, 4), (push.B, push.C)));
    }

    void Record(Harness h) {
        var target = device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 16, Height = 16, Depth = 1,
                    MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.ColourTarget
                }
            )
        );

        var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)], name: "Opaque"));

        h.System.Record(
            h.Camera,
            h.Opaque,
            new(list, effects) { Device = device, Output = new([PixelFormat.Rgba8UNorm]) }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }
}
