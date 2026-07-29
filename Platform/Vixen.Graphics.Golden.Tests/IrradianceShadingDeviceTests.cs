// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Features;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.ShaderCompiler;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A material lit by the irradiance field — doc 19 § L2 reaching a surface.
/// </summary>
/// <remarks>
///     <para>
///         <b>The last hop, and the one nothing had run.</b> The field is filled, repaired, uploaded and
///         sampled, and <c>IndirectDiffuse</c> draws it into a buffer — but a buffer is not a lit scene.
///         This is <c>ForwardPlus</c> composing the same field into its own ambient term, which is what
///         makes indirect light something a surface actually receives rather than something a debug view
///         can show.
///     </para>
///     <para>
///         <b>The scene is chosen so the answer is a product of two numbers.</b> Image-based lighting
///         off, no shadows, no reflection probe, no punctual lights, and a field filled from a uniform
///         sky of radiance <i>L</i>. A uniform environment lights every surface with exactly <i>L</i>
///         whichever way it faces, so a Lambertian quad of albedo <i>C</i> comes back as
///         <i>C</i> × <i>L</i> — per channel, which is what makes a transposed or scaled coefficient
///         visible rather than plausible.
///     </para>
///     <para>
///         <b>The π is the thing this test is really for.</b> The field stores irradiance divided by π —
///         the number to multiply by albedo — and <c>Ibl.Diffuse</c> takes plain irradiance and divides
///         by π itself. Handing one to the other unscaled is a scene lit three and an eighth times too
///         dim, which is dark enough to be mistaken for a tuning problem and bright enough not to be
///         mistaken for a bug. A closed form per channel is the only thing that catches it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public class IrradianceShadingDeviceTests {
    /// <summary>Where the quad sits.</summary>
    const float QuadZ = -6f;

    const float QuadHalf = 3f;

    /// <summary>How bright the sky is, and therefore half of every pixel's answer.</summary>
    const float Radiance = 0.5f;

    /// <summary>The quad's albedo — three different numbers, so a channel swap is visible.</summary>
    static Vector3 Albedo => new(0.8f, 0.4f, 0.2f);

    /// <summary>A blue no shading in this fixture can produce, so a pass that never ran is visible.</summary>
    static Color4 Background => new(0f, 0f, 0.25f, 1f);

    static RenderCamera Camera => RenderCamera.Default with { Position = Vector3.Zero, AspectRatio = 1f };

    /// <summary>
    ///     Whether the frame clusters its lights.
    /// </summary>
    /// <remarks>
    ///     <b>False, and that is the interesting half.</b> The non-clustered variant is the one that
    ///     statically reads the per-draw light block, so it is the only one that requires set 3 to be
    ///     bound — and set 3 is where the lighting feature's own layout used to disagree with the
    ///     pipeline's about both the descriptor kind and the stages. The clustered variant never
    ///     touches it, which is why every device test drawing this pass passed while that was broken.
    /// </remarks>
    const bool Clustered = false;

    /// <summary>The variant under test: the field on, and every other source of light off.</summary>
    static EffectKey Key(ShaderComposition composition) =>
        EffectKey.Of(
            "ForwardPlus",
            [
                new("ForwardPlus.UseIrradianceField", "true"),
                new("ForwardPlus.UseImageBasedLighting", "false"),
                new("ForwardPlus.UseShadows", "false"),
                new("ForwardPlus.UseReflectionProbe", "false"),
                new("ForwardPlus.UseClusteredLights", Clustered ? "true" : "false")
            ],
            composition
        );

    /// <summary>
    ///     The variant compiles, and its per-frame set is the field's.
    /// </summary>
    /// <remarks>
    ///     A variant nobody has asked the compiler for is a variant nobody knows compiles — and what it
    ///     binds decides what a frame has to supply. The four pool volumes and the index have to be in
    ///     set 0 for the field to be a per-frame thing rather than a per-material one, which is the
    ///     whole reason the slot is composed by the project.
    /// </remarks>
    [Fact]
    public void TheFieldVariantCompilesAndBindsThePoolPerFrame() {
        var material = Composed();
        var data = RavenEffects.Everything().TryGet(Key(material.Composition));

        Assert.NotNull(data);

        var frame = data!.Bindings
            .Where(binding => binding.Set == DescriptorSetSlot.PerFrame)
            .Select(binding => binding.Name)
            .ToArray();

        foreach (var name in new[] { "irradianceL0", "irradianceL1R", "irradianceL1G", "irradianceL1B" }) {
            Assert.Contains($"{MaterialCompiler.IrradianceFieldShader}.{name}", frame);
        }

        Assert.Contains($"{MaterialCompiler.IrradianceFieldShader}.irradianceIndirection", frame);
        Assert.Contains($"{MaterialCompiler.IrradianceFieldShader}.irradiancePointSampler", frame);

        // And the names the renderer writes are the names the variant declares. This is the pair that
        // fails silently: a prefix spelled two ways binds nothing and lights nothing, and the picture is
        // the same one a field that found no light produces.
        var written = Names();

        foreach (var name in frame) {
            Assert.True(
                !name.StartsWith(MaterialCompiler.IrradianceFieldShader, StringComparison.Ordinal)
                || written.Contains($"ForwardPlus.{name}"),
                $"the variant declares '{name}' and IrradianceFieldTexture.Apply writes no such name"
            );
        }
    }

    /// <summary>Which half of doc 19 § L2 fills the field this frame reads.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Both, separately, because until now each had only ever been checked against the
    ///         other's absence.</b> <c>IrradianceFillDeviceTests</c> dispatches the fill and reads the
    ///         pool back; this file shades from a field the CPU filled. Neither had ever run the
    ///         renderer's own device path — the <c>PassKind.Compute</c> branch, the pool created as a
    ///         storage image, the upload that carries the index volume and nothing else — so the two
    ///         verified halves had never actually met.
    ///     </para>
    ///     <para>
    ///         Same scene, same closed form, one property different. What the pair says is that a probe
    ///         a compute shader wrote is a probe a material can read, which is the claim doc 19 § 3
    ///         makes when it says nothing above the storage knows which filler ran.
    ///     </para>
    /// </remarks>
    public enum Fills {
        /// <summary>Traced on the CPU and copied up.</summary>
        Cpu,

        /// <summary>Dispatched into the pool, and nothing copied up but the indirection.</summary>
        Device
    }

    /// <summary>
    ///     A surface under a uniform sky comes back as its own albedo times that sky.
    /// </summary>
    /// <remarks>
    ///     Both fillers, and the same two numbers out of each. A tolerance wide enough to accept one
    ///     and not the other would be a tolerance measuring the filler rather than the shading, and the
    ///     dispatch agrees with the reference filler to a ten-thousandth — see
    ///     <see cref="IrradianceFillDeviceTests" />.
    /// </remarks>
    [Theory]
    [InlineData(Fills.Cpu)]
    [InlineData(Fills.Device)]
    public void ASurfaceIsLitByTheFieldItStandsIn(Fills fills) {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, lit: true, fills);

        // The corner the quad does not cover is the clear, so the pass ran at all.
        var corner = Pixel(image, 2, 2);

        Assert.True(corner.Z > 0.2f && corner.X < 0.05f, $"the pass did not clear: {corner}");

        var centre = Pixel(image, image.Width / 2, image.Height / 2);

        // C × L, per channel. Tolerant of an 8-bit target and of the filler's sixty-four rays, and
        // nothing like tolerant enough to accept a missing or doubled π.
        Assert.Equal(Albedo.X * Radiance, centre.X, 0.02f);
        Assert.Equal(Albedo.Y * Radiance, centre.Y, 0.02f);
        Assert.Equal(Albedo.Z * Radiance, centre.Z, 0.02f);
    }

    /// <summary>
    ///     <b>And with the permutation off, the same frame is unlit.</b>
    /// </summary>
    /// <remarks>
    ///     What says the light in the picture above came from the field rather than from anything else
    ///     the pass does. Every other source is switched off in both frames, so the only difference is
    ///     the flag — and a fixture where the answer does not depend on it is a fixture measuring
    ///     something else.
    /// </remarks>
    [Fact]
    public void WithoutTheFlagTheSameFrameIsDark() {
        if (!Fixture.TryOpen(out var fixture, out var reason)) {
            Skip(reason);
            return;
        }

        using var owned = fixture!;
        var image = Render(owned, lit: false);
        var centre = Pixel(image, image.Width / 2, image.Height / 2);

        Assert.True(centre.X < 0.02f, $"something other than the field lit the quad: {centre}");
    }

    // --- The frame ----------------------------------------------------------

    /// <summary>Draws one forward frame with the field composed into it.</summary>
    static Bitmap Render(Fixture fixture, bool lit, Fills fills = Fills.Cpu) {
        var device = fixture.Device;
        var material = Composed();

        material.Parameters.Set(ForwardPlusKeys.UseIrradianceField, lit);
        material.Parameters.Set(ForwardPlusKeys.UseImageBasedLighting, false);
        material.Parameters.Set(ForwardPlusKeys.UseShadows, false);
        material.Parameters.Set(ForwardPlusKeys.UseReflectionProbe, false);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Everything()));

        var effect = effects.Resolve(Key(material.Composition));

        Assert.NotNull(effect);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        using var view = new ViewConstants(device) {
            Descriptors = allocator,
            Layout = effect!.SetLayouts[(int)DescriptorSetSlot.PerView]
        };

        using var scene = new SceneConstants(device) { Descriptors = allocator };
        var opaque = system.AddStage(new("Opaque"));
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
        // Its layout is the effect's, which is what makes the non-clustered path drawable at all —
        // see ForwardLightingRenderFeature.Layout.
        var lighting = new ForwardLightingRenderFeature {
            Device = device,
            Clustered = Clustered,
            Layout = effect.SetLayouts[(int)DescriptorSetSlot.PerDraw]
        };
        var transforms = new TransformRenderFeature { Device = device, Scene = scene.Parameters };

        meshes.Add(transforms);
        meshes.Add(materials);
        meshes.Add(lighting);
        system.AddFeature(meshes);

        materials.PermutationKeys["ForwardPlus"] = ForwardPlusKeys.UsedPermutationKeys;
        materials.PermutationSources[ForwardPlusKeys.UseClusteredLights] = lighting.PermutationKeys[0];
        lighting.Scene = scene.Parameters;

        var camera = new RenderView("camera") { Camera = Camera, Stages = opaque.Mask };

        camera.Frustum = new(camera.ViewProjection);
        system.SetViews([camera]);

        var quad = system.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, QuadZ), QuadHalf * 1.5f),
                Stages = opaque.Mask,
                FeatureIndex = meshes.Index
            }
        );

        system.Objects.Data.Data(meshes.Draws)[quad.Index] = new() {
            VertexBuffer = fixture.Buffer<Vertex>(Vertex.Quad, BufferUsage.Vertex),
            IndexBuffer = fixture.Buffer<ushort>([0, 1, 2, 2, 1, 3], BufferUsage.Index),
            IndexFormat = IndexFormat.UInt16,
            Count = 6,
            InstanceCount = 1
        };

        system.Objects.Data.Data(transforms.World)[quad.Index] = Matrix4x4.Identity;
        materials.Assign(system, quad, material);

        // The field, filled once and whole: a uniform sky, so what the surface receives has a closed
        // form and the round robin has nothing to converge toward.
        var field = new IrradianceField(new BoundingBox(new(-12f), new(12f)), new(2));

        field.AllocateAll();

        // ⚠ One or the other, never both — see IrradianceFieldRenderer.DeviceFiller. The device one
        // resolves its own variant, `IrradianceFill` composed with `NoDistanceField`, out of the same
        // effect system the material came from; `RavenEffects.Everything()` is what makes that possible
        // without a second provider.
        using var dispatch = fills is Fills.Device
            ? new IrradianceFieldFill(device) {
                Effects = effects,
                Pipelines = new ComputePipelineCache(device),
                Descriptors = allocator,
                SkyColour = new(Radiance)
            }
            : null;

        using var probes = new IrradianceFieldRenderer {
            Name = "IrradianceField",
            Field = field,
            Filler = fills is Fills.Device ? null : new TracedIrradianceFiller(new EmptyWorld(), new UniformSky(Radiance)),
            DeviceFiller = dispatch,
            SceneConstants = scene,
            Device = device,
            Budget = field.BrickCount,

            // ⚠ The forward pass rather than the screen-space one, and this is the line that makes the
            // whole fixture work: the names are qualified by the pass that reads them, so a field bound
            // for `IndirectDiffuse` is invisible to `ForwardPlus` and vice versa.
            Passes = { "ForwardPlus" }
        };

        probes.Passes.Remove("IndirectDiffuse");

        var unused = Fill(scene, samplers, device, fixture);

        var pass = new RenderPassRenderer {
            Name = "Forward",
            ClearColour = Background,
            SceneConstants = scene,
            Children = { new SingleStageRenderer { View = camera, Stage = opaque, Constants = view } }
        };

        pass.ColourTargets.Add("Display");
        pass.SceneBuffers["lightBuffer"] = "SceneLights";
        pass.SceneBuffers["clusters"] = "Clusters";

        var display = fixture.Owned("Display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { probes, pass } }
        };

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        // One light nothing points at, and a cluster list of zeros. Imported rather than declared,
        // because a transient the graph provides holds whatever that memory held — and an uninitialised
        // cluster list is a random number of lights per froxel, which is a picture nobody can read.
        var lights = fixture.Buffer<PunctualLightData>([default], BufferUsage.Storage);
        var clusters = fixture.Buffer<byte>(new byte[ClusterGrid.BufferSize], BufferUsage.Storage);

        compositor.BufferImports["SceneLights"] = new(
            lights,
            new(Marshal.SizeOf<PunctualLightData>(), BufferUsage.Storage, MemoryAccess.HostUpload, "SceneLights"),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        compositor.BufferImports["Clusters"] = new(
            clusters,
            new(ClusterGrid.BufferSize, BufferUsage.Storage, MemoryAccess.HostUpload, "Clusters"),
            ResourceState.ShaderRead,
            ResourceState.ShaderRead
        );

        allocator.BeginFrame();

        var frame = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);

        // The graph knows nothing about the textures set 0 had to be given, so nobody else can move
        // them out of UNDEFINED — and a set holding one there is a validation error at submit whether
        // or not the shader samples it.
        var picture = fixture.Render(
            frame.Texture("harness", "Display"),
            list => list.Barrier(
                new(
                    [],
                    [.. unused.Select(texture => new TextureBarrier(texture, ResourceState.Undefined, ResourceState.ShaderRead))]
                )
            )
        );

        Assert.True(scene.IsComplete, "set 0 was left incomplete, so the frame bound none of it");
        Assert.True(materials.BoundCount > 0, "set 2 was left incomplete, so the material bound none of it");

        // ⚠ Before the pixels. A dispatch that carried a reason is a field nothing filled, and the dark
        // quad that produces is the same picture a wrong π or an unbound pool draws — this is the only
        // assertion that tells the three apart.
        Assert.Null(dispatch?.Skipped);
        Assert.Equal(field.BrickCount, probes.Filled);

        return picture;
    }

    /// <summary>Everything in set 0 that is not the field's, since a set is bound wholly or not at all.</summary>
    /// <returns>The textures it created, which still have to be moved out of UNDEFINED.</returns>
    static TextureHandle[] Fill(SceneConstants scene, SamplerCache samplers, IGraphicsDevice device, Fixture fixture) {
        // No sun and no ambient intensity change. Every photon in the picture came out of the field.
        scene.Parameters.Set(ForwardPlusKeys.LightDirection, Vector3.Zero);
        scene.Parameters.Set(ForwardPlusKeys.LightColor, Vector3.Zero);
        ClusterGrid.Apply(scene.Parameters, Camera, "ForwardPlus");

        var flat = Flat(device, fixture);
        var cube = Cube(device, fixture);

        scene.Parameters.Set(ForwardPlusKeys.ShadowMap, flat.View);
        scene.Parameters.Set(ForwardPlusKeys.Environment, cube.View);
        scene.Parameters.Set(ForwardPlusKeys.Probes, cube.View);

        scene.Parameters.Set(ForwardPlusKeys.ShadowSampler, samplers.PointClamp);
        scene.Parameters.Set(ForwardPlusKeys.EnvironmentSampler, samplers.LinearClamp);
        scene.Parameters.Set(ForwardPlusKeys.ProbeSampler, samplers.LinearClamp);

        return [flat.Texture, cube.Texture];
    }

    /// <summary>The names <c>IrradianceFieldTexture</c> writes for the forward pass.</summary>
    static HashSet<string> Names() {
        var parameters = new ParameterCollection();
        var field = new IrradianceField(new BoundingBox(new(-1f), new(1f)), new(1));

        new IrradianceFieldTexture(field).Apply(
            parameters,
            $"ForwardPlus.{MaterialCompiler.IrradianceFieldShader}"
        );

        return [.. parameters.Keys.Select(key => key.Name)];
    }

    static (TextureHandle Texture, TextureViewHandle View) Flat(IGraphicsDevice device, Fixture fixture) {
        var texture = device.CreateTexture(
            new() {
                Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                Dimension = TextureDimension.Texture2D,
                Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled, Name = "unused"
            }
        );

        var view = device.CreateTextureView(texture);

        fixture.Owns(() => device.Destroy(texture));

        return (texture, view);
    }

    static (TextureHandle Texture, TextureViewHandle View) Cube(IGraphicsDevice device, Fixture fixture) {
        var texture = device.CreateTexture(
            new() {
                Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 6, SampleCount = 1,
                Dimension = TextureDimension.TextureCube,
                Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled, Name = "unused"
            }
        );

        var view = device.CreateTextureView(texture);

        fixture.Owns(() => device.Destroy(texture));

        return (texture, view);
    }

    static Vector3 Pixel(in Bitmap image, int x, int y) {
        var offset = image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));

        return new(image.Pixels[offset] / 255f, image.Pixels[offset + 1] / 255f, image.Pixels[offset + 2] / 255f);
    }

    /// <summary>What the vertex stage reads: position, normal, tangent, texcoord.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Vertex {
        public const int Stride = 48;

        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Texcoord;

        public static Vertex[] Quad => [
            Corner(-QuadHalf, -QuadHalf),
            Corner(QuadHalf, -QuadHalf),
            Corner(-QuadHalf, QuadHalf),
            Corner(QuadHalf, QuadHalf)
        ];

        static Vertex Corner(float x, float y) =>
            new() {
                Position = new(x, y, QuadZ),
                Normal = new(0f, 0f, 1f),
                Tangent = new(1f, 0f, 0f, 1f),
                Texcoord = new((x / QuadHalf * 0.5f) + 0.5f, (y / QuadHalf * 0.5f) + 0.5f)
            };
    }

    /// <summary>A rough dielectric, so its response is Lambertian and its albedo is its base colour.</summary>
    static Material Composed() {
        var compilation = MaterialCompiler.Compile(
            new() {
                ShaderName = "ForwardPlus",
                Features = [new MetalRoughnessFeature { BaseColor = Albedo, Metalness = 0f, Roughness = 1f }]
            },

            // The project's decision, not the material's — see MaterialCompiler.ForwardIrradianceSlot.
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

    /// <summary>A world with nothing in it, so every ray reaches the sky.</summary>
    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => Vector3.Zero;
    }

    /// <summary>One radiance from every direction, and surfaces that give back nothing.</summary>
    sealed class UniformSky(float radiance) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(radiance);

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    static void Skip(string? reason) {
        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }
    }
}
