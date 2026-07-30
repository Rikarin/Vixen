// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Text.Json;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.SurfaceCache;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The surface cache's lighting and bounce dispatched, held against <c>CardRadiosity</c>.</summary>
/// <remarks>
///     <para>
///         The CPU pair exists first and is checked against closed forms and the Cornell box; these
///         run the same passes as dispatches over the same uploaded cache and compare texel by
///         texel. The open-sky facts compose <c>NoDistanceField</c> — pure arithmetic, nothing
///         marched, the tightest comparison there is. The seam fact then hands <b>one</b>
///         <see cref="GlobalDistanceField" /> object to both sides — the CPU reference samples the
///         very grids the clipmap uploads — so the only divergence left is the device's trilinear
///         against the CPU's, and the fixture keeps every decision (a hit, a shadow, a card) a safe
///         margin away from where that hair could flip it.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class SurfaceCacheRadiosityDeviceTests {
    /// <summary>The card struct's offsets are the reflection's, member for member.</summary>
    /// <remarks>The same assertion the fill jobs carry, for the same reason: a struct that agrees
    ///     with a comment agrees with nothing, and std430's rounding of a <c>float3</c> to sixteen
    ///     bytes is exactly the mistake sequential layout would make silently.</remarks>
    [Fact]
    public void TheCardStructMatchesTheReflection() {
        var path = Path.Combine(RavenEffects.Library, "SurfaceCache", "SurfaceCacheGather.reflect.json");

        using var reflection = JsonDocument.Parse(File.ReadAllText(path));

        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var size = 0;

        foreach (var set in reflection.RootElement.GetProperty("Sets").EnumerateArray()) {
            foreach (var binding in set.GetProperty("Bindings").EnumerateArray()) {
                if (binding.GetProperty("Name").GetString() != "cards") {
                    continue;
                }

                foreach (var member in binding.GetProperty("Members").EnumerateArray()) {
                    var name = member.GetProperty("Name").GetString()!;

                    if (name == "cards") {
                        size = member.GetProperty("Size").GetInt32();
                    } else {
                        offsets[name["cards.".Length..]] = member.GetProperty("Offset").GetInt32();
                    }
                }
            }
        }

        Assert.Equal(SurfaceCacheCardData.Stride, size);
        Assert.Equal(offsets["centre"], (int)Marshal.OffsetOf<SurfaceCacheCardData>(nameof(SurfaceCacheCardData.Centre)));
        Assert.Equal(offsets["halfSize"], (int)Marshal.OffsetOf<SurfaceCacheCardData>(nameof(SurfaceCacheCardData.HalfSize)));
        Assert.Equal(offsets["origin"], (int)Marshal.OffsetOf<SurfaceCacheCardData>(nameof(SurfaceCacheCardData.Origin)));
        Assert.Equal(offsets["resolution"], (int)Marshal.OffsetOf<SurfaceCacheCardData>(nameof(SurfaceCacheCardData.Resolution)));
        Assert.Equal(offsets["axis"], (int)Marshal.OffsetOf<SurfaceCacheCardData>(nameof(SurfaceCacheCardData.Axis)));
    }

    /// <summary>The lighting dispatch is the CPU pass, texel for texel, with nothing shadowing.</summary>
    [Fact]
    public void DirectLightAgreesWithTheReferenceUnderAnOpenSky() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var (store, _) = Scene();

        using var allocator = new DescriptorAllocator(device);
        using var texture = new SurfaceCacheTexture(store);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "SurfaceCache"])));

        var pipelines = new ComputePipelineCache(device);

        using var light = new SurfaceCacheLightFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            TowardSun = Vector3.Normalize(new(0.3f, 1f, 0.1f)),
            SunIrradiance = new(2f, 1.5f, 1f)
        };

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "surface-light")) {
            texture.Upload(device, commands);

            Assert.Equal(store.Cards.Count, light.Record(commands, texture));
            Assert.True(texture.RecordDirectReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(light.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        // The reference, over a world with nothing in it — the composition the dispatch ran under.
        var radiosity = new CardRadiosity(new EmptyWorld());

        radiosity.Light(store, light.TowardSun, light.SunIrradiance);

        var texels = new Vector4[store.Atlas.Size.X * store.Atlas.Size.Y];

        Assert.True(texture.TryReadDirect(texels));
        CompareDirect(store, texels, 1e-4f);
    }

    /// <summary>Every valid texel's gather under a uniform sky is that sky, on the device too.</summary>
    [Fact]
    public void AGatherUnderAUniformSkyIsTheSkyOnTheDevice() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var (store, _) = Scene();

        using var allocator = new DescriptorAllocator(device);
        using var texture = new SurfaceCacheTexture(store);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "SurfaceCache"])));

        var pipelines = new ComputePipelineCache(device);

        using var gather = new SurfaceCacheGatherFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            SkyColour = new(0.6f, 0.5f, 0.4f)
        };

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "surface-gather")) {
            texture.Upload(device, commands);

            Assert.Equal(store.Cards.Count, gather.Record(commands, texture));
            Assert.True(texture.RecordGatherReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(gather.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        var reference = new CardRadiosity(new EmptyWorld()) { Sky = _ => gather.SkyColour };

        reference.Gather(store);

        var texels = new Vector4[store.Atlas.Size.X * store.Atlas.Size.Y];

        Assert.True(texture.TryReadGather(texels));

        // The closed form first — the sky, exactly — then the reference, which must be the same
        // number twice.
        for (var index = 0; index < store.Cards.Count; index++) {
            var (card, origin) = store.Cards[index];

            for (var y = 0; y < card.Resolution.Y; y++) {
                for (var x = 0; x < card.Resolution.X; x++) {
                    var texel = texels[((origin.Y + y) * store.Atlas.Size.X) + origin.X + x];

                    if (!store.IsValid(index, new(x, y))) {
                        continue;
                    }

                    Assert.Equal(gather.SkyColour.X, texel.X, 1e-4f);
                    Assert.Equal(gather.SkyColour.Y, texel.Y, 1e-4f);
                    Assert.Equal(store.Gathered(index, new(x, y)).X, texel.X, 1e-4f);
                }
            }
        }
    }

    /// <summary>One clipmap, both sides: the bounce reads the cache through the composed sampler.</summary>
    /// <remarks>
    ///     The discriminating fact for the whole seam. The gather rays march the uploaded clipmap, a
    ///     hit asks <c>SurfaceCacheSource</c> — the Raven half of <c>TryRadiance</c> — and the CPU
    ///     reference does the identical walk over the identical grids. The panel's emissive reaching
    ///     the floor and the floor's lit albedo reaching the panel are the two directions of doc 19
    ///     § L4's answer, and both are asserted against the reference and against zero.
    /// </remarks>
    [Fact]
    public void TheBounceReadsTheCacheThroughTheClipmap() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var (store, world) = Scene();

        // Sun straight overhead at irradiance π: the floor's direct term is exactly one where the
        // panel does not shadow it, and the panel's underside faces away and gets nothing.
        var radiosity = new CardRadiosity(world) { Sky = _ => new(0.5f, 0.4f, 0.3f), MaxDistance = 8f };

        radiosity.Light(store, new(0f, 1f, 0f), new(MathF.PI));

        using var allocator = new DescriptorAllocator(device);
        using var texture = new SurfaceCacheTexture(store);
        using var fieldTexture = new GlobalDistanceFieldTexture(world);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "SurfaceCache"])));

        var pipelines = new ComputePipelineCache(device);

        using var gather = new SurfaceCacheGatherFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator,
            Source = "GlobalDistanceField",
            CacheSource = MaterialCompiler.SurfaceCacheShader,
            SkyColour = new(0.5f, 0.4f, 0.3f),
            MaxDistance = 8f
        };

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "surface-bounce")) {
            texture.Upload(device, commands);
            fieldTexture.Upload(device, commands);

            // After the uploads, because the views have to exist to be written.
            fieldTexture.Apply(gather.Parameters, $"{SurfaceCacheGatherFill.ShaderName}.GlobalDistanceField");
            texture.Apply(gather.Parameters, $"{SurfaceCacheGatherFill.ShaderName}.{MaterialCompiler.SurfaceCacheShader}");

            Assert.Equal(store.Cards.Count, gather.Record(commands, texture));
            Assert.True(texture.RecordGatherReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(gather.Skipped);
        Assert.Empty(effects.Misses);
        AssertClean();

        // The reference gathers over the same grids, and the store swaps — Gathered is the pass.
        radiosity.Gather(store);

        var texels = new Vector4[store.Atlas.Size.X * store.Atlas.Size.Y];

        Assert.True(texture.TryReadGather(texels));

        var worst = 0f;
        var floorLit = 0f;
        var panelLit = 0f;

        for (var index = 0; index < store.Cards.Count; index++) {
            var (card, origin) = store.Cards[index];

            for (var y = 0; y < card.Resolution.Y; y++) {
                for (var x = 0; x < card.Resolution.X; x++) {
                    if (!store.IsValid(index, new(x, y))) {
                        continue;
                    }

                    var gpu = texels[((origin.Y + y) * store.Atlas.Size.X) + origin.X + x];
                    var cpu = store.Gathered(index, new(x, y));

                    worst = MathF.Max(worst, MathF.Abs(gpu.X - cpu.X));
                    worst = MathF.Max(worst, MathF.Abs(gpu.Y - cpu.Y));
                    worst = MathF.Max(worst, MathF.Abs(gpu.Z - cpu.Z));

                    if (card.Axis == 2) {
                        floorLit = MathF.Max(floorLit, gpu.X);
                    }

                    if (card.Axis == 3) {
                        panelLit = MathF.Max(panelLit, gpu.X);
                    }
                }
            }
        }

        // Measured before stated: on the machine that measured it the drift was exactly zero — the
        // marches land in regions where the sampled field is linear and the arithmetic is the same
        // IEEE arithmetic — and the stated bound is what survives a device whose texture filter
        // carries fewer weight bits than this one's.
        Assert.True(worst < 1e-4f, $"the dispatch drifted {worst} from the reference");

        // And the seam itself carried light both ways: the panel's emissive pulls a floor texel
        // above the sky it would otherwise see, and the lit floor reaches the panel's underside.
        Assert.True(floorLit > 0.6f, $"no floor texel rose above the sky ({floorLit}), so no ray ever read the cache");
        Assert.True(panelLit > 0.1f, $"the panel's underside saw nothing ({panelLit}), so the lit floor never answered");
    }

    /// <summary>A floor slab under an emissive panel — every capture, both worlds, one store.</summary>
    /// <remarks>
    ///     The clipmap is built and composited even for the open-sky facts, because the capture
    ///     always marches it: the surfaces the cards hold have to be real surfaces of the same field
    ///     the seam fact traces, or the depths the dispatch reconstructs positions from would belong
    ///     to a different world than the rays.
    /// </remarks>
    static (SurfaceCacheStore Store, GlobalDistanceField World) Scene() {
        var world = new GlobalDistanceField(resolution: 48, finestExtent: 4f);

        world.Update(
            Vector3.Zero,
            [
                DistanceFieldInstance.At(Slab(new(0f, -0.5f, 0f), new(3f, 0.5f, 3f)), Vector3.Zero),
                DistanceFieldInstance.At(Slab(new(0f, 2.25f, 0f), new(1f, 0.25f, 1f)), Vector3.Zero)
            ]
        );

        var store = new SurfaceCacheStore(new SurfaceCacheAtlas(new(32, 32)));

        // The floor's card looks down at the slab's top; the panel's looks up at its underside.
        var floor = store.AddCard(new(2, new(0f, -0.05f, 0f), new(2f, 0.15f, 2f), new(8, 8)));
        var panel = store.AddCard(new(3, new(0f, 1.95f, 0f), new(1f, 0.15f, 1f), new(8, 8)));

        Assert.True(floor >= 0 && panel >= 0);

        var capture = new TracedCardCapture(world, new SlabPaint());

        Assert.Equal(64, capture.Capture(store, floor));
        Assert.Equal(64, capture.Capture(store, panel));

        return (store, world);
    }

    /// <summary>A box's field, written from its own equation rather than baked from triangles.</summary>
    static MeshDistanceField Slab(Vector3 centre, Vector3 half, int resolution = 24) {
        var pad = new Vector3(0.6f);
        var bounds = new BoundingBox(centre - half - pad, centre + half + pad);
        var distances = new float[resolution * resolution * resolution];
        var field = new MeshDistanceField(bounds, new(resolution), distances);

        for (var z = 0; z < resolution; z++) {
            for (var y = 0; y < resolution; y++) {
                for (var x = 0; x < resolution; x++) {
                    var q = Vector3.Abs(field.PositionOf(x, y, z) - centre) - half;
                    var outside = Vector3.Max(q, Vector3.Zero).Length();
                    var inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);

                    distances[x + (resolution * (y + (resolution * z)))] = outside + inside;
                }
            }
        }

        return field;
    }

    static void CompareDirect(SurfaceCacheStore store, Vector4[] texels, float tolerance) {
        for (var index = 0; index < store.Cards.Count; index++) {
            var (card, origin) = store.Cards[index];

            for (var y = 0; y < card.Resolution.Y; y++) {
                for (var x = 0; x < card.Resolution.X; x++) {
                    var texel = texels[((origin.Y + y) * store.Atlas.Size.X) + origin.X + x];

                    if (!store.IsValid(index, new(x, y))) {
                        continue;
                    }

                    var expected = store.Direct(index, new(x, y));

                    Assert.True(
                        MathF.Abs(expected.X - texel.X) < tolerance
                        && MathF.Abs(expected.Y - texel.Y) < tolerance
                        && MathF.Abs(expected.Z - texel.Z) < tolerance,
                        $"card {index} texel ({x}, {y}): device {texel} against reference {expected}"
                    );
                }
            }
        }
    }

    /// <summary>The paint: emissive above head height, diffuse floor below.</summary>
    sealed class SlabPaint : ISurfaceMaterial {
        public Vector3 Albedo(Vector3 position, Vector3 normal) => position.Y > 1f ? new(0.1f) : new(0.7f);

        public Vector3 Emissive(Vector3 position, Vector3 normal) => position.Y > 1f ? new(3f, 2f, 1f) : Vector3.Zero;
    }

    /// <summary>A world with nothing in it — the CPU half of <c>NoDistanceField</c>.</summary>
    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
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
