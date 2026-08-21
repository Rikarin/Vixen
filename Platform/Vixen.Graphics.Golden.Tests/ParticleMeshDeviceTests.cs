// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.Materials;
using Vixen.Rendering.Vfx;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Vixen.Vfx;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     The mesh particle path, end to end: a <see cref="VfxSystem" /> becoming instances of a mesh.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="ParticleRenderFeature" /> could draw this and nothing could describe it.</b>
///         <c>VfxGeometryBuilder.BuildInstances</c> has always produced <c>ParticleInstance</c> — three
///         rows of an affine transform and a colour — and <c>Draw</c> has always bound it as a second
///         vertex buffer at binding 1. What did not exist was anything that could put those two
///         buffers into one pipeline: <see cref="VertexSchema.Layout" /> resolved to a single
///         <see cref="VertexBufferLayout" />, and no shader in <c>Raven/Library</c> declared the four
///         instance inputs. So the feature bound a buffer the pipeline had never heard of — and
///         <c>SetMesh</c>, the call that would have started any of it, had no caller at all.
///     </para>
///     <para>
///         Four separate contracts have to hold at once here, and each fails silently on its own:
///     </para>
///     <list type="bullet">
///         <item><description>
///             the <b>attribute names</b>, because <c>ParticleInstances.Schema</c> matches by name —
///             <c>instanceRow0</c> against a stage declaring <c>row0</c> is a pipeline the driver
///             refuses with a complaint about the layout rather than about the attribute;
///         </description></item>
///         <item><description>
///             the <b>step mode</b>, which is the whole mechanism and is not an error when it is
///             wrong: a transform read per vertex hands every corner of the mesh a different
///             particle's placement, and the effect draws as one smeared shape rather than as two;
///         </description></item>
///         <item><description>
///             the <b>row convention</b> — translation in the <c>w</c> lanes, three rows and no fourth
///             — which read as columns puts every instance somewhere plausible and wrong;
///         </description></item>
///         <item><description>
///             the <b>material</b>, on <see cref="ParticleSpriteDeviceTests" />' terms: <c>Draw</c>
///             skips any object whose material resolves to no variant.
///         </description></item>
///     </list>
///     <para>
///         A picture is the only thing that catches all four.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class ParticleMeshDeviceTests {
    /// <summary>How far in front of the camera the particles sit.</summary>
    const float Depth = -6f;

    /// <summary>How far either side of the middle, in metres.</summary>
    /// <remarks>
    ///     Far enough apart that the two instances do not touch, which is what the run-counting
    ///     assertion rests on, and near enough that both are well inside a 1:1 frustum at
    ///     <see cref="Depth" />.
    /// </remarks>
    const float Spread = 1.5f;

    /// <summary>Half the mesh's own extent, times the size the graph sets.</summary>
    const float Radius = 0.7f;

    /// <summary>What the pass clears to: a blue no additive orange can produce.</summary>
    /// <remarks>
    ///     Load-bearing, on <see cref="ParticleSpriteDeviceTests" />' terms: the blend is additive, so
    ///     a pass that ran and drew nothing leaves the clear untouched and a pass that never ran leaves
    ///     black. Clearing to something is what tells the two apart.
    /// </remarks>
    static Color4 Background => new(0f, 0f, 0.25f, 1f);

    /// <summary>A mesh effect's particles reach the screen as two instances of its mesh.</summary>
    /// <remarks>
    ///     ⚠ <b>Two particles, wide apart, and the assertion is that there are two shapes.</b> One
    ///     instance draws identically whether the stream advances per vertex or per instance — the
    ///     single transform is read either way — so a frame with one particle in it proves the names
    ///     and the row convention and says nothing at all about the rate. Two placed apart is the
    ///     smallest frame in which the wrong step mode looks wrong: every corner of the mesh takes a
    ///     different particle's placement, and what comes out is one shape spanning both positions
    ///     with the middle filled in.
    /// </remarks>
    [Fact]
    public void AMeshEffectDrawsOneInstancePerParticle() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);

            return;
        }

        using var owned = fixture!;
        var image = Render(owned);

        var corner = Pixel(image, 2, 2);

        Assert.True(corner.Z > 0.2f && corner.X < 0.05f, $"the pass did not clear: {corner}");

        var runs = Runs(image, image.Height / 2);

        Assert.True(runs.Count > 0, "nothing was drawn at all");

        // The step-mode assertion, and it is the whole reason this frame holds two particles.
        Assert.True(
            runs.Count == 2,
            $"expected one shape per particle across the middle of the picture and found {runs.Count}: "
            + $"[{string.Join(", ", runs.Select(run => $"{run.Start}..{run.End}"))}]. One wide run is the "
            + "instance stream being read at the vertex rate."
        );

        // Neither of them straddles the middle, which is what "two separate instances" means and what
        // a mis-transposed transform — every particle at the origin — would not produce.
        Assert.True(runs[0].End < image.Width / 2, $"the left instance reaches the middle: {runs[0].End}");
        Assert.True(runs[1].Start > image.Width / 2, $"the right instance reaches the middle: {runs[1].Start}");

        // And the colour the graph set, on the sprite test's terms: more red than green, and clear of
        // the blue the clear contributes through the additive blend. A colour read out of the wrong
        // sixteen bytes of the instance record is one of the transform rows, which is not this.
        var inside = Pixel(image, (runs[1].Start + runs[1].End) / 2, image.Height / 2);

        Assert.True(inside.X > inside.Y * 1.4f, $"the mesh is not the colour the effect set: {inside}");
        Assert.True(inside.X > inside.Z * 2f, $"the mesh is not the colour the effect set: {inside}");
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Draws one frame of one mesh effect and reads the picture back.</summary>
    static Bitmap Render(Fixture fixture) {
        var device = fixture.Device;

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Everything()));

        // ⚠ The engine's own default, for <see cref="ParticleSpriteMaterial.Default" />'s reason: what
        // a game gets is `WorldRenderer.MeshParticleMaterial`, and the way that kind of material failed
        // once was a parameter nobody set — invisible under an additive blend, with every counter
        // reporting a healthy frame.
        var material = ParticleMeshMaterial.Default();
        var effect = effects.Resolve(EffectKey.Of("ParticleMesh", [], material.Composition));

        Assert.NotNull(effect);

        using var allocator = new DescriptorAllocator(device);
        using var system = new RenderSystem();

        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect!.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        var stage = system.AddStage(
            new("Debris", RenderSortMode.BackToFront) {
                Blend = BlendState.Additive,
                DepthStencil = DepthStencilState.Disabled,
                Rasterizer = RasterizerState.TwoSided
            }
        );

        var describer = new EffectPipelineDescriber(device);

        // ⚠ Entry zero here, where a real host has it at `WorldRenderer.MeshParticleLayout`. The schema
        // is the *pair* — the mesh's own format and the per-instance stream beside it — and it is the
        // thing under test: a table holding only `SurfaceVertex.Schema` describes a pipeline with one
        // vertex buffer, against a draw that binds two.
        describer.VertexSchemas.Add(MeshParticleVertices.Schema);

        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };

        var particles = new ParticleRenderFeature {
            Device = device,
            Pipelines = new(device),
            Describer = describer
        };

        particles.Add(materials);
        system.AddFeature(particles);

        var camera = new RenderView("camera") {
            Camera = Camera,
            Stages = stage.Mask,
            Position = Vector3.Zero
        };

        camera.Frustum = new(camera.ViewProjection);
        system.SetViews([camera]);
        particles.View = camera;

        var debris = Effect();

        var id = system.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, Depth), 8f),
                Stages = stage.Mask,
                FeatureIndex = particles.Index
            }
        );

        particles.SetSystem(id, debris);
        materials.Assign(system, id, material);

        var quad = Quad(device);

        // The call this whole file exists for.
        particles.SetMesh(id, quad);

        var pass = new RenderPassRenderer {
            Name = "Debris",
            ClearColour = Background,
            Children = { new SingleStageRenderer { View = camera, Stage = stage, Constants = view } }
        };

        pass.ColourTargets.Add("Display");

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = pass
        };

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();
        var frame = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);

        var picture = fixture.Render(frame.Texture("harness", "Display"));

        // ⚠ Asserted rather than assumed, because each of these is a way the picture comes back as the
        // clear with nothing saying why.
        Assert.Equal(2, particles.LastParticleCount);
        Assert.True(materials.BoundCount > 0, "set 2 was never bound, so every draw was skipped");

        debris.Dispose();
        device.Destroy(quad.VertexBuffer);
        device.Destroy(quad.IndexBuffer);

        return picture;
    }

    /// <summary>The camera the instances are oriented against.</summary>
    static RenderCamera Camera => RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

    /// <summary>A square in the mesh's own space, two units across, as the buffers a draw binds.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Written straight into device buffers rather than through
    ///         <see cref="GeometryResidency" />.</b> What is under test is the vertex layout and the
    ///         shader, and a suballocator between the two would add a staging flush this frame has no
    ///         place to record.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The stride is <see cref="SurfaceVertex" />'s, not a packed three floats</b>, because
    ///         that is what the schema declares — and a buffer whose stride disagrees with the layout
    ///         reads the second vertex out of the middle of the first.
    ///     </para>
    ///     <para>
    ///         In the local XY plane, because <c>BuildInstances</c> maps local x to the camera's across
    ///         vector and local y to the aligned axis — so an XY square faces the camera.
    ///     </para>
    /// </remarks>
    static MeshDraw Quad(VulkanDevice device) {
        SurfaceVertex[] vertices = [Corner(-1f, -1f), Corner(1f, -1f), Corner(1f, 1f), Corner(-1f, 1f)];
        uint[] indices = [0, 1, 2, 0, 2, 3];

        var vertexBuffer = device.CreateBuffer(
            new(
                (long)vertices.Length * SurfaceVertex.SizeInBytes,
                BufferUsage.Vertex,
                MemoryAccess.HostUpload,
                "debris vertices"
            )
        );

        var indexBuffer = device.CreateBuffer(
            new((long)indices.Length * sizeof(uint), BufferUsage.Index, MemoryAccess.HostUpload, "debris indices")
        );

        device.Write(vertexBuffer, 0, MemoryMarshal.AsBytes(vertices.AsSpan()));
        device.Write(indexBuffer, 0, MemoryMarshal.AsBytes(indices.AsSpan()));

        return new() {
            VertexBuffer = vertexBuffer,
            IndexBuffer = indexBuffer,
            IndexFormat = IndexFormat.UInt32,
            Count = indices.Length,
            VertexLayout = 0
        };

        static SurfaceVertex Corner(float x, float y) =>
            new() {
                Position = new(x, y, 0f),
                Normal = new(0f, 0f, 1f),
                Tangent = new(1f, 0f, 0f, 1f),
                TexCoord = new((x + 1f) * 0.5f, (y + 1f) * 0.5f)
            };
    }

    /// <summary>Two orange particles in front of the camera, one either side of the middle.</summary>
    /// <remarks>
    ///     ⚠ <b>Placed rather than spawned into position, and that is not a shortcut.</b> Every
    ///     initializer that scatters is random per particle, so a graph would give this frame two
    ///     particles somewhere in a box — and the assertion is about where each one is. What is under
    ///     test is the drawing, so the simulation is asked for two particles and then told where they
    ///     go.
    /// </remarks>
    static VfxSystem Effect() {
        var graph = VfxCompiledGraph.Compile(
            [VfxSpawner.Burst(2)],
            [
                new(VfxOpcode.SetPosition, new Vector4(0f, 0f, Depth, 0f)),
                new(VfxOpcode.SetSize, new Vector4(Radius, Radius, 0f, 0f)),
                new(VfxOpcode.SetColour, new Vector4(1f, 0.45f, 0.08f, 1f)),
                new(VfxOpcode.SetLifetime, new Vector4(100f, 100f, 0f, 0f))
            ],
            [],
            256,
            VfxRenderer.Instanced()
        );

        var effect = new VfxSystem(graph);

        effect.Step(1f / 60f);

        Assert.Equal(2, effect.Count);

        var positions = effect.Particles.Position;

        positions[0] = new(-Spread, 0f, Depth);
        positions[1] = new(Spread, 0f, Depth);

        return effect;
    }

    /// <summary>The runs of drawn pixels across one row, left to right.</summary>
    /// <remarks>
    ///     Red rather than luminance, because the clear is blue and the particles are orange — so a
    ///     threshold on red separates "the effect drew here" from "the pass cleared here" with no
    ///     dependence on how bright either is.
    /// </remarks>
    static List<(int Start, int End)> Runs(in Bitmap image, int row) {
        var runs = new List<(int Start, int End)>();
        var start = -1;

        for (var x = 0; x < image.Width; x++) {
            var drawn = Pixel(image, x, row).X > 0.2f;

            if (drawn && start < 0) {
                start = x;
            } else if (!drawn && start >= 0) {
                runs.Add((start, x - 1));
                start = -1;
            }
        }

        if (start >= 0) {
            runs.Add((start, image.Width - 1));
        }

        return runs;
    }

    /// <summary>One pixel, as channels in 0..1.</summary>
    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    /// <summary>Skips when there is no device, unless the environment insists on one.</summary>
    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
    }
}
