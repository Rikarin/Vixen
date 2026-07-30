// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.Reflections;
using Vixen.Rendering.ScreenProbes;
using Vixen.Rendering.SurfaceCache;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The reflection kernel, held texel by texel against <c>TracedReflections</c>.</summary>
/// <remarks>
///     One eight-by-eight dispatch covers every answer the reference distinguishes: a sharp hit on
///     an emissive wall answered through the cache, a sharp miss beside it answered by the sky
///     slot, the blend band mixing trace and field, the field whole above the threshold, and an
///     invalid texel answering nothing. Both sides march the same <see cref="GlobalDistanceField" />
///     grids, read the same cache, and sample a field filled under the same uniform sky — the
///     zero-drift arrangement the radiosity tests established, now across four slots at once.
/// </remarks>
[Collection("Vulkan")]
public sealed class ReflectionTraceDeviceTests {
    const int Side = 8;

    static readonly Vector3 SkyMiss = new(0.2f, 0.15f, 0.1f);

    [Fact]
    public void TheKernelIsTheReferenceTexelByTexel() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // The world: a floor and an emissive wall, composited into the clipmap both sides march.
        var world = new GlobalDistanceField(resolution: 48, finestExtent: 4f);

        world.Update(
            Vector3.Zero,
            [
                DistanceFieldInstance.At(Slab(new(0f, -0.5f, 0f), new(3f, 0.5f, 3f)), Vector3.Zero),
                DistanceFieldInstance.At(Slab(new(2.5f, 1.5f, 0f), new(0.5f, 3f, 3f)), Vector3.Zero)
            ]
        );

        var store = new SurfaceCacheStore(new SurfaceCacheAtlas(new(32, 32)));
        var floor = store.AddCard(new(2, new(0f, -0.05f, 0f), new(2f, 0.15f, 2f), new(8, 8)));
        var wall = store.AddCard(new(1, new(2.1f, 1.5f, 0f), new(0.2f, 2.5f, 2.5f), new(8, 8)));

        var capture = new TracedCardCapture(world, new Paint());

        Assert.Equal(64, capture.Capture(store, floor));
        Assert.Equal(64, capture.Capture(store, wall));

        new CardRadiosity(world) { MaxDistance = 8f }.Light(store, new(0f, 1f, 0f), new(MathF.PI));

        // The field the rough path reads: uniform, so its answer has a closed form of its own.
        var field = new IrradianceField(new BoundingBox(new(-4f), new(4f)), new(2));

        field.AllocateAll();
        new TracedIrradianceFiller(new EmptyWorld(), new UniformSky(2.5f)).Fill(field);

        // The surfaces: one camera, one up normal, roughness ramping across x — with column zero
        // aimed past the wall (a sharp miss) and the last texel invalid.
        var camera = new Vector3(-2f, 3f, 0f);
        var positions = new Vector4[Side * Side];
        var normals = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var z = x == 0 ? -3.5f : -0.7f + (0.2f * y);

                positions[at] = new(0f, 0.5f, z, 1f);
                normals[at] = new(0f, 1f, 0f, x / 7f);
            }
        }

        positions[^1] = new(0f, 0.5f, 0f, 0f);

        using var allocator = new DescriptorAllocator(device);
        using var texture = new SurfaceCacheTexture(store);
        using var fieldTexture = new GlobalDistanceFieldTexture(world);
        using var probes = new IrradianceFieldTexture(field);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(["Core", "Shading", "DistanceFields", "IrradianceFields", "SurfaceCache", "Reflections"])
            )
        );

        var pipelines = new ComputePipelineCache(device);

        var positionPlane = owned.Owned("reflect-positions", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var normalPlane = owned.Owned("reflect-normals", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var target = owned.Owned("reflect-target", TextureUsage.Storage | TextureUsage.CopySource, PixelFormat.Rgba32Float, Side, Side);

        var staging = owned.Buffer<Vector4>([.. positions, .. normals], BufferUsage.CopySource);
        var readback = device.CreateBuffer(
            new BufferDescription(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "reflect-readback")
        );

        using var trace = new ReflectionTraceFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            Source = "GlobalDistanceField",
            CacheSource = MaterialCompiler.SurfaceCacheShader,
            RoughSource = MaterialCompiler.IrradianceFieldShader,
            MissSource = MaterialCompiler.SkyReflectionMissShader,
            Positions = positionPlane.View,
            Normals = normalPlane.View,
            Target = target.View,
            Viewport = new(Side, Side),
            CameraPosition = camera,
            MaxDistance = 8f,
            RoughnessThreshold = 0.5f,
            RoughnessBlend = 0.25f
        };

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "reflection-trace")) {
            texture.Upload(device, commands);
            fieldTexture.Upload(device, commands);
            probes.Upload(device, commands);

            // The planes: staged, copied, and settled where the kernel samples them.
            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(positionPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(normalPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(target.Texture, ResourceState.Undefined, SurfaceCacheTexture.PlaneIsBeingWritten)
                    ]
                )
            );

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(positionPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, Side * Side * 16, new TextureRegion(normalPlane.Texture), new(Side, Side, 1));

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(positionPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(normalPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)
                    ]
                )
            );

            fieldTexture.Apply(trace.Parameters, $"{ReflectionTraceFill.ShaderName}.GlobalDistanceField");
            texture.Apply(trace.Parameters, $"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.SurfaceCacheShader}");
            probes.Apply(trace.Parameters, $"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.IrradianceFieldShader}");
            trace.Parameters.Set(
                ParameterKeys.New<Vector3>($"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.SkyReflectionMissShader}.missSkyColor"),
                SkyMiss
            );

            Assert.Equal(Side * Side, trace.Record(commands));

            commands.Barrier(
                new([], [new TextureBarrier(target.Texture, SurfaceCacheTexture.PlaneIsBeingWritten, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(target.Texture), new(Side, Side, 1), readback, 0);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(trace.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        var answers = new Vector4[Side * Side];
        var bytes = new float[Side * Side * 4];

        device.Read(readback, 0, MemoryMarshal.AsBytes(bytes.AsSpan()));
        device.Destroy(readback);

        for (var index = 0; index < answers.Length; index++) {
            answers[index] = new(bytes[index * 4], bytes[(index * 4) + 1], bytes[(index * 4) + 2], bytes[(index * 4) + 3]);
        }

        // The reference: the same world, the same cache, the same field, the same fallback.
        var dark = new UniformSky(0f);

        var reference = new TracedReflections(world, new SurfaceCacheRadiance(store, dark), new SkyFallback(new UniformSky(SkyMiss))) {
            Field = field,
            MaxDistance = 8f,
            RoughnessThreshold = 0.5f,
            RoughnessBlend = 0.25f
        };

        var worst = 0f;
        var sharpHit = 0f;
        var sharpMiss = 0f;
        var wide = 0f;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var answer = answers[at];

                if (positions[at].W < 0.5f) {
                    Assert.Equal(0f, answer.W);

                    continue;
                }

                var position = new Vector3(positions[at].X, positions[at].Y, positions[at].Z);
                var view = Vector3.Normalize(position - camera);
                var expected = reference.Reflect(position, new(0f, 1f, 0f), view, normals[at].W);

                worst = MathF.Max(worst, MathF.Abs(answer.X - expected.X));
                worst = MathF.Max(worst, MathF.Abs(answer.Y - expected.Y));
                worst = MathF.Max(worst, MathF.Abs(answer.Z - expected.Z));

                if (x == 0) {
                    sharpMiss = answer.X;
                } else if (x == 1) {
                    sharpHit = answer.X;
                } else if (x == 7 && y == 0) {
                    wide = answer.X;

                    // The field's own answer for this very read, asked directly — the rough path is
                    // the field speaking, not a number this test guessed.
                    var mirror = Vector3.Normalize(view - (2f * Vector3.Dot(view, new(0f, 1f, 0f)) * new Vector3(0f, 1f, 0f)));

                    Assert.Equal(field.Irradiance(position, mirror).X, answer.X, 5e-3f);
                }
            }
        }

        // Measured before stated, the radiosity tests' own arrangement across one more slot. The
        // march and the cache agree to the bit as they did there; what the drift actually is — four
        // thousandths, measured — is the field read, whose pool the device filters with hardware
        // trilinear weights the CPU does not imitate.
        Assert.True(worst < 0.01f, $"the kernel drifted {worst} from the reference");

        // And the four answers are four different answers, not one number wearing four hats: the
        // miss is the sky slot's colour, the hit is the wall's emissive through the cache, and the
        // rough read sits well above both — the field's light, which neither other path carries.
        Assert.Equal(SkyMiss.X, sharpMiss, 1e-3f);
        Assert.Equal(1.5f, sharpHit, 0.02f);
        Assert.True(wide > sharpHit + 0.2f, $"the rough answer ({wide}) is not distinguishably the field's");
    }

    /// <summary>Doc 06's probes in the miss seat, on the device — the caveat's last mile.</summary>
    /// <remarks>
    ///     An empty world, so every sharp ray misses and the probe answers all of it: the same
    ///     parallax correction, the same inward-measured weight, the same first-non-zero-weight
    ///     selection as <c>ReflectionProbeMiss</c> on the CPU — whose <c>Apply</c> wrote every
    ///     binding the composed sampler reads. The faces are painted one colour each, so the
    ///     bilinear-versus-nearest difference between the two samplers is confined to face seams
    ///     the fixture's directions never cross.
    /// </remarks>
    [Fact]
    public void TheProbesTakeTheMissSeat() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // The cube: six faces, six colours, CPU image and device texture from one array.
        var radiance = new CubeImage(4);
        Vector3[] palette = [
            new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f),
            new(1f, 1f, 0f), new(0f, 1f, 1f), new(1f, 0f, 1f)
        ];

        for (var face = 0; face < 6; face++) {
            radiance.Face((CubeFace)face).Fill(palette[face]);
        }

        var cube = device.CreateTexture(
            new() {
                Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 6, SampleCount = 1,
                Dimension = TextureDimension.TextureCube,
                Format = PixelFormat.Rgba32Float,
                Usage = TextureUsage.Sampled | TextureUsage.CopyDestination,
                Name = "probe-cube"
            }
        );

        var cubeView = device.CreateTextureView(cube);

        owned.Owns(() => {
            device.Destroy(cubeView);
            device.Destroy(cube);
        });

        var faceTexels = new Vector4[6 * 4 * 4];

        for (var face = 0; face < 6; face++) {
            for (var texel = 0; texel < 16; texel++) {
                faceTexels[(face * 16) + texel] = new(palette[face], 1f);
            }
        }

        var cubeStaging = owned.Buffer<Vector4>(faceTexels, BufferUsage.CopySource);

        // A sphere, and a big one, deliberately: box parallax aims corrected directions at the
        // box's corners — cube-face diagonals, where the device's seamless filtering blends faces
        // the CPU's nearest fetch never sees. A wide sphere under a steep camera keeps every
        // corrected direction well inside one face, which is where a uniform-face fixture can
        // referee; the box arithmetic is Ibl.ParallaxCorrect's own, mirrored line for line.
        var probe = new ReflectionProbe {
            CapturePosition = new(0f, 1f, 0f),
            Bounds = new(new(-6f), new(6f)),
            Radius = 6f,
            BlendDistance = 6f,
            Prefiltered = cubeView,
            MipCount = 1
        };

        var miss = new ReflectionProbeMiss { FarColour = SkyMiss };

        miss.Add(probe, radiance);

        // The surfaces: sharp everywhere, positions spread so the weights and the corrected
        // directions vary — a probe answering one number would prove much less. The camera is
        // steep, so every mirror direction leans hard on +Y and no corrected direction goes near
        // a face seam.
        var camera = new Vector3(-1f, 6f, 0f);
        var positions = new Vector4[Side * Side];
        var normals = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;

                positions[at] = new(-1f + (0.25f * x), 0.5f, -1f + (0.25f * y), 1f);
                normals[at] = new(0f, 1f, 0f, 0f);
            }
        }

        using var allocator = new DescriptorAllocator(device);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(["Core", "Shading", "DistanceFields", "IrradianceFields", "SurfaceCache", "Reflections"])
            )
        );

        var pipelines = new ComputePipelineCache(device);

        var positionPlane = owned.Owned("probe-positions", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var normalPlane = owned.Owned("probe-normals", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var target = owned.Owned("probe-target", TextureUsage.Storage | TextureUsage.CopySource, PixelFormat.Rgba32Float, Side, Side);

        var staging = owned.Buffer<Vector4>([.. positions, .. normals], BufferUsage.CopySource);
        var readback = device.CreateBuffer(
            new BufferDescription(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "probe-readback")
        );

        using var trace = new ReflectionTraceFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            MissSource = "ReflectionProbeMissSource",
            Positions = positionPlane.View,
            Normals = normalPlane.View,
            Target = target.View,
            Viewport = new(Side, Side),
            CameraPosition = camera,
            MaxDistance = 8f
        };

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "probe-miss")) {
            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(cube, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(positionPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(normalPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(target.Texture, ResourceState.Undefined, SurfaceCacheTexture.PlaneIsBeingWritten)
                    ]
                )
            );

            for (var face = 0; face < 6; face++) {
                commands.CopyBufferToTexture(cubeStaging, face * 16 * 16, new TextureRegion(cube, 0, face), new(4, 4, 1));
            }

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(positionPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, Side * Side * 16, new TextureRegion(normalPlane.Texture), new(Side, Side, 1));

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(cube, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(positionPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(normalPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)
                    ]
                )
            );

            miss.Apply(
                trace.Parameters,
                $"{ReflectionTraceFill.ShaderName}.ReflectionProbeMissSource",
                owned.Sampler(SamplerDescription.LinearClamp)
            );

            Assert.Equal(Side * Side, trace.Record(commands));

            commands.Barrier(
                new([], [new TextureBarrier(target.Texture, SurfaceCacheTexture.PlaneIsBeingWritten, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(target.Texture), new(Side, Side, 1), readback, 0);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(trace.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        var bytes = new float[Side * Side * 4];

        device.Read(readback, 0, MemoryMarshal.AsBytes(bytes.AsSpan()));
        device.Destroy(readback);

        var reference = new TracedReflections(new EmptyWorld(), new UniformSky(0f), miss) { MaxDistance = 8f };
        var worst = 0f;
        var answered = 0;
        var report = "";

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var answer = new Vector3(bytes[at * 4], bytes[(at * 4) + 1], bytes[(at * 4) + 2]);
                var position = new Vector3(positions[at].X, positions[at].Y, positions[at].Z);
                var view = Vector3.Normalize(position - camera);
                var expected = reference.Reflect(position, new(0f, 1f, 0f), view, 0f);
                var drift = (answer - expected).Length();

                if (drift > worst) {
                    worst = drift;
                    report = $"texel ({x}, {y}) at {position}: device {answer} against reference {expected}";
                }

                if ((answer - new Vector3(SkyMiss.X, SkyMiss.Y, SkyMiss.Z)).Length() > 0.1f) {
                    answered++;
                }
            }
        }

        Assert.True(worst < 5e-3f, $"the probe miss drifted {worst} from the reference — {report}");
        Assert.True(answered > 32, $"only {answered} of 64 texels rose above the far colour — the probe barely answered");
    }

    /// <summary>The screen answers first, and its answer is the frame's own colour.</summary>
    /// <remarks>
    ///     An orthographic screen looking down on the scene, with a ceiling patch in its depth and
    ///     a position-coded colour beside it: mirror rays that cross the patch inside its shell
    ///     reflect the colour at the pixel that stopped them, on both sides of the comparison —
    ///     which is SSR reduced to its arithmetic, the frame's colour reached through the frame's
    ///     depth. Rays aimed past the patch leave the viewport and fall through to the sky, so the
    ///     screen never pretends to answer what it cannot see.
    /// </remarks>
    [Fact]
    public void TheScreenAnswersFirstWithTheFramesColour() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // A top-down orthographic camera as one hand-built row-vector matrix: ndc.x = world.x / 4,
        // ndc.y = world.z / 4, device depth = (world.y + 4) / 8 — reversed, high is near.
        var viewProjection = new Matrix4x4(
            new Vector4(0.25f, 0f, 0f, 0f),
            new Vector4(0f, 0f, 0.125f, 0f),
            new Vector4(0f, 0.25f, 0f, 0f),
            new Vector4(0f, 0f, 0.5f, 1f)
        );

        // The screen: a ceiling patch at y = 2 over x ∈ [0, 2], z ∈ [-2, 2], sky everywhere else,
        // and a colour that encodes its own pixel so a wrong hit cannot read right.
        var surface = new ReconstructedScreenSurface(new(Side, Side));
        var depths = new Vector4[Side * Side];
        var colours = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var ceiling = x is 4 or 5 && y is >= 2 and <= 5;

                surface.Depth[at] = ceiling ? 0.75f : 0f;
                depths[at] = new(ceiling ? 0.75f : 0f, 0f, 0f, 0f);
                colours[at] = new(0.1f * x, 0.1f * y, 0.3f, 1f);
            }
        }

        var camera = new Vector3(-2f, 3f, 0f);
        var positions = new Vector4[Side * Side];
        var normals = new Vector4[Side * Side];

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var z = x == 0 ? -3.5f : -0.7f + (0.2f * y);

                positions[at] = new(0f, 0.5f, z, 1f);
                normals[at] = new(0f, 1f, 0f, 0f);
            }
        }

        using var allocator = new DescriptorAllocator(device);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                loader,
                _ => RavenEffects.Only(["Core", "Shading", "DistanceFields", "IrradianceFields", "SurfaceCache", "Reflections"])
            )
        );

        var pipelines = new ComputePipelineCache(device);

        var positionPlane = owned.Owned("ssr-positions", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var normalPlane = owned.Owned("ssr-normals", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var depthPlane = owned.Owned("ssr-depth", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var colourPlane = owned.Owned("ssr-colour", TextureUsage.Sampled | TextureUsage.CopyDestination, PixelFormat.Rgba32Float, Side, Side);
        var target = owned.Owned("ssr-target", TextureUsage.Storage | TextureUsage.CopySource, PixelFormat.Rgba32Float, Side, Side);

        var staging = owned.Buffer<Vector4>([.. positions, .. normals, .. depths, .. colours], BufferUsage.CopySource);
        var readback = device.CreateBuffer(
            new BufferDescription(Side * Side * 16, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "ssr-readback")
        );

        using var trace = new ReflectionTraceFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            Positions = positionPlane.View,
            Normals = normalPlane.View,
            Target = target.View,
            ScreenDepth = depthPlane.View,
            ScreenColour = colourPlane.View,
            ViewProjection = viewProjection,
            ScreenViewport = new(Side, Side),
            ScreenSteps = 32,
            ScreenThickness = 0.05f,
            Viewport = new(Side, Side),
            CameraPosition = camera,
            MaxDistance = 8f
        };

        trace.Parameters.Set(
            ParameterKeys.New<Vector3>($"{ReflectionTraceFill.ShaderName}.{MaterialCompiler.SkyReflectionMissShader}.missSkyColor"),
            SkyMiss
        );

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "ssr")) {
            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(positionPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(normalPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(depthPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(colourPlane.Texture, ResourceState.Undefined, ResourceState.CopyDestination),
                        new TextureBarrier(target.Texture, ResourceState.Undefined, SurfaceCacheTexture.PlaneIsBeingWritten)
                    ]
                )
            );

            var plane = Side * Side * 16;

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(positionPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, plane, new TextureRegion(normalPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, plane * 2, new TextureRegion(depthPlane.Texture), new(Side, Side, 1));
            commands.CopyBufferToTexture(staging, plane * 3, new TextureRegion(colourPlane.Texture), new(Side, Side, 1));

            commands.Barrier(
                new(
                    [],
                    [
                        new TextureBarrier(positionPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(normalPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(depthPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead),
                        new TextureBarrier(colourPlane.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)
                    ]
                )
            );

            Assert.Equal(Side * Side, trace.Record(commands));

            commands.Barrier(
                new([], [new TextureBarrier(target.Texture, SurfaceCacheTexture.PlaneIsBeingWritten, ResourceState.CopySource)])
            );

            commands.CopyTextureToBuffer(new TextureRegion(target.Texture), new(Side, Side, 1), readback, 0);
            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(trace.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        var bytes = new float[Side * Side * 4];

        device.Read(readback, 0, MemoryMarshal.AsBytes(bytes.AsSpan()));
        device.Destroy(readback);

        var sky = new UniformSky(SkyMiss);

        var reference = new TracedReflections(new EmptyWorld(), new UniformSky(0f), new SkyFallback(sky)) {
            MaxDistance = 8f,
            ScreenTrace = new ScreenSpaceTrace(surface) { ViewProjection = viewProjection, Steps = 32, Thickness = 0.05f },
            ScreenColour = pixel => new(0.1f * pixel.X, 0.1f * pixel.Y, 0.3f)
        };

        var worst = 0f;
        var screened = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var at = (y * Side) + x;
                var answer = new Vector3(bytes[at * 4], bytes[(at * 4) + 1], bytes[(at * 4) + 2]);
                var position = new Vector3(positions[at].X, positions[at].Y, positions[at].Z);
                var view = Vector3.Normalize(position - camera);
                var expected = reference.Reflect(position, new(0f, 1f, 0f), view, 0f);

                worst = MathF.Max(worst, (answer - expected).Length());

                if ((answer - new Vector3(SkyMiss.X, SkyMiss.Y, SkyMiss.Z)).Length() > 0.05f) {
                    screened++;
                }
            }
        }

        // The march is the same IEEE arithmetic over the same planes on both sides.
        Assert.True(worst < 1e-4f, $"the screen trace drifted {worst} from the reference");

        // The aimed-past column misses the viewport and stays sky; the rest found the ceiling.
        Assert.True(screened >= 40, $"only {screened} of 64 texels read the frame's colour — the screen barely answered");

        var missColumn = new Vector3(bytes[0], bytes[1], bytes[2]);

        Assert.Equal(SkyMiss.X, missColumn.X, 1e-4f);
    }

    /// <summary>A box's field, written from its own equation.</summary>
    static MeshDistanceField Slab(Vector3 centre, Vector3 half, int resolution = 24) {
        var pad = new Vector3(0.6f);
        var bounds = new BoundingBox(centre - half - pad, centre + half + pad);
        var distances = new float[resolution * resolution * resolution];
        var mesh = new MeshDistanceField(bounds, new(resolution), distances);

        for (var z = 0; z < resolution; z++) {
            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    var q = Vector3.Abs(mesh.PositionOf(x, y, z) - centre) - half;
                    var outside = Vector3.Max(q, Vector3.Zero).Length();
                    var inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);

                    distances[x + (resolution * (y + (resolution * z)))] = outside + inside;
                }
            }
        }

        return mesh;
    }

    /// <summary>Emissive wall past x = 1, diffuse floor below it.</summary>
    sealed class Paint : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => position.X > 1f ? new(0.3f) : new(0.7f);

        public Vector3 Emissive(Vector3 position, Vector3 normal) => position.X > 1f ? new(1.5f, 1f, 0.5f) : Vector3.Zero;
    }

    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class UniformSky(Vector3 radiance) : IRadianceSource {
        public UniformSky(float radiance) : this(new Vector3(radiance)) { }

        public Vector3 Sky(Vector3 direction) => radiance;

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    static void AssertClean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The run produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan device is available");

        return false;
    }
}
