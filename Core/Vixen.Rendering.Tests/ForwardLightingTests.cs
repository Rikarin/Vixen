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
using Vixen.Rendering.Lighting;
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

    DescriptorSetLayoutHandle perDraw;

    const int LightSize = 80;

    /// <summary>
    ///     The per-draw set's shape, as <c>ForwardPlus</c> declares it.
    /// </summary>
    /// <remarks>
    ///     Stated here because the effect this harness fakes has no layouts to take one from. The
    ///     feature no longer invents its own — see <see cref="ForwardLightingRenderFeature.Layout" />
    ///     — and a test that let it would be testing something the engine does not do. All four parts
    ///     matter: a set is compatible only with a layout identically defined, and this feature used to
    ///     get the stages wrong.
    /// </remarks>
    DescriptorSetLayoutHandle PerDraw =>
        perDraw.IsValid
            ? perDraw
            : perDraw = device.CreateDescriptorSetLayout(
                new(
                    DescriptorSetSlot.PerDraw,
                    [new(0, DescriptorKind.DynamicUniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment)],
                    "ForwardPlus.PerDraw"
                )
            );


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
        var lighting = new ForwardLightingRenderFeature { Device = device, Layout = PerDraw };

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

    /// <summary>A colour target to record into, made once so a frame loop creates no resources.</summary>
    TextureViewHandle Target() =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 16, Height = 16, Depth = 1,
                    MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.ColourTarget
                }
            )
        );

    /// <summary>Runs one whole frame and returns the set every draw in it bound.</summary>
    /// <remarks>
    ///     Between <see cref="NullDevice.BeginFrame" /> and <see cref="NullDevice.EndFrame" />
    ///     because the question this answers is about frames in flight, and a test that never opened
    ///     one would be asking it of nothing.
    /// </remarks>
    long Frame(Harness h, TextureViewHandle target) {
        device.BeginFrame();
        device.Recorder!.Clear();

        h.System.Draw();

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
        device.EndFrame();

        var bound = device.Recorder!
            .OfKind(RecordedCommandKind.BindDescriptorSet)
            .Where(command => command.A == (long)DescriptorSetSlot.PerDraw)
            .Select(command => command.B)
            .Distinct()
            .ToArray();

        return Assert.Single(bound);
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The layout the shader reads ----------------------------------------

    /// <summary>
    ///     The GPU record is eighty bytes, and every field is where std140 puts it.
    /// </summary>
    /// <remarks>
    ///     The comment in <c>Lighting.rvn</c> says the field order makes each <c>float3</c> land on a
    ///     sixteen-byte boundary with no padding. This is that claim as an assertion, because the way
    ///     it fails is silent: the shader reads whatever bytes are at the offsets it was compiled for
    ///     and shades with them.
    /// </remarks>
    [Fact]
    public void The_gpu_record_is_eighty_bytes_with_no_padding() =>
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

        // The shadow index, in the first of the two floats that used to be padding — so the tail
        // below is exactly where it was, and no shader compiled against the old record moved.
        Assert.Equal(-1f, At(bytes, 56));
    }

    /// <summary>
    ///     A light nobody shadowed reaches the GPU as a negative index, not as tile zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole reason <see cref="RenderLight.ShadowTile" /> counts from one. A struct cannot
    ///         make its own zero mean something else, so with a zero-based index every light that no
    ///         atlas ever touched — every light in every project that renders no atlas at all — would
    ///         arrive claiming tile zero, and be shadowed by whichever lamp happens to occupy it.
    ///     </para>
    ///     <para>
    ///         That failure has no error in it and no black screen: it is one lamp casting another
    ///         lamp's shadows, which reads as a scene that was lit badly.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_light_no_atlas_touched_is_shadowed_by_nothing() {
        var untouched = RenderLight.Point(Vector3.Zero, 10f, new(1f));
        Assert.Equal(0, untouched.ShadowTile);
        Assert.Equal(-1f, untouched.ToGpu().ShadowIndex);

        // And a packed one is its tile, zero-based, on the other side of the same subtraction.
        var packed = untouched;
        packed.ShadowTile = 7;

        Assert.Equal(6f, packed.ToGpu().ShadowIndex);
    }

    /// <summary>An area light's shape lands in the sixteen bytes the record grew by.</summary>
    /// <remarks>
    ///     The tail is where a tube's axis and a rectangle's width live, and the record went from
    ///     sixty-four bytes to eighty to hold them. Asserted at the offsets rather than by reading the
    ///     fields back, because what has to be right is the byte a shader compiled against
    ///     <c>PunctualLight</c> reads — the C# field could be anywhere.
    /// </remarks>
    [Fact]
    public void An_area_lights_shape_lands_in_the_records_tail() {
        var light = RenderLight.Tube(
            Vector3.Zero,
            new(0f, 0f, 2f),
            halfLength: 3f,
            radius: 0.25f,
            range: 10f,
            new(1f, 1f, 1f)
        );

        Span<byte> bytes = stackalloc byte[LightSize];
        var record = light.ToGpu();
        MemoryMarshal.Write(bytes, in record);

        Assert.Equal(3f, At(bytes, 12));   // kind — Tube
        Assert.Equal(0.25f, At(bytes, 48));  // radius, which a tube uses for its thickness
        Assert.Equal(1f, At(bytes, 72));   // tangent.z — normalised on the way out
        Assert.Equal(3f, At(bytes, 76));   // halfLength
    }

    /// <summary>A rectangle's width axis comes out square to its normal, whatever was authored.</summary>
    /// <remarks>
    ///     The closest-point search treats the normal, the tangent and their cross product as an
    ///     orthonormal basis. An axis a few degrees off makes that basis sheared, and a sheared panel
    ///     lights a room slightly wrongly in a way nobody would think to look for — so it is squared
    ///     up once, here, rather than trusted.
    /// </remarks>
    [Fact]
    public void A_rectangles_axis_is_squared_against_its_normal() {
        var light = RenderLight.Rect(
            Vector3.Zero,
            new(0f, 1f, 0f),
            new(1f, 0.5f, 0f),
            halfWidth: 2f,
            halfHeight: 1f,
            range: 10f,
            new(1f, 1f, 1f)
        ).ToGpu();

        Assert.Equal(0f, Vector3.Dot(light.Tangent, light.Direction), 5);
        Assert.Equal(1f, light.Tangent.Length(), 5);

        // A width axis parallel to the normal has no square part to keep, and still has to come out
        // as *some* usable axis rather than as a zero vector the cross product would collapse.
        var degenerate = RenderLight.Rect(
            Vector3.Zero,
            new(0f, 1f, 0f),
            new(0f, 1f, 0f),
            halfWidth: 2f,
            halfHeight: 1f,
            range: 10f,
            new(1f, 1f, 1f)
        ).ToGpu();

        Assert.Equal(1f, degenerate.Tangent.Length(), 5);
        Assert.Equal(0f, Vector3.Dot(degenerate.Tangent, degenerate.Direction), 5);
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

    /// <summary>
    ///     A flickering lamp reorders the list, and an overflowing list then blinks.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The mechanism behind a bug reported three separate ways</b>, measured rather than
    ///         argued. Two facts compound. <see cref="ForwardLightingRenderFeature" />'s score is
    ///         evaluated at the sphere's near point, so for an object large enough to contain the
    ///         lights — a 64 m floor slab — every distance clamps to zero, the falloff window
    ///         saturates, and the ranking collapses to <em>intensity alone</em>. And a lamp whose
    ///         intensity is animated then swaps rank with its neighbour every frame.
    ///     </para>
    ///     <para>
    ///         Over the budget, that is a light entering and leaving the list — its whole contribution
    ///         appearing and disappearing, which reads as a lamp blinking or as blocks of ground
    ///         flickering, and only while the camera moves, because a temporal resolve averages the
    ///         churn away whenever it can keep its history.
    ///     </para>
    ///     <para>
    ///         Under the budget nothing is dropped, so nothing can churn — which is the whole of the
    ///         fix and also its limit. <see cref="ForwardLightingRenderFeature.Select" /> says the
    ///         general answer is clustered lighting, where there is no per-object budget to overflow.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_list_that_overflows_churns_when_a_lamp_flickers() {
        // The sample's floodlights: bands that overlap once each is swung ±12%.
        float[] baselines = [150000f, 150000f, 140000f, 130000f, 130000f, 110000f, 110000f, 120000f, 125000f];

        Assert.True(Churns(8, baselines), "eight slots for nine lights held still, so nothing here is being measured");
        Assert.False(Churns(baselines.Length + 1, baselines), "a list with room to spare still reordered");
    }

    /// <summary>Whether the chosen set differs between two frames of a flicker.</summary>
    /// <remarks>
    ///     The <em>set</em>, not the order. A list that keeps the same lights in a different order
    ///     shades identically — what changes a pixel is a light leaving the list altogether.
    /// </remarks>
    bool Churns(int budget, float[] baselines) {
        using var h = Build();
        h.Lighting.MaxLightsPerObject = budget;

        // One big object with every light inside its bounds, which is what collapses the score.
        var id = AddMesh(h, Vector3.Zero, radius: 40f);

        for (var i = 0; i < baselines.Length; i++) {
            h.Lighting.Lights.Add(
                RenderLight.Point(new(i * 4f, 2f, 0f), 60f, new(1f), baselines[i])
            );
        }

        var frames = new List<HashSet<float>>();

        // Two moments of a ±12% flicker, each lamp on its own phase — LampFlicker's shape.
        foreach (var clock in (float[])[0f, 0.21f]) {
            for (var i = 0; i < baselines.Length; i++) {
                var light = h.Lighting.Lights[i];
                light.Intensity = baselines[i] * (1f + (0.12f * MathF.Sin((clock * 1.7f * MathF.Tau) + i)));
                h.Lighting.Lights[i] = light;
            }

            Record(h);
            frames.Add([.. h.Lighting.LightsFor(h.System, id).Select(light => light.Position.X)]);
        }

        return !frames[0].SetEquals(frames[1]);
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

    // --- The probe each object picked ---------------------------------------

    /// <summary>
    ///     Two objects in two rooms take two probes, out of one array bound once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What per-object reflection probes are, and the reason they cost nothing extra to bind:
    ///         the cubes are one binding with a count, the volumes are an array beside them, and an
    ///         object picks both with an <c>int</c> in a block it already had. The alternative — a
    ///         descriptor set per probe bound per draw — is a set per object in all but name.
    ///     </para>
    ///     <para>
    ///         It goes in this feature's block because the header already had the room: std140 starts
    ///         the light array on a sixteen-byte boundary, so the count left twelve bytes of padding
    ///         and two of them are these.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_objects_in_two_rooms_take_two_probes() {
        using var h = Build();

        var selector = new ReflectionProbeSelector();

        selector.Probes.Add(new() { Bounds = new(new(-5f, -5f, 5f), new(5f, 5f, 15f)), CapturePosition = new(0f, 0f, 10f) });
        selector.Probes.Add(new() { Bounds = new(new(-5f, -5f, 25f), new(5f, 5f, 35f)), CapturePosition = new(0f, 0f, 30f) });

        h.Lighting.Probes = selector;

        var near = AddMesh(h, new(0f, 0f, 10f));
        var far = AddMesh(h, new(0f, 0f, 30f));

        Record(h);

        Assert.Equal(0, ProbeOf(h, near));
        Assert.Equal(1, ProbeOf(h, far));

        // And both are inside their own probe, so both take it whole.
        Assert.Equal(1f, WeightOf(h, near));
        Assert.Equal(1f, WeightOf(h, far));
    }

    /// <summary>An object in no probe's volume takes none, and the shader's default is no probe.</summary>
    [Fact]
    public void An_object_outside_every_probe_takes_none() {
        using var h = Build();

        var selector = new ReflectionProbeSelector();
        selector.Probes.Add(new() { Bounds = new(new(-5f, -5f, 5f), new(5f, 5f, 15f)), CapturePosition = new(0f, 0f, 10f) });

        h.Lighting.Probes = selector;

        var outside = AddMesh(h, new(0f, 0f, 40f));

        Record(h);

        Assert.Equal(0, ProbeOf(h, outside));
        Assert.Equal(0f, WeightOf(h, outside));
    }

    /// <summary>A probe fading at its edge fades in the block, which is what stops it popping.</summary>
    [Fact]
    public void A_probes_falloff_reaches_the_block() {
        using var h = Build();

        var selector = new ReflectionProbeSelector();

        // The object is on the camera's axis and the probe's far face is what it is near, because an
        // object placed off to the side to reach an edge would be outside the frustum and culled —
        // and a culled object has no block at all, which is a different test failing.
        selector.Probes.Add(
            new() {
                Bounds = new(new(-10f, -10f, 0f), new(10f, 10f, 12f)),
                CapturePosition = new(0f, 0f, 6f),
                BlendDistance = 4f
            }
        );

        h.Lighting.Probes = selector;

        var edge = AddMesh(h, new(0f, 0f, 10f));

        Record(h);

        Assert.Equal(0.5f, WeightOf(h, edge), 4);
    }

    /// <summary>With no selector, nothing is written and the object keeps the shader's default.</summary>
    [Fact]
    public void No_selector_leaves_the_probe_fields_alone() {
        using var h = Build();
        var id = AddMesh(h, new(0f, 0f, 10f));

        Record(h);

        Assert.Equal(0, ProbeOf(h, id));
        Assert.Equal(0f, WeightOf(h, id));
    }

    static int ProbeOf(Harness h, RenderObjectId id) =>
        MemoryMarshal.Read<int>(h.Lighting.Block(h.System, id)[ForwardLightingRenderFeature.ProbeIndexOffset..]);

    static float WeightOf(Harness h, RenderObjectId id) =>
        MemoryMarshal.Read<float>(h.Lighting.Block(h.System, id)[ForwardLightingRenderFeature.ProbeWeightOffset..]);

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

    // --- Growth, and the frames that are still reading -----------------------

    /// <summary>
    ///     Outgrowing the light buffer takes a new set rather than rewriting one a frame in flight
    ///     is reading.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The hazard a persistent set has and this one does not. Growth recreates the buffer, so
    ///         the set has to be made to say something new — and writing that into the set frame
    ///         <c>f - 1</c> was bound to points a descriptor the GPU is reading at a buffer that has
    ///         just been destroyed. Most drivers execute it without a word and the validation layers
    ///         only catch it with synchronisation validation switched on, which is exactly the class
    ///         of bug that ships.
    ///     </para>
    ///     <para>
    ///         What is asserted is the property rather than the mechanism: <strong>no set is written
    ///         twice inside a window of <c>FramesInFlight</c> frames</strong>, growth frame included.
    ///         Every frame writes exactly one set, so a handle appearing twice in such a window
    ///         <em>is</em> a rewrite of something still in flight. The buffer needs no equivalent
    ///         check — <see cref="IGraphicsDevice" /> defers destruction until the frames that could
    ///         reference it have retired, which is the backend's job and not this feature's.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Growing_the_buffer_never_rewrites_a_set_a_frame_in_flight_is_reading() {
        using var h = Build();
        var target = Target();

        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 11f), 60f, new(1f)));

        for (var i = 0; i < 4; i++) {
            AddMesh(h, new(0f, 0f, 10f));
        }

        var seen = new List<long>();

        for (var frame = 0; frame < 4; frame++) {
            seen.Add(Frame(h, target));
        }

        var before = h.Lighting.Buffer;
        var growth = seen.Count;

        // Past the high-water mark the first sizing left, which was room for sixty-four objects.
        for (var i = 0; i < 80; i++) {
            AddMesh(h, new(0f, 0f, 10f));
        }

        for (var frame = 0; frame < 4; frame++) {
            seen.Add(Frame(h, target));
        }

        // The premise: it really did grow, and on the frame the assertions below are about.
        Assert.NotEqual(before, h.Lighting.Buffer);

        var inFlight = seen.GetRange(growth - (device.FramesInFlight - 1), device.FramesInFlight - 1);
        Assert.DoesNotContain(seen[growth], inFlight);

        for (var first = 0; first + device.FramesInFlight <= seen.Count; first++) {
            var window = seen.GetRange(first, device.FramesInFlight);
            Assert.Equal(window.Count, window.Distinct().Count());
        }
    }

    /// <summary>Growth costs no sets and no buffers that are never given back.</summary>
    /// <remarks>
    ///     The other half of recycling. A ring that took a fresh set every frame would be correct and
    ///     would also allocate one per frame for ever, so the leak assertion is what makes "recycled"
    ///     mean something: the count settles at frames-in-flight and growth does not move it.
    /// </remarks>
    [Fact]
    public void Growth_settles_at_frames_in_flight_sets_and_leaks_no_buffers() {
        using var h = Build();
        var target = Target();

        h.Lighting.Lights.Add(RenderLight.Point(new(0f, 0f, 11f), 60f, new(1f)));
        AddMesh(h, new(0f, 0f, 10f));

        for (var frame = 0; frame < 4; frame++) {
            Frame(h, target);
        }

        for (var i = 0; i < 80; i++) {
            AddMesh(h, new(0f, 0f, 10f));
        }

        for (var frame = 0; frame < 4; frame++) {
            Frame(h, target);
        }

        var settled = device.LiveResourceCount;

        for (var frame = 0; frame < 16; frame++) {
            Frame(h, target);
        }

        Assert.Equal(device.FramesInFlight, h.Lighting.SetCount);
        Assert.Equal(settled, device.LiveResourceCount);
    }
}
