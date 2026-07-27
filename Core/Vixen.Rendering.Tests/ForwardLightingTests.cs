// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
///     Per-object light lists — docs/plan/06 § Lighting, the forward path.
/// </summary>
/// <remarks>
///     Two things have to hold at once and neither is visible from the other side. The
///     <em>selection</em> has to pick the lights that actually reach an object and rank them the way
///     the shader would; the <em>layout</em> has to put them where <c>PunctualLight</c> in
///     <c>Lighting.rvn</c> says they are. Get the first wrong and the scene is subtly mis-lit; get the
///     second wrong and it is lit by whatever the bytes happen to mean.
/// </remarks>
public class ForwardLightingTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();

    const int LightSize = 64;

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
        public required ForwardLightingRenderFeature Lighting { get; init; }
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
        var lighting = new ForwardLightingRenderFeature { Device = device };

        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);

        effects.AddProvider(new AlwaysCompiles());

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var camera = new RenderView("camera") {
            Stages = opaque.Mask,
            Position = Vector3.Zero,
            Frustum = new(view * projection)
        };

        system.SetViews([camera]);

        return new() {
            System = system,
            Opaque = opaque,
            Camera = camera,
            Meshes = meshes,
            Materials = materials,
            Lighting = lighting,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    static RenderObjectId AddMesh(Harness h, Vector3 at, float radius = 1f) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(at, radius),
                Stages = h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, new("Lit"));
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
        list.BeginRenderPass(new([new(target)], name: "Opaque"));

        h.System.Record(
            h.Camera,
            h.Opaque,
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

    // --- The layout the shader reads ----------------------------------------

    /// <summary>
    ///     The GPU record is sixty-four bytes, and every field is where std140 puts it.
    /// </summary>
    /// <remarks>
    ///     The comment in <c>Lighting.rvn</c> says the field order makes each <c>float3</c> land on a
    ///     sixteen-byte boundary with no padding. This is that claim as an assertion, because the way
    ///     it fails is silent: the shader reads whatever bytes are at the offsets it was compiled for
    ///     and shades with them.
    /// </remarks>
    [Fact]
    public void The_gpu_record_is_sixty_four_bytes_with_no_padding() =>
        Assert.Equal(LightSize, Unsafe.SizeOf<PunctualLightData>());

    /// <summary>Each field is at the byte offset the shader's struct declares.</summary>
    [Fact]
    public void Every_field_lands_where_the_shader_expects_it() {
        var light = new RenderLight {
            Kind = LightKind.Spot,
            Position = new(1f, 2f, 3f),
            Direction = new(0f, 0f, 1f),
            Colour = new(4f, 5f, 6f),
            Intensity = 1f,
            Range = 7f,
            Radius = 8f,
            InnerAngle = 0f,
            OuterAngle = 0f
        };

        Span<byte> bytes = stackalloc byte[LightSize];
        var record = light.ToGpu();
        MemoryMarshal.Write(bytes, in record);

        Assert.Equal(1f, At(bytes, 0));   // position.x
        Assert.Equal(3f, At(bytes, 8));   // position.z
        Assert.Equal(2f, At(bytes, 12));  // kind — Spot
        Assert.Equal(4f, At(bytes, 16));  // colour.r
        Assert.Equal(7f, At(bytes, 28));  // range
        Assert.Equal(1f, At(bytes, 40));  // direction.z
        Assert.Equal(1f, At(bytes, 44));  // cos(0) — inner
        Assert.Equal(8f, At(bytes, 48));  // radius
        Assert.Equal(1f, At(bytes, 52));  // cos(0) — outer
    }

    static float At(ReadOnlySpan<byte> bytes, int offset) => MemoryMarshal.Read<float>(bytes[offset..]);

    /// <summary>Colour and intensity are multiplied together before they reach the GPU.</summary>
    /// <remarks>
    ///     Once per light per frame rather than once per light per fragment. It also means the shader
    ///     has one number to read where the author had two, which is why the record has no separate
    ///     intensity field to get out of step with the colour.
    /// </remarks>
    [Fact]
    public void Intensity_is_folded_into_the_colour() {
        var light = RenderLight.Point(Vector3.Zero, 10f, new(0.5f, 0.25f, 0.125f), 4f).ToGpu();

        Assert.Equal(2f, light.Colour.X);
        Assert.Equal(1f, light.Colour.Y);
        Assert.Equal(0.5f, light.Colour.Z);
    }

    // --- Selection ----------------------------------------------------------

    [Fact]
    public void A_light_within_range_reaches_the_object() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));
        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 12f), 5f, new(1f)));

        Record(h);

        Assert.Single(h.Lighting.LightsFor(h.System, id));
    }

    [Fact]
    public void A_light_out_of_range_does_not() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));
        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 40f), 5f, new(1f)));

        Record(h);

        Assert.Empty(h.Lighting.LightsFor(h.System, id));
    }

    /// <summary>Range is measured to the object's surface, not to its centre.</summary>
    /// <remarks>
    ///     A ten-metre sphere whose centre is eleven metres from a five-metre lamp is one metre from
    ///     it. Measuring to the centre would leave a building unlit by the light on its own wall.
    /// </remarks>
    [Fact]
    public void Range_is_measured_to_the_surface_rather_than_the_centre() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 20f), radius: 10f);
        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 31f), 5f, new(1f)));

        Record(h);

        Assert.Single(h.Lighting.LightsFor(h.System, id));
    }

    /// <summary>
    ///     When more lights reach an object than the block holds, the brightest survive.
    /// </summary>
    /// <remarks>
    ///     Twelve lights of increasing brightness at the same distance, into a block of four. The
    ///     answer is the last four, in descending order — the ranking is what makes a fixed-size list
    ///     an approximation rather than an arbitrary truncation.
    /// </remarks>
    [Fact]
    public void The_brightest_lights_win_when_more_reach_an_object_than_fit() {
        using var h = Build();
        h.Lighting.MaxLightsPerObject = 4;

        var id = AddMesh(h, new(0f, 0f, 10f));

        for (var i = 1; i <= 12; i++) {
            h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 12f), 5f, new(1f), i));
        }

        Record(h);

        var chosen = h.Lighting.LightsFor(h.System, id).Select(light => light.Colour.X).ToArray();

        Assert.Equal([12f, 11f, 10f, 9f], chosen);
    }

    /// <summary>The directional light is the sun, and is not in anybody's list.</summary>
    /// <remarks>
    ///     It reaches everything, so putting it in every list would be paying list traversal for
    ///     something that is always there. <c>ForwardPlus.rvn</c> takes it as its own uniform for the
    ///     same reason.
    /// </remarks>
    [Fact]
    public void The_directional_light_becomes_the_sun_rather_than_a_list_entry() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));

        h.Lighting.Lights.Add(RenderLight.Directional(new(0f, -1f, 0f), new(1f), 3f));
        h.Lighting.Lights.Add(RenderLight.Directional(new(1f, 0f, 0f), new(1f), 9f));

        Record(h);

        Assert.Empty(h.Lighting.LightsFor(h.System, id));
        Assert.Equal(9f, h.Lighting.Sun!.Value.Intensity);
    }

    /// <summary>A spot light pointing the other way does not reach an object inside its range.</summary>
    [Fact]
    public void A_spot_pointing_away_does_not_reach() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));

        h.Lighting.Lights.Add(
            RenderLight.Spot(
                new(0f, 0f, 12f),
                new(0f, 0f, 1f),
                range: 20f,
                innerAngle: 0.2f,
                outerAngle: 0.3f,
                new Color3(1f)
            )
        );

        Record(h);

        Assert.Empty(h.Lighting.LightsFor(h.System, id));
    }

    [Fact]
    public void A_spot_pointing_at_the_object_does() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));

        h.Lighting.Lights.Add(
            RenderLight.Spot(
                new(0f, 0f, 12f),
                new(0f, 0f, -1f),
                range: 20f,
                innerAngle: 0.2f,
                outerAngle: 0.3f,
                new Color3(1f)
            )
        );

        Record(h);

        Assert.Single(h.Lighting.LightsFor(h.System, id));
    }

    /// <summary>
    ///     A light behind the camera still lights what is in front of it.
    /// </summary>
    /// <remarks>
    ///     The reason lights are never tested against the view frustum. It looks like a missed
    ///     optimisation and is a correctness requirement — a lamp over the player's shoulder lights
    ///     the whole room, and culling it would darken exactly the objects that are on screen.
    /// </remarks>
    [Fact]
    public void A_light_outside_the_frustum_still_lights_what_is_inside_it() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 4f));

        // Behind the camera, which sits at the origin looking down +Z.
        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, -2f), 20f, new(1f)));

        Record(h);

        Assert.Single(h.Lighting.LightsFor(h.System, id));
    }

    /// <summary>A culled object costs no light selection and no block.</summary>
    [Fact]
    public void A_culled_object_is_never_lit() {
        using var h = Build();
        AddMesh(h, new(0f, 0f, -50f));
        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, -50f), 20f, new(1f)));

        Record(h);

        Assert.Equal(0, h.Lighting.UsedBytes);
    }

    // --- The block, and how it is bound -------------------------------------

    /// <summary>The block says how many lights it holds, ahead of the lights themselves.</summary>
    /// <remarks>
    ///     At offset zero and sixteen bytes wide, because std140 starts an array of structures on a
    ///     sixteen-byte boundary whatever precedes it. Writing the array at four would put every
    ///     light one slot early.
    /// </remarks>
    [Fact]
    public void The_block_declares_its_count_before_the_lights() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));

        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 11f), 5f, new(1f)));
        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 12f), 5f, new(1f)));

        Record(h);

        var block = h.Lighting.Block(h.System, id);

        Assert.Equal(2u, MemoryMarshal.Read<uint>(block));
        Assert.Equal(16, ForwardLightingRenderFeature.HeaderSize);
        Assert.Equal(11f, At(block, ForwardLightingRenderFeature.HeaderSize + 8));
    }

    /// <summary>Every object's block starts at a multiple of the device's offset alignment.</summary>
    /// <remarks>
    ///     A dynamic uniform offset that is not a multiple of
    ///     <c>minUniformBufferOffsetAlignment</c> is rejected outright by Vulkan. Deriving the stride
    ///     from the alignment rather than from the block's size is what makes that impossible to get
    ///     wrong per object.
    /// </remarks>
    [Fact]
    public void Blocks_are_aligned_for_a_dynamic_offset() {
        using var h = Build();
        h.Lighting.OffsetAlignment = 256;

        for (var i = 0; i < 4; i++) {
            AddMesh(h, new(0f, 0f, 10f + i));
        }

        Record(h);

        Assert.Equal(0, h.Lighting.BlockStride % 256);
        Assert.True(h.Lighting.BlockStride >= ForwardLightingRenderFeature.HeaderSize + (8 * LightSize));
        Assert.Equal(4 * h.Lighting.BlockStride, h.Lighting.UsedBytes);
    }

    /// <summary>
    ///     One descriptor set and one buffer between every object, reached by a per-draw offset.
    /// </summary>
    /// <remarks>
    ///     The claim <see cref="DescriptorKind.DynamicUniformBuffer" /> exists to make: four objects
    ///     produce four binds of the <em>same</em> set at four offsets, not four sets. Allocating a
    ///     set per draw is the most common reason a Vulkan renderer ends up slower than the D3D11 one
    ///     it replaced.
    /// </remarks>
    [Fact]
    public void Every_draw_binds_one_shared_set_at_its_own_offset() {
        using var h = Build();
        var ids = new List<RenderObjectId>();

        for (var i = 0; i < 4; i++) {
            ids.Add(AddMesh(h, new(0f, 0f, 10f + i)));
        }

        Record(h);

        var binds = device.Recorder!
            .OfKind(RecordedCommandKind.BindDescriptorSet)
            .Where(command => command.A == (long)DescriptorSetSlot.PerDraw)
            .ToArray();

        Assert.Equal(4, binds.Length);
        Assert.Single(binds.Select(bind => bind.B).Distinct());

        var offsets = ids
            .Select(id => h.System.Objects.Data.Data(h.Lighting.Assignments)[id.Index].Offset)
            .ToArray();

        Assert.Equal(4, offsets.Distinct().Count());
    }
}
