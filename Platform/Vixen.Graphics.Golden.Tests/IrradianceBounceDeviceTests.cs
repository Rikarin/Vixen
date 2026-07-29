// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     Doc 19 § L2's bounce: filler B capturing the scene as <c>ForwardPlus</c> shades it, twice.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two things that had never met.</b> <c>IrradianceCaptureDeviceTests</c> renders cubes with
///         a fixture shader that writes a vertex colour, so no probe had ever seen the scene
///         <i>shaded</i>; and <c>IrradianceShadingDeviceTests</c> shades from a field nothing captured.
///         The bounce is what closes the loop between them, and doc 19 § L2 describes it in one line —
///         "two or three passes feeding the previous result back as ambient" — which is a claim about
///         a loop nobody had run.
///     </para>
///     <para>
///         <b>A sunlit floor and a wall the sun cannot reach.</b> The wall's normal is perpendicular
///         to the sun, so it receives no direct light at all — everything it ever holds came off the
///         floor. The first pass leaves it nearly black; that pass's answer goes into the field; the
///         second pass shades the wall with the field as well. Reading the field toward the wall
///         therefore measures the bounce and nothing else.
///     </para>
///     <para>
///         ⚠ <b>Two things had to be right about the geometry before any of this showed a bounce,
///         and getting either wrong reads as the feedback being broken.</b> A single flat floor
///         cannot light itself — every ray leaving it goes up and never returns — so the first scene
///         here produced one pass and then nothing. And a field covers a box and answers nothing
///         outside it, so a wall standing beyond the field receives no indirect light however many
///         passes run.
///     </para>
///     <para>
///         <b>Nothing here is a capture-specific path.</b> The scene is drawn by <c>RenderSystem</c>
///         through <c>MeshRenderFeature</c>, <c>MaterialRenderFeature</c> and
///         <c>ForwardLightingRenderFeature</c> — the same three a frame uses — into six cube views.
///         A probe sees what a camera standing there would see, which is the property the whole
///         filler rests on and the one a fixture shader cannot check.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class IrradianceBounceDeviceTests {
    /// <summary>How bright the sun is.</summary>
    const float Sun = 3f;

    /// <summary>The floor's albedo — three different numbers, so a channel swap is visible.</summary>
    static Vector3 Albedo => new(0.9f, 0.6f, 0.3f);

    /// <summary>Where the floor is, and how far it reaches.</summary>
    const float FloorY = -1.5f;

    const float FloorHalf = 3.5f;

    /// <summary>How high the wall reaches above the floor, and where it stands.</summary>
    const float WallHeight = 4f;

    const float WallX = -3f;

    /// <summary>Whether the frame clusters its lights.</summary>
    /// <remarks>
    ///     False, so the per-draw light block is statically read and set 3 has to be bound — the
    ///     variant that used to fault. See <c>IrradianceShadingDeviceTests.Clustered</c>.
    /// </remarks>
    const bool Clustered = false;

    /// <summary>Where the field is read: a point facing the wall across a gap.</summary>
    static Vector3 Toward => new(0.5f, 0f, 0f);

    /// <summary>
    ///     The second pass adds light the first could not have, and the series contracts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The wall is the measurement, because the sun cannot reach it.</b> Its normal is
    ///         perpendicular to the sun, so its <i>N</i>·<i>L</i> is zero and every photon it holds
    ///         came off the floor. Reading the field toward it separates the bounce from the direct
    ///         term entirely — no fraction to subtract, no tolerance to argue about.
    ///     </para>
    ///     <para>
    ///         <b>Contracting, not monotone, and that is the scheme rather than a defect.</b> Each
    ///         pass re-gathers the whole field from a scene shaded with the previous one, so it is a
    ///         Jacobi iteration and it overshoots before it settles — the run this was written against
    ///         went 0.254, 0.331, 0.324. Asserting a monotone increase would be asserting something
    ///         the method does not promise; what it does promise is that the change shrinks, which is
    ///         what makes three passes worth more than two and thirty worth no more than four.
    ///     </para>
    ///     <para>
    ///         ⚠ No closed form here, deliberately. A cube capture of a finite room projected into
    ///         four coefficients and interpolated is a quadrature of a form factor, and an exact
    ///         answer would need a closed room and an exact projection. The <i>shape</i> of the series
    ///         is exact, and it is what these check.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheSecondPassAddsLightTheFirstCouldNotHave() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var lit = Bake(owned, bounces: 3, indirect: true);
        var direct = Bake(owned, bounces: 3, indirect: false);

        // The first pass reads an empty field whether or not one is composed, so the two runs have to
        // start in the same place. A difference here would mean the composition changed the direct
        // term, which it has no business doing.
        Assert.Equal(direct[0], lit[0], 0.02f);
        Assert.True(lit[0] > 0.05f, $"the first pass saw nothing at all: {lit[0]}");

        // The bounce arrives, and it is not a rounding difference.
        Assert.True(lit[1] > lit[0] * 1.1f, $"the second pass added nothing: {lit[0]} then {lit[1]}");

        // And stays. A loop that fed something back once and then lost it would pass the line above.
        Assert.True(lit[2] > lit[0] * 1.1f, $"the third pass lost what the second gained: {lit[2]}");

        // Contracting. A feedback loop that returned the whole answer rather than the albedo's share
        // of it would change by as much every pass, or more — which is the classic radiosity mistake
        // and looks entirely healthy for two passes.
        Assert.True(
            MathF.Abs(lit[2] - lit[1]) < MathF.Abs(lit[1] - lit[0]) * 0.5f,
            $"the series is not contracting: {lit[0]}, {lit[1]}, {lit[2]}"
        );

        // And flat without it. Everything else about the two runs is identical, so this is what says
        // the growth came from the field rather than from a bake having been run three times.
        Assert.Equal(direct[0], direct[1], 0.01f);
        Assert.Equal(direct[0], direct[2], 0.01f);
    }

    /// <summary>
    ///     <b>And the first pass is the floor's own colour, so the capture really is shaded.</b>
    /// </summary>
    /// <remarks>
    ///     A fixture shader writing a constant would pass every assertion above about the shape of a
    ///     series and none of this. The floor is a rough dielectric under a white sun, so what a probe
    ///     above it receives is tinted by the albedo — three different numbers, in the ratio the
    ///     material was authored with.
    /// </remarks>
    [Fact]
    public void TheCaptureCarriesTheMaterialsOwnColour() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var readings = Bake(owned, bounces: 1, indirect: true, out var field);

        Assert.True(readings[0] > 0.05f, $"the floor was not lit: {readings[0]}");

        var received = field.Irradiance(Vector3.Zero, -Vector3.UnitY);

        Assert.True(received.X > received.Y && received.Y > received.Z, $"the colour came back as {received}");

        // The ratios the material was authored with, which no constant-colour shader reproduces.
        Assert.Equal(Albedo.Y / Albedo.X, received.Y / received.X, 0.05f);
        Assert.Equal(Albedo.Z / Albedo.X, received.Z / received.X, 0.05f);
    }

    // --- The bake -----------------------------------------------------------

    static float[] Bake(Fixture fixture, int bounces, bool indirect) => Bake(fixture, bounces, indirect, out _);

    /// <summary>Bakes a field N times over, and reports what the middle of it holds after each.</summary>
    static float[] Bake(Fixture fixture, int bounces, bool indirect, out IrradianceField baked) {
        var device = fixture.Device;
        var material = Composed();

        material.Parameters.Set(ForwardPlusKeys.UseIrradianceField, indirect);
        material.Parameters.Set(ForwardPlusKeys.UseImageBasedLighting, false);
        material.Parameters.Set(ForwardPlusKeys.UseShadows, false);
        material.Parameters.Set(ForwardPlusKeys.UseReflectionProbe, false);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Everything()));

        var effect = effects.Resolve(Key(material.Composition, indirect));

        Assert.NotNull(effect);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();
        using var scene = new SceneConstants(device) { Descriptors = allocator };

        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect!.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        // ⚠ Two-sided, which a capture stage always wants — see IrradianceCubeCapture's remarks. A
        // probe sees whichever side of a surface faces it, and which side that is depends on where
        // the probe stands rather than on how the mesh was wound.
        var opaque = system.AddStage(new("Opaque") { Rasterizer = RasterizerState.TwoSided });
        var describer = new EffectPipelineDescriber(device);

        describer.VertexLayouts.Add([
            new VertexBufferLayout(
                Vertex.Stride,
                [
                    new(5, VertexFormat.Float32X3, 0),
                    new(6, VertexFormat.Float32X3, 12),
                    new(7, VertexFormat.Float32X4, 24),
                    new(8, VertexFormat.Float32X2, 40)
                ]
            )
        ]);

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = describer };
        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };

        var lighting = new ForwardLightingRenderFeature {
            Device = device,
            Clustered = Clustered,
            Layout = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw],
            Scene = scene.Parameters
        };

        var transforms = new TransformRenderFeature { Device = device, Scene = scene.Parameters };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);

        materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
        materials.PermutationSources[ForwardPlusKeys.UseClusteredLights] = lighting.PermutationKeys[0];

        // ⚠ Six views, made once and re-aimed per probe. A view carries an index the render system
        // assigns in SetViews and every sorted work list is keyed on it, so making new ones per probe
        // would rebuild those lists sixty-four times for a matrix that changed.
        var views = CubeMapping.Faces
            .Select(face => new RenderView($"probe {face}") { Stages = opaque.Mask })
            .ToArray();

        system.SetViews(views);

        var floor = system.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, FloorY + (WallHeight / 2f), 0f), FloorHalf * 3f),
                Stages = opaque.Mask,
                FeatureIndex = meshes.Index
            }
        );

        var room = Vertex.Room;

        system.Objects.Data.Data(meshes.Draws)[floor.Index] = new() {
            VertexBuffer = fixture.Buffer<Vertex>(room, BufferUsage.Vertex),
            IndexBuffer = fixture.Buffer<ushort>([.. Enumerable.Range(0, room.Length).Select(index => (ushort)index)], BufferUsage.Index),
            IndexFormat = IndexFormat.UInt16,
            Count = room.Length,
            InstanceCount = 1
        };

        system.Objects.Data.Data(transforms.World)[floor.Index] = Matrix4x4.Identity;
        materials.Assign(system, floor, material);

        // ⚠ Large enough to hold the room, and that is not a detail. A field covers a box and answers
        // NOTHING outside it — a wall standing beyond the field receives no indirect light however
        // many passes run, so the bounce is exactly zero and reads as the feedback being broken.
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(1));

        field.AllocateAll();

        using var texture = new IrradianceFieldTexture(field);
        var unused = Constants(scene, samplers, device, fixture);

        var cube = new IrradianceCubeCapture(device) {
            Size = 16,
            Range = 100f,
            MinimumDistance = 0.25f,

            // Black, so every photon in the answer came off the floor. A sky would light the probe
            // directly and the bounce would be a small change to a large number.
            Sky = new(0f, 0f, 0f, 1f)
        };

        using var source = new RenderedIrradianceCaptures(device, cube, Draw) {
            Prepare = position => {
                foreach (var face in CubeMapping.Faces) {
                    views[(int)face].Position = position;
                    views[(int)face].ViewProjection = ShadowProjections.Cube(position, face, 100f, 0.01f);
                }

                // Everything a frame does before it records: extract, cull, prepare, sort. Six views
                // at once, which is what makes one command list enough for a whole cube.
                system.Draw();
                allocator.BeginFrame();
                VulkanDiagnostics.Reset();
            },

            // ⚠ Before the submit, not after. See RenderedIrradianceCaptures.Recorded.
            Recorded = () => {
                if (VulkanDiagnostics.ErrorCount > 0) {
                    Assert.Fail(
                        "The capture recorded invalid work, and submitting it would fault the GPU: "
                        + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
                    );
                }
            }
        };

        var filler = new CapturedIrradianceFiller(source);
        var readings = new float[bounces];

        for (var bounce = 0; bounce < bounces; bounce++) {
            // ⚠ Before the fill, and that ordering is the bounce. What the material reads while a
            // pass captures is the previous pass's answer; uploading afterwards would shade this
            // pass with its own half-written result, which is a feedback loop with no defined value.
            Upload(device, texture, scene, unused, bounce == 0);

            Assert.Equal(field.BrickCount, filler.Fill(field));
            Assert.Equal(0, filler.Skipped);

            field.Dilate();
            field.SyncBorders();

            readings[bounce] = field.Irradiance(Toward, -Vector3.UnitX).X;
        }

        Assert.Empty(effects.Misses);
        Assert.True(scene.IsComplete, "set 0 was left incomplete, so nothing the field wrote was bound");
        Assert.True(materials.BoundCount > 0, "set 2 was left incomplete, so the material bound none of it");

        baked = field;
        source.Dispose();

        return readings;

        void Draw(ICommandList commands, CubeFace face, Matrix4x4 _) {
            // ⚠ The formats, and leaving them at the default is a GPU fault rather than an error:
            // a pipeline built for no attachments, used in a pass that has two, is undefined
            // behaviour on every driver here. See IrradianceCubeCapture.Output.
            var context = new RenderDrawContext(commands, effects) {
                Device = device,
                ViewConstants = view,
                SceneConstants = scene,
                Output = IrradianceCubeCapture.Output
            };

            system.Record(views[(int)face], opaque, context);
        }
    }

    /// <summary>Copies the field up and names it into set 0, so the next pass shades with it.</summary>
    /// <remarks>
    ///     Its own submit, because <c>Upload</c> is a buffer-to-texture copy and a copy cannot be
    ///     recorded inside a render pass — and every list the capture opens is six render passes.
    /// </remarks>
    static void Upload(
        VulkanDevice device,
        IrradianceFieldTexture texture,
        SceneConstants scene,
        TextureHandle[] unused,
        bool first
    ) {
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "irradiance upload")) {
            if (first) {
                // The graph is not building this frame, so nobody else moves the two stand-in
                // textures out of UNDEFINED — and a set holding one there is a validation error at
                // submit whether or not the shader samples it.
                commands.Barrier(
                    new(
                        [],
                        [.. unused.Select(handle => new TextureBarrier(handle, ResourceState.Undefined, ResourceState.ShaderRead))]
                    )
                );
            }

            texture.Upload(device, commands);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        texture.Apply(scene.Parameters, $"ForwardPlus.{MaterialCompiler.IrradianceFieldShader}");
    }

    /// <summary>Everything in set 0 that is not the field's, since a set is bound wholly or not at all.</summary>
    static TextureHandle[] Constants(
        SceneConstants scene,
        SamplerCache samplers,
        VulkanDevice device,
        Fixture fixture
    ) {
        // Straight down, because Lighting.Directional takes the direction the light TRAVELS and
        // negates it — so this is a sun overhead and a floor facing up receives all of it.
        scene.Parameters.Set(ForwardPlusKeys.LightDirection, new Vector3(0f, -1f, 0f));
        scene.Parameters.Set(ForwardPlusKeys.LightColor, new Vector3(Sun));
        ClusterGrid.Apply(scene.Parameters, RenderCamera.Default with { AspectRatio = 1f }, "ForwardPlus");

        var flat = Unused(device, fixture, TextureDimension.Texture2D, 1);
        var cube = Unused(device, fixture, TextureDimension.TextureCube, 6);

        scene.Parameters.Set(ForwardPlusKeys.ShadowMap, flat.View);
        scene.Parameters.Set(ForwardPlusKeys.Environment, cube.View);
        scene.Parameters.Set(ForwardPlusKeys.Probes, cube.View);

        // One light nothing points at, and a cluster list of zeros. Created rather than imported,
        // because there is no graph here to provide a transient — and an uninitialised cluster list
        // is a random number of lights per froxel, which is a probe nobody can read.
        scene.Parameters.Set(
            ForwardPlusKeys.LightBuffer,
            fixture.Buffer<PunctualLightData>([default], BufferUsage.Storage)
        );

        scene.Parameters.Set(
            ForwardPlusKeys.Clusters,
            fixture.Buffer<byte>(new byte[ClusterGrid.BufferSize], BufferUsage.Storage)
        );

        scene.Parameters.Set(ForwardPlusKeys.ShadowSampler, samplers.PointClamp);
        scene.Parameters.Set(ForwardPlusKeys.EnvironmentSampler, samplers.LinearClamp);
        scene.Parameters.Set(ForwardPlusKeys.ProbeSampler, samplers.LinearClamp);

        return [flat.Texture, cube.Texture];
    }

    static (TextureHandle Texture, TextureViewHandle View) Unused(
        VulkanDevice device,
        Fixture fixture,
        TextureDimension dimension,
        int layers
    ) {
        var texture = device.CreateTexture(
            new() {
                Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = layers, SampleCount = 1,
                Dimension = dimension,
                Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled, Name = "unused"
            }
        );

        var view = device.CreateTextureView(texture);

        fixture.Owns(() => device.Destroy(texture));

        return (texture, view);
    }

    /// <summary>The variant under test: the field on or off, and every other source of light off.</summary>
    static EffectKey Key(ShaderComposition composition, bool indirect) =>
        EffectKey.Of(
            "ForwardPlus",
            [
                new("ForwardPlus.UseIrradianceField", indirect ? "true" : "false"),
                new("ForwardPlus.UseImageBasedLighting", "false"),
                new("ForwardPlus.UseShadows", "false"),
                new("ForwardPlus.UseReflectionProbe", "false"),
                new("ForwardPlus.UseClusteredLights", Clustered ? "true" : "false")
            ],
            composition
        );

    /// <summary>A rough dielectric, so its response is Lambertian and its albedo is its base colour.</summary>
    static Material Composed() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = Albedo, Metalness = 0f, Roughness = 1f }]
            },
            new Dictionary<string, string> {
                [MaterialCompiler.ForwardIrradianceSlot] = MaterialCompiler.IrradianceFieldShader
            }
        );

        Assert.False(
            compilation.Failed,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic => diagnostic.ToString()))
        );

        return compilation.Material!;
    }

    /// <summary>What the vertex stage reads: position, normal, tangent, texcoord.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex {
        public const int Stride = 48;

        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Texcoord;

        /// <summary>
        ///     An open-topped box: a floor under the probe and four walls around it.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         ⚠ <b>A floor on its own has no bounce, and finding that out is what this shape is
        ///         for.</b> A flat plane cannot light itself — every ray leaving it goes up and never
        ///         comes back — so a scene of one quad produces a first pass and then nothing, however
        ///         correctly the field is fed back. The first attempt here was exactly that, and it
        ///         read as the feedback being broken.
        ///     </para>
        ///     <para>
        ///         Walls are what return the light. The sun lights the floor; the floor lights the
        ///         walls; the walls light the floor and each other. Open at the top because a closed
        ///         box lets no sun in, and doc 19 § L2's closed-box test is about leaks rather than
        ///         about bounces.
        ///     </para>
        /// </remarks>
        public static Vertex[] Room => [
            .. Quad(new(0f, FloorY, 0f), Vector3.UnitY, new(FloorHalf, 0f, 0f), new(0f, 0f, FloorHalf)),

            // One wall, facing the probe across the floor. Its normal is perpendicular to the sun, so
            // NdotL is zero and it receives NO direct light at all — everything it holds came off the
            // floor, which is what makes the growth below unambiguous.
            .. Quad(
                new(WallX, FloorY + WallHeight, 0f),
                Vector3.UnitX,
                new(0f, 0f, FloorHalf),
                new(0f, WallHeight, 0f)
            )
        ];

        /// <summary>Two triangles about a centre, spanning two half-extents.</summary>
        static Vertex[] Quad(Vector3 centre, Vector3 normal, Vector3 right, Vector3 up) => [
            At(centre - right - up, normal, 0f, 0f),
            At(centre + right - up, normal, 1f, 0f),
            At(centre - right + up, normal, 0f, 1f),
            At(centre - right + up, normal, 0f, 1f),
            At(centre + right - up, normal, 1f, 0f),
            At(centre + right + up, normal, 1f, 1f)
        ];

        static Vertex At(Vector3 position, Vector3 normal, float u, float v) =>
            new() {
                Position = position,
                Normal = normal,
                Tangent = new(1f, 0f, 0f, 1f),
                Texcoord = new(u, v)
            };
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
