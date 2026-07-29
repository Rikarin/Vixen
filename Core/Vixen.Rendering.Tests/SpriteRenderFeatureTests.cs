// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Sprites;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The third concrete renderable — doc 06 § Geometry and materials, "sprites, sprite sheets,
///     9-slice".
/// </summary>
/// <remarks>
///     Driven through the whole frame into the Null backend, so what is asserted is the command
///     stream rather than an intention. What makes this different from the particle feature is that
///     the geometry has no camera in it: a sprite is a quad in its own plane, so the expansion is the
///     same for every view and the sprite's place in the world is a matrix somebody else pushes.
/// </remarks>
public class SpriteRenderFeatureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

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
        public required RenderStage Transparent { get; init; }
        public required RenderView Camera { get; init; }
        public required SpriteRenderFeature Sprites { get; init; }
        public required MaterialRenderFeature Materials { get; init; }

        public void Dispose() {
            Sprites.Dispose();
            System.Dispose();
        }
    }

    Harness Build() {
        var system = new RenderSystem();

        // ⚠ ByGroup, which is what a 2D stage has to be. Sprites are blended quads all the same
        // distance away, so a depth sort would order them by object id.
        var transparent = system.AddStage(new("Transparent", RenderSortMode.ByGroup));

        var sprites = new SpriteRenderFeature {
            Device = device,
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };

        sprites.Add(materials);
        system.AddFeature(sprites);
        effects.AddProvider(new AlwaysCompiles());

        var view = Matrix4x4.LookAt(new(0f, 0f, -10f), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var camera = new RenderView("camera") {
            Stages = transparent.Mask,
            Position = new(0f, 0f, -10f),
            Camera = new(new(0f, 0f, -10f), Vector3.UnitZ, Vector3.UnitY, MathF.PI / 3f, 1f, 0.1f, 1000f),
            Frustum = new(view * projection)
        };

        system.SetViews([camera]);

        return new() {
            System = system,
            Transparent = transparent,
            Camera = camera,
            Sprites = sprites,
            Materials = materials
        };
    }

    static Sprite Square(NineSlice border = default) =>
        new() {
            Name = "square",
            Region = new(0f, 0f, 64f, 64f),
            TextureSize = new(128, 128),
            Border = border,
            PixelsPerUnit = 64f
        };

    static RenderObjectId Add(Harness h, Sprite? sprite, SpriteAppearance appearance = default) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, 4f),
                Stages = h.Transparent.Mask,
                FeatureIndex = h.Sprites.Index
            }
        );

        h.Sprites.SetSprite(id, sprite);
        h.Sprites.SetAppearance(id, appearance);
        h.Materials.Assign(h.System, id, new Material("Sprite"));

        return id;
    }

    ICommandList Record(Harness h) {
        h.System.Draw();

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
        list.BeginRenderPass(new([new(target)], name: "Transparent"));

        h.System.Record(
            h.Camera,
            h.Transparent,
            new(list, effects) { Device = device, Output = new([PixelFormat.Rgba8UNorm]) }
        );

        list.EndRenderPass();
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        return list;
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_sprite_draws_six_indices() {
        using var h = Build();

        Add(h, Square());

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(6, draw.A);
        Assert.Equal(1, draw.B);
        Assert.Equal(1, h.Sprites.LastQuadCount);
    }

    [Fact]
    public void A_bordered_sprite_draws_nine_quads_in_one_call() {
        using var h = Build();

        Add(h, Square(NineSlice.Uniform(16f)), new() { Size = new(4f, 2f) });

        Record(h);

        // The whole point of expanding on the CPU: a nine-slice is more geometry, not more draws.
        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        Assert.Equal(9 * 6, draw.A);
        Assert.Equal(9, h.Sprites.LastQuadCount);
    }

    [Fact]
    public void The_geometry_is_bound_once_for_every_sprite_in_the_frame() {
        using var h = Build();

        Add(h, Square());
        Add(h, Square(NineSlice.Uniform(16f)), new() { Size = new(4f, 2f) });

        Record(h);

        // Two draws out of one buffer, each reaching its own run through the draw call's vertex
        // offset — which is what makes a scene of sprites a list of draws and not a list of
        // bindings.
        Assert.Equal(2, device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).Count);
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));
        Assert.Equal(10, h.Sprites.LastQuadCount);
    }

    [Fact]
    public void The_second_sprites_run_starts_where_the_first_one_ended() {
        using var h = Build();

        Add(h, Square(NineSlice.Uniform(16f)), new() { Size = new(4f, 2f) });
        var second = Add(h, Square());

        Record(h);

        // The vertex offset is the fourth argument, and it is how one buffer holds the whole frame.
        var draws = device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed);

        Assert.Equal(0, draws[0].D);
        Assert.Equal(9 * 4, draws[1].D);
        Assert.Equal(9 * 4, h.System.Objects.Data.Data(h.Sprites.Draws)[second.Index].FirstVertex);
    }

    [Fact]
    public void An_object_with_no_sprite_draws_nothing() {
        using var h = Build();

        // ⚠ The bias on the sprite index, checked: a per-object array arrives zeroed, so an object
        // that has never been given a sprite must not draw the first sprite somebody registered.
        Add(h, Square());
        Add(h, null);

        Record(h);

        Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
        Assert.Equal(1, h.Sprites.LastQuadCount);
    }

    [Fact]
    public void Detaching_a_sprite_stops_it_being_drawn() {
        using var h = Build();

        var id = Add(h, Square());

        Assert.Equal("square", h.Sprites.SpriteOf(id)?.Name);

        h.Sprites.SetSprite(id, null);

        Record(h);

        Assert.Null(h.Sprites.SpriteOf(id));
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }

    [Fact]
    public void One_sprite_shown_by_many_objects_is_registered_once() {
        using var h = Build();
        var sprite = Square();

        Add(h, sprite);
        Add(h, sprite);
        Add(h, Square());

        Record(h);

        // Two hundred pieces of grass showing one sprite should cost two hundred integers. The third
        // is an equal *value* rather than the same reference — Sprite is a record, so it collapses
        // too, which is the behaviour that makes an importer handing out fresh instances harmless.
        Assert.Single(h.Sprites.Known);
        Assert.Equal(3, device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).Count);
    }

    [Fact]
    public void The_appearance_decides_the_painting_order() {
        using var h = Build();

        var background = Add(h, Square(), new() { SortGroup = 10 });
        var foreground = Add(h, Square(), new() { SortGroup = 20 });

        Record(h);

        // ⚠ What replaces depth in 2D. Both quads are the same distance from the camera, so the only
        // thing that can decide which is in front is the number an artist set — and the stage sorts
        // ByGroup so that the sort reads it rather than a distance that is a tie.
        var draws = device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed);

        Assert.Equal(2, draws.Count);
        Assert.Equal(0, draws[0].D);
        Assert.Equal(4, draws[1].D);

        // Which is to say: the object added first is expanded first, and the sort puts it first
        // because its group is lower. Raising it flips the pair.
        h.Sprites.SetAppearance(background, new() { SortGroup = 30 });

        device.Recorder.Clear();
        Record(h);

        draws = device.Recorder.OfKind(RecordedCommandKind.DrawIndexed);

        Assert.Equal(4, draws[0].D);
        Assert.Equal(0, draws[1].D);
        Assert.NotEqual(foreground, background);
    }

    [Fact]
    public void A_frame_with_no_sprites_records_nothing_at_all() {
        using var h = Build();

        Record(h);

        // Not an empty draw and not a binding: a feature with nothing to draw should cost a frame
        // nothing, which is also what makes the buffer-validity guard reachable.
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Equal(0, h.Sprites.LastQuadCount);
    }

    [Fact]
    public void A_feature_that_has_not_been_added_to_a_system_says_so() {
        using var sprites = new SpriteRenderFeature();

        // The message is the point: the alternative is a NullReferenceException from inside the
        // store, which says nothing about the order the caller got wrong.
        var error = Assert.Throws<InvalidOperationException>(() => sprites.SetSprite(new(0), Square()));

        Assert.Contains("RenderSystem", error.Message, StringComparison.Ordinal);
    }
}
