// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Materials;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The screen-probe trace, dispatched on a device and read back.</summary>
/// <remarks>
///     <para>
///         The same two-sided arrangement as <c>IrradianceFillDeviceTests</c>, one probe kind over:
///         <c>TracedScreenProbeGather</c> is the reference, the dispatch is compared against it texel
///         by texel, and the composition is <c>NoDistanceField</c> — every ray reaches the sky, which
///         is the closed form both sides are separately held to.
///     </para>
///     <para>
///         <b>The linear sky is what makes the comparison see the decode.</b> Under a uniform sky
///         every texel is the same number whatever direction it stands for, so a transposed or
///         mirrored octahedral decode is invisible — the blindness the cube capture's tests escaped by
///         lighting a single texel. A sky that varies with a direction's y gives every texel its own
///         answer, and the dispatch can only reproduce the reference by decoding the same direction
///         from the same texel.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenProbeTraceDeviceTests {
    const float Radiance = 0.75f;

    /// <summary>What was packed and uploaded is what a readback returns — layout included.</summary>
    /// <remarks>
    ///     The half the dispatch cannot check: the pack, the row pitch, and the atlas placement of
    ///     every probe's patch, pinned by pushing a CPU-gathered atlas through the device and back.
    ///     One probe is invalidated first, so the validity alpha is exercised in both states.
    /// </remarks>
    [Fact]
    public void WhatWasUploadedReadsBack() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var atlas = new ScreenProbeAtlas(new(new(64, 48)));
        var gather = new TracedScreenProbeGather(new EmptyWorld(), new LinearSky(0.6f, 0.3f));

        gather.Fill(atlas, new Floor());
        atlas.Invalidate(new(2, 1));

        using var texture = new ScreenProbeTexture(atlas);

        var texels = new Vector4[atlas.Layout.AtlasSize.X * atlas.Layout.AtlasSize.Y];

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "screen probe upload")) {
            texture.Upload(device, commands);

            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.True(texture.TryRead(texels));
        AssertClean();

        Compare(atlas, texels, probe => atlas.IsValid(probe));
    }

    /// <summary>
    ///     Every texel the dispatch traced is the texel the reference gather traced.
    /// </summary>
    /// <param name="tilt">How much the sky changes per unit of a direction's y — zero is uniform.</param>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    public void TheDispatchedTraceAgreesWithTheReference(float tilt) {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // The reference, gathered entirely on the CPU.
        var reference = new ScreenProbeAtlas(new(new(64, 48)));

        new TracedScreenProbeGather(new EmptyWorld(), new LinearSky(Radiance, tilt)).Fill(reference, new Floor());

        // The device side gets the surfaces and nothing else — the texels are the dispatch's.
        var traced = new ScreenProbeAtlas(new(new(64, 48)));
        var floor = new Floor();

        for (var y = 0; y < traced.Layout.GridSize.Y; y++) {
            for (var x = 0; x < traced.Layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                Assert.True(floor.TrySurface(traced.Layout.Anchor(probe), out var position, out var normal));
                traced.SetSurface(probe, position, normal);
            }
        }

        using var allocator = new DescriptorAllocator(device);
        using var texture = new ScreenProbeTexture(traced) { AtlasIsWritten = true };

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        // ScreenProbes and everything its two slots reach into: DistanceFields for the march, and
        // IrradianceFields because the trace terminates long rays there — the import exists even when
        // NoIrradiance fills the slot.
        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields", "ScreenProbes"]))
        );

        using var trace = new ScreenProbeTraceFill(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator,
            SkyColour = new(Radiance),
            SkyGradient = new(tilt)
        };

        var texels = new Vector4[traced.Layout.AtlasSize.X * traced.Layout.AtlasSize.Y];

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "screen probe trace")) {
            texture.Upload(device, commands);

            Assert.Equal(traced.Layout.ProbeCount, trace.Record(commands, texture));
            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(trace.Skipped);
        Assert.Equal(1, trace.Dispatches);
        Assert.Empty(effects.Misses);
        Assert.True(texture.TryRead(texels));
        AssertClean();

        Compare(reference, texels, _ => true);
    }

    /// <summary>
    ///     A ray that runs out of budget terminates in the irradiance field on the device too — and
    ///     beyond the field's box, in the sky, on both sides alike.
    /// </summary>
    /// <param name="maxDistance">The ray budget: four ends inside the field, fifty beyond it.</param>
    /// <remarks>
    ///     The reference computes its termination through <c>IrradianceField.TrySample</c> and the
    ///     dispatch through <c>IrradianceFieldProbes.Radiance</c>, and the two read the same pool
    ///     through conventions pinned by the field's own tests — so a uniform far field either comes
    ///     back identical from both, or one of the two lookups is not the lookup it claims to be.
    /// </remarks>
    [Theory]
    [InlineData(4f)]
    [InlineData(50f)]
    public void AMissTerminatesInTheFieldOnTheDeviceToo(float maxDistance) {
        const float FarRadiance = 0.4f;

        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var field = new IrradianceField(new BoundingBox(new(-8f), new(8f)), new(2));

        field.AllocateAll();
        new TracedIrradianceFiller(new EmptyWorld(), new LinearSky(FarRadiance, 0f)).Fill(field);
        field.SyncBorders();

        var reference = new ScreenProbeAtlas(new(new(64, 48)));

        new TracedScreenProbeGather(
            new EmptyWorld(),
            new LinearSky(Radiance, 0f),
            new ScreenProbeGatherSettings { MaxDistance = maxDistance }
        ) { FarField = field }.Fill(reference, new Floor());

        var traced = new ScreenProbeAtlas(new(new(64, 48)));
        var floor = new Floor();

        for (var y = 0; y < traced.Layout.GridSize.Y; y++) {
            for (var x = 0; x < traced.Layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                Assert.True(floor.TrySurface(traced.Layout.Anchor(probe), out var position, out var normal));
                traced.SetSurface(probe, position, normal);
            }
        }

        using var allocator = new DescriptorAllocator(device);
        using var texture = new ScreenProbeTexture(traced) { AtlasIsWritten = true };
        using var fieldTexture = new IrradianceFieldTexture(field);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields", "ScreenProbes"]))
        );

        using var trace = new ScreenProbeTraceFill(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator,
            SkyColour = new(Radiance),
            MaxDistance = maxDistance,
            FarSource = MaterialCompiler.IrradianceFieldShader
        };

        var texels = new Vector4[traced.Layout.AtlasSize.X * traced.Layout.AtlasSize.Y];

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "screen probe far trace")) {
            fieldTexture.Upload(device, commands);
            texture.Upload(device, commands);

            // The field's volumes, under the slot's qualified prefix — the same handover the forward
            // pass's composition uses, one shader over. After the upload, because that is what makes
            // the views exist to be written.
            fieldTexture.Apply(trace.Parameters, $"{ScreenProbeTraceFill.ShaderName}.{MaterialCompiler.IrradianceFieldShader}");

            Assert.Equal(traced.Layout.ProbeCount, trace.Record(commands, texture));
            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(trace.Skipped);
        Assert.Empty(effects.Misses);
        Assert.True(texture.TryRead(texels));
        AssertClean();

        Compare(reference, texels, _ => true);
    }

    /// <summary>
    ///     The trace order's first stage on the device: a wall only the depth buffer can see stops
    ///     the same rays it stops on the CPU.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The distance field is empty, so every darkened texel is the screen's doing — and a
    ///         second reference without the screen trace proves the wall darkened something, or the
    ///         agreement would be the sky agreeing with itself.
    ///     </para>
    ///     <para>
    ///         ⚠ A screen hit is a <i>binary</i> decision, so this comparison leans harder on "one
    ///         function evaluated twice" than the radiance comparisons do: a last-bit disagreement in
    ///         the octahedral decode would not nudge a texel by a millionth, it would flip it whole.
    ///         The orthographic camera keeps the projection affine for exactly that reason. If
    ///         another driver ever flips a marginal texel here, the upgrade is a margin audit on the
    ///         reference — assert no sample lands within float noise of a threshold — not a wider
    ///         tolerance.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TheScreenTraceStopsTheSameRaysOnTheDevice() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // A wall over the right half of a 64×48 screen, at z = −3 under the placement tests'
        // orthographic camera — device depth 0.75. The left half is sky.
        var screenSurface = new ReconstructedScreenSurface(new(64, 48));

        for (var y = 0; y < 48; y++) {
            for (var x = 32; x < 64; x++) {
                screenSurface.Depth[(y * 64) + x] = 0.75f;
            }
        }

        var camera = Matrix4x4.Orthographic(4f, 4f, 1f, 9f);
        var settings = new ScreenProbeGatherSettings { MaxDistance = 8f };

        var reference = new ScreenProbeAtlas(new(new(64, 48)));

        new TracedScreenProbeGather(new EmptyWorld(), new LinearSky(Radiance, 0.3f), settings) {
            ScreenTrace = new(screenSurface) { ViewProjection = camera }
        }.Fill(reference, new Floor());

        // The wall must have stopped something, or agreement below proves nothing.
        var bare = new ScreenProbeAtlas(new(new(64, 48)));

        new TracedScreenProbeGather(new EmptyWorld(), new LinearSky(Radiance, 0.3f), settings).Fill(bare, new Floor());

        Assert.True(Differs(reference, bare), "no ray was stopped by the wall, so the scene tests nothing");

        var traced = new ScreenProbeAtlas(new(new(64, 48)));
        var floor = new Floor();

        for (var y = 0; y < traced.Layout.GridSize.Y; y++) {
            for (var x = 0; x < traced.Layout.GridSize.X; x++) {
                var probe = new Int2(x, y);

                Assert.True(floor.TrySurface(traced.Layout.Anchor(probe), out var position, out var normal));
                traced.SetSurface(probe, position, normal);
            }
        }

        using var allocator = new DescriptorAllocator(device);
        using var texture = new ScreenProbeTexture(traced) { AtlasIsWritten = true };

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields", "ScreenProbes"]))
        );

        // The depth buffer, uploaded as the frame would have drawn it.
        var depthTexture = owned.Owned(
            "screen depth",
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.Rgba32Float,
            64,
            48
        );

        var depthTexels = new float[64 * 48 * 4];

        for (var i = 0; i < 64 * 48; i++) {
            depthTexels[i * 4] = screenSurface.Depth[i];
        }

        var staging = device.CreateBuffer(
            new(
                (long)depthTexels.Length * sizeof(float),
                BufferUsage.CopySource,
                MemoryAccess.HostUpload,
                "screen depth staging"
            )
        );

        device.Write(staging, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(depthTexels.AsSpan()));

        using var trace = new ScreenProbeTraceFill(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator,
            SkyColour = new(Radiance),
            SkyGradient = new(0.3f),
            MaxDistance = 8f,
            ScreenDepth = depthTexture.View,
            ScreenViewport = new(64, 48),
            ViewProjection = camera
        };

        var texels = new Vector4[traced.Layout.AtlasSize.X * traced.Layout.AtlasSize.Y];

        allocator.BeginFrame();
        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "screen trace")) {
            commands.Barrier(
                new([], [new TextureBarrier(depthTexture.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
            );

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(depthTexture.Texture), new(64, 48, 1));

            commands.Barrier(
                new([], [new TextureBarrier(depthTexture.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
            );

            texture.Upload(device, commands);

            Assert.Equal(traced.Layout.ProbeCount, trace.Record(commands, texture));
            Assert.True(texture.RecordReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();
        device.Destroy(staging);

        Assert.Null(trace.Skipped);
        Assert.Empty(effects.Misses);
        Assert.True(texture.TryRead(texels));
        AssertClean();

        Compare(reference, texels, _ => true);
    }

    /// <summary>Whether any texel of two same-shaped atlases disagrees.</summary>
    static bool Differs(ScreenProbeAtlas left, ScreenProbeAtlas right) {
        var layout = left.Layout;

        for (var py = 0; py < layout.GridSize.Y; py++) {
            for (var px = 0; px < layout.GridSize.X; px++) {
                for (var ty = 0; ty < layout.MapResolution; ty++) {
                    for (var tx = 0; tx < layout.MapResolution; tx++) {
                        if (left[new Int2(px, py), new(tx, ty)] != right[new Int2(px, py), new(tx, ty)]) {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Holds every valid probe's patch of the readback against the reference atlas.</summary>
    /// <remarks>
    ///     Ten-thousandths, which is what two machines' float maths cost — anything structural is off
    ///     by far more: a mirrored decode swaps whole texels, and a misplaced patch reads another
    ///     probe's map.
    /// </remarks>
    static void Compare(ScreenProbeAtlas expected, Vector4[] actual, Func<Int2, bool> covered) {
        var layout = expected.Layout;
        var width = layout.AtlasSize.X;
        var compared = 0;

        for (var py = 0; py < layout.GridSize.Y; py++) {
            for (var px = 0; px < layout.GridSize.X; px++) {
                var probe = new Int2(px, py);

                if (!covered(probe)) {
                    continue;
                }

                var origin = layout.AtlasOrigin(probe);
                var validity = expected.IsValid(probe) ? 1f : 0f;

                for (var ty = 0; ty < layout.MapResolution; ty++) {
                    for (var tx = 0; tx < layout.MapResolution; tx++) {
                        var texel = actual[((origin.Y + ty) * width) + origin.X + tx];
                        var reference = expected.IsValid(probe)
                            ? expected[probe, new(tx, ty)]
                            : Vector3.Zero;

                        var what = $"texel {tx},{ty} of probe {probe}";

                        Assert.True(
                            MathF.Abs(texel.X - reference.X) < 1e-4f
                            && MathF.Abs(texel.Y - reference.Y) < 1e-4f
                            && MathF.Abs(texel.Z - reference.Z) < 1e-4f,
                            $"{what}: the reference says {reference} and the device holds {texel}"
                        );

                        Assert.True(
                            MathF.Abs(texel.W - validity) < 1e-4f,
                            $"{what}: validity should be {validity} and the device holds {texel.W}"
                        );

                        compared++;
                    }
                }
            }
        }

        Assert.True(compared > 0, "nothing was compared, so agreement proves nothing");
    }

    static void AssertClean() {
        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The run produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }
    }

    // --- The scenes, matching the reference tests' ------------------------

    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    /// <summary>A sky of <c>baseline + tilt · direction.y</c>, and surfaces that give back nothing.</summary>
    sealed class LinearSky(float baseline, float tilt) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(baseline + (tilt * direction.Y));

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

    /// <summary>Every pixel shows a floor at y = 0, facing up.</summary>
    sealed class Floor : IScreenSurface {
        public bool TrySurface(Int2 pixel, out Vector3 position, out Vector3 normal) {
            position = new((pixel.X - 16) * 0.1f, 0f, (pixel.Y - 16) * 0.1f);
            normal = new(0f, 1f, 0f);

            return true;
        }
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
