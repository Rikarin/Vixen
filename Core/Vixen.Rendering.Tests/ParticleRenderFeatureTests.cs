// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Vixen.Vfx;
using Xunit;

namespace Tests;

/// <summary>
///     The second concrete renderable, and the first whose geometry is rebuilt every frame — doc 06
///     § VFX pipeline.
/// </summary>
/// <remarks>
///     Driven through the whole frame into the Null backend, so what is asserted is the command
///     stream rather than an intention. The thing that makes this different from the mesh feature is
///     that nothing exists before <c>Prepare</c> runs: the vertices are made from the particles, and
///     an effect with nothing alive has to produce no draw at all rather than an empty one.
/// </remarks>
public class ParticleRenderFeatureTests : IDisposable {
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
        public required ParticleRenderFeature Particles { get; init; }
        public required MaterialRenderFeature Materials { get; init; }

        public void Dispose() {
            Particles.Dispose();
            System.Dispose();
        }
    }

    Harness Build() {
        var system = new RenderSystem();
        var transparent = system.AddStage(new("Transparent"));

        var particles = new ParticleRenderFeature {
            Device = device,
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };

        particles.Add(materials);
        system.AddFeature(particles);
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
            Particles = particles,
            Materials = materials
        };
    }

    /// <summary>A burst of particles at the origin.</summary>
    static VfxSystem Effect(int count, float lifetime = 100f) {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -1f, -1f, 0f)) { B = new(1f, 1f, 1f, 0f) },
                new(VfxOpcode.SetSize, new Vector4(0.2f, 0.2f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(lifetime, lifetime, 0f, 0f))
            ],
            [],
            1024,
            VfxRenderer.SortedBillboard
        );

        var effect = new VfxSystem(graph);
        effect.Step(1f / 60f);

        return effect;
    }

    static RenderObjectId AddEffect(Harness h, VfxSystem effect, Material material) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, 4f),
                Stages = h.Transparent.Mask,
                FeatureIndex = h.Particles.Index
            }
        );

        h.Particles.SetSystem(id, effect);
        h.Materials.Assign(h.System, id, material);

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
    public void An_effect_draws_six_indices_a_particle() {
        using var h = Build();
        using var effect = Effect(10);

        AddEffect(h, effect, new Material("Particle"));

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        // Two triangles a particle, one instance, and the run reached through the vertex offset
        // rather than through a binding of its own.
        Assert.Equal(60, draw.A);
        Assert.Equal(1, draw.B);
        Assert.Equal(10, h.Particles.LastParticleCount);
    }

    [Fact]
    public void The_geometry_is_bound_once_for_every_effect_in_the_frame() {
        using var h = Build();
        using var first = Effect(4);
        using var second = Effect(6);

        AddEffect(h, first, new Material("Particle"));
        AddEffect(h, second, new Material("Particle"));

        Record(h);

        // Two draws out of one buffer. Every effect is a run of the same vertex buffer at the same
        // offset, so rebinding per effect would be the thing this arrangement exists to avoid.
        Assert.Equal(2, device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).Count);
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));
        Assert.Single(device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer));

        Assert.Equal(10, h.Particles.LastParticleCount);
    }

    [Fact]
    public void The_second_effect_starts_where_the_first_one_ended() {
        using var h = Build();
        using var first = Effect(4);
        using var second = Effect(6);

        var a = AddEffect(h, first, new Material("Particle"));
        var b = AddEffect(h, second, new Material("Particle"));

        h.System.Extract();
        h.System.Cull();
        h.System.Prepare();

        var draws = h.System.Objects.Data.Data(h.Particles.Draws);

        Assert.Equal(0, draws[a.Index].FirstVertex);
        Assert.Equal(4 * 4, draws[b.Index].FirstVertex);
        Assert.Equal(4, draws[a.Index].ParticleCount);
        Assert.Equal(6, draws[b.Index].ParticleCount);
    }

    [Fact]
    public void An_effect_with_nothing_alive_draws_nothing() {
        using var h = Build();
        using var effect = Effect(8);

        effect.Reset();

        AddEffect(h, effect, new Material("Particle"));

        Record(h);

        // Not an empty draw — no draw. A draw of zero indices is a command the driver validates and
        // then does nothing with, which is a cost for an effect that has finished.
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
        Assert.Equal(0, h.Particles.LastParticleCount);
    }

    [Fact]
    public void An_object_with_no_effect_attached_draws_nothing() {
        using var h = Build();

        var id = h.System.Objects.Add(
            new() {
                Bounds = new(Vector3.Zero, 4f),
                Stages = h.Transparent.Mask,
                FeatureIndex = h.Particles.Index
            }
        );

        h.Materials.Assign(h.System, id, new Material("Particle"));

        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }

    [Fact]
    public void The_particle_count_follows_the_effect_between_frames() {
        using var h = Build();

        // A burst that outlives one frame and not many. Resetting would not do here: it puts the
        // effect back to its start, and the burst simply happens again.
        using var effect = Effect(10, lifetime: 0.2f);

        AddEffect(h, effect, new Material("Particle"));

        h.System.Extract();
        h.System.Cull();
        h.System.Prepare();
        Assert.Equal(10, h.Particles.LastParticleCount);

        // The geometry is rebuilt from scratch every frame, which is the whole difference between
        // this feature and the mesh one: last frame's vertices are not this frame's.
        for (var frame = 0; frame < 30; frame++) {
            effect.Step(1f / 60f);
        }

        h.System.Extract();
        h.System.Cull();
        h.System.Prepare();
        Assert.Equal(0, h.Particles.LastParticleCount);
    }

    [Fact]
    public void Detaching_an_effect_stops_it_being_drawn() {
        using var h = Build();
        using var effect = Effect(10);

        var id = AddEffect(h, effect, new Material("Particle"));

        h.Particles.SetSystem(id, null);

        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>A trail: two ribbons of four particles each, placed by hand.</summary>
    static VfxSystem Trail() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(8)],
            [
                new(VfxOpcode.SetPosition, Vector4.Zero),
                new(VfxOpcode.SetSize, new Vector4(0.2f, 0.2f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f)),
                new(VfxOpcode.SetCustom, Vector4.Zero)
            ],
            [],
            64,
            VfxRenderer.Ribbon(0),
            [new("strand", VfxAttributeType.Float)]
        );

        var effect = new VfxSystem(graph);
        effect.Step(1f / 60f);

        var positions = effect.Particles.Position;
        var ages = effect.Particles.Age;
        var strands = effect.Particles.Custom(0);

        for (var strip = 0; strip < 2; strip++) {
            for (var along = 0; along < 4; along++) {
                var index = (strip * 4) + along;

                positions[index] = new(along, strip, 0f);
                strands[index] = strip;
                ages[index] = 4 - along;
            }
        }

        return effect;
    }

    /// <summary>An instanced effect: a mesh drawn once per particle.</summary>
    static VfxSystem Rocks(int count) {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(count)],
            [
                new(VfxOpcode.PositionInBox, new Vector4(-1f, -1f, -1f, 0f)) { B = new(1f, 1f, 1f, 0f) },
                new(VfxOpcode.SetSize, new Vector4(0.3f, 0.3f, 0f, 0f)),
                new(VfxOpcode.SetColour, Vector4.One),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            256,
            VfxRenderer.Instanced()
        );

        var effect = new VfxSystem(graph);
        effect.Step(1f / 60f);

        return effect;
    }

    MeshDraw Rock() {
        var buffer = device.CreateBuffer(new(1024, BufferUsage.Vertex, MemoryAccess.DeviceLocal, "Rock"));
        var index = device.CreateBuffer(new(512, BufferUsage.Index, MemoryAccess.DeviceLocal, "Rock indices"));

        return new() {
            VertexBuffer = buffer,
            IndexBuffer = index,
            IndexFormat = IndexFormat.UInt32,
            Count = 36,
            VertexLayout = 0
        };
    }

    /// <summary>
    ///     A ribbon draws its own indices, because its triangles depend on where each strip ends.
    /// </summary>
    [Fact]
    public void A_ribbon_draws_the_indices_its_strips_needed() {
        using var h = Build();
        using var effect = Trail();

        var id = AddEffect(h, effect, new Material("Ribbon"));

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        // Two strips of four: three lengths each, two triangles a length.
        Assert.Equal(6 * VfxGeometryBuilder.IndicesPerRibbonSegment, draw.A);
        Assert.Equal(1, draw.B);

        var draws = h.System.Objects.Data.Data(h.Particles.Draws);
        Assert.Equal(VfxRendererKind.Ribbon, draws[id.Index].Kind);
        Assert.Equal(0, draws[id.Index].FirstIndex);
    }

    /// <summary>
    ///     Two ribbon effects share the frame's index buffer, the second starting where the first ended.
    /// </summary>
    [Fact]
    public void Two_ribbons_share_one_index_buffer() {
        using var h = Build();
        using var first = Trail();
        using var second = Trail();

        var a = AddEffect(h, first, new Material("Ribbon"));
        var b = AddEffect(h, second, new Material("Ribbon"));

        h.System.Extract();
        h.System.Cull();
        h.System.Prepare();

        var draws = h.System.Objects.Data.Data(h.Particles.Draws);

        Assert.Equal(0, draws[a.Index].FirstIndex);
        Assert.Equal(draws[a.Index].IndexCount, draws[b.Index].FirstIndex);

        // And the vertex runs are separate too, which is what lets one index range name one effect's
        // triangles while the vertex offset picks out its vertices.
        Assert.Equal(0, draws[a.Index].FirstVertex);
        Assert.Equal(8 * VfxGeometryBuilder.VerticesPerRibbonParticle, draws[b.Index].FirstVertex);
    }

    /// <summary>
    ///     An instanced effect is one draw of the mesh, instanced by however many particles are alive.
    /// </summary>
    [Fact]
    public void A_mesh_effect_is_one_instanced_draw() {
        using var h = Build();
        using var effect = Rocks(12);

        var id = AddEffect(h, effect, new Material("Rock"));
        h.Particles.SetMesh(id, Rock());

        Record(h);

        var draw = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));

        // The mesh's index count, not the particles' — the particles are the instance count.
        Assert.Equal(36, draw.A);
        Assert.Equal(12, draw.B);

        // Two vertex bindings: the mesh's geometry and this feature's instance stream.
        Assert.Equal(2, device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer).Count);
    }

    /// <summary>An instanced effect with no mesh attached draws nothing rather than crashing.</summary>
    /// <remarks>
    ///     The mesh arrives by a separate call from the effect, so the two can be set in either order
    ///     or one of them forgotten. A frame between them is normal and has to be quiet.
    /// </remarks>
    [Fact]
    public void A_mesh_effect_with_no_mesh_draws_nothing() {
        using var h = Build();
        using var effect = Rocks(4);

        AddEffect(h, effect, new Material("Rock"));

        Record(h);

        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed));
    }

    /// <summary>
    ///     A frame of nothing but billboards still binds once, which is what the rebinding cost is
    ///     traded against.
    /// </summary>
    [Fact]
    public void Mixing_kinds_rebinds_and_not_mixing_them_does_not() {
        using var h = Build();
        using var quads = Effect(4);
        using var trail = Trail();

        AddEffect(h, quads, new Material("Particle"));
        AddEffect(h, trail, new Material("Particle"));

        Record(h);

        // Two kinds, two sets of bindings — and the two draws that go with them.
        Assert.Equal(2, device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).Count);
        Assert.Equal(2, device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer).Count);
        Assert.Equal(2, device.Recorder.OfKind(RecordedCommandKind.BindIndexBuffer).Count);
    }

    [Fact]
    public void One_effect_on_two_objects_is_expanded_for_each() {
        using var h = Build();
        using var effect = Effect(5);

        AddEffect(h, effect, new Material("Particle"));
        AddEffect(h, effect, new Material("Particle"));

        Record(h);

        // The same VfxSystem shown twice — two draws, and the particles expanded once for each,
        // because the runs are separate even though the simulation is one.
        Assert.Equal(2, device.Recorder!.OfKind(RecordedCommandKind.DrawIndexed).Count);
        Assert.Equal(10, h.Particles.LastParticleCount);
    }
}
