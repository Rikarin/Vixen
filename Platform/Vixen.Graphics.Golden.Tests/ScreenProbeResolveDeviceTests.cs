// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.DistanceFields;
using Vixen.Rendering.IrradianceFields;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The screen-probe resolve, dispatched on a device and read back.</summary>
/// <remarks>
///     The atlas is seeded from the CPU gather under a <i>linear</i> sky, so every texel carries its
///     own number and the projection genuinely integrates — a uniform map constrains only the constant
///     band, which is the blindness the fill comparison already recorded. The reference is
///     <see cref="ScreenProbeAtlas.Resolve" />, whose solid angles are the same exact table the
///     dispatch is handed.
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenProbeResolveDeviceTests {
    [Fact]
    public void TheDispatchedResolveAgreesWithTheReference() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var atlas = new ScreenProbeAtlas(new(new(64, 48)));
        var gather = new TracedScreenProbeGather(new EmptyWorld(), new LinearSky(0.6f, 0.3f));

        gather.Fill(atlas, new Floor());

        // One probe without a surface, so the invalid path is exercised too.
        atlas.Invalidate(new(2, 1));
        atlas.Resolve();

        using var allocator = new DescriptorAllocator(device);
        using var texture = new ScreenProbeTexture(atlas);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields", "ScreenProbes", "SurfaceCache"])));

        using var resolve = new ScreenProbeResolve(device) {
            Effects = effects,
            Pipelines = new ComputePipelineCache(device),
            Descriptors = allocator
        };

        var grid = atlas.Layout.GridSize;
        var resolved = new SphericalHarmonicsL1[grid.X * grid.Y];
        var validities = new float[grid.X * grid.Y];

        allocator.BeginFrame();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "screen probe resolve")) {
            texture.Upload(device, commands);

            Assert.Equal(atlas.Layout.ProbeCount, resolve.Record(commands, texture));
            Assert.True(texture.RecordProbeReadback(commands));

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Null(resolve.Skipped);
        Assert.Equal(1, resolve.Dispatches);
        Assert.Empty(effects.Misses);
        Assert.True(texture.TryReadProbes(resolved, validities));

        if (VulkanDiagnostics.ErrorCount > 0) {
            Assert.Fail(
                "The resolve produced validation errors, so what it wrote is meaningless: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );
        }

        // ⚠ The widest disagreement over every coefficient of every probe, measured rather than
        // assumed. The dispatch reduces sixty-four lanes through a shared-memory tree where
        // `ScreenProbeAtlas.Resolve` adds sixty-four terms in sequence, so bit-exactness is not
        // available and never will be: a parallel reduction reorders a float sum. What is available
        // is a bound, and it has to be a number somebody measured — widening a reduction and then
        // loosening the comparison until it passes is how a reduction wrong by more than
        // reassociation ships.
        //
        // ⚠ **Measured at 2.3841858e-7, and the SERIAL kernel measured exactly the same number.**
        // The baseline was taken by running this test against the one-lane version it replaced, and
        // the widening cost nothing at all: 2.38e-7 is 2^-22, one ulp at these magnitudes, and it is
        // what the rgba32f atlas round trip costs whatever order the sum runs in. That is the answer
        // the doubt deserved rather than a tolerance chosen to fit — and it is the direction the
        // arithmetic predicts, since a tree adding sixty-four non-negative terms in six pairwise
        // steps is the more accurate order than sixty-three sequential ones, not the less.
        var drift = 0f;
        var compared = 0;

        for (var y = 0; y < grid.Y; y++) {
            for (var x = 0; x < grid.X; x++) {
                var probe = new Int2(x, y);
                var index = (y * grid.X) + x;
                var expected = atlas.Resolved(probe);
                var actual = resolved[index];

                Assert.Equal(atlas.IsValid(probe) ? 1f : 0f, validities[index], 1e-4f);

                drift = MathF.Max(drift, Same(expected.L00, actual.L00, $"L00 of probe {probe}"));
                drift = MathF.Max(drift, Same(expected.L1m1, actual.L1m1, $"L1m1 of probe {probe}"));
                drift = MathF.Max(drift, Same(expected.L10, actual.L10, $"L10 of probe {probe}"));
                drift = MathF.Max(drift, Same(expected.L11, actual.L11, $"L11 of probe {probe}"));
                compared++;
            }
        }

        // A loop that asserts inside itself passes vacuously on an empty grid, so the count is part
        // of what this claims.
        Assert.Equal(grid.X * grid.Y, compared);

        // Stated separately from the per-coefficient comparison above, and two orders tighter: the
        // loose hundred-thousandth is what a *device's* filters may cost, and this is what the
        // reduction does. It is here so a later change to the reduction fails on the reduction
        // rather than hiding inside a tolerance sized for something else — this is the assertion
        // that says the sum's shape changed rather than the hardware.
        Assert.True(drift < 1e-6f, $"the tree reduction drifted by {drift}, which is more than reassociation costs");
    }

    /// <summary>Asserts two coefficients agree, and answers how far apart they were.</summary>
    static float Same(Vector3 expected, Vector3 actual, string what) {
        var apart = MathF.Max(
            MathF.Abs(expected.X - actual.X),
            MathF.Max(MathF.Abs(expected.Y - actual.Y), MathF.Abs(expected.Z - actual.Z))
        );

        Assert.True(apart < 1e-4f, $"{what}: the reference says {expected} and the dispatch wrote {actual}");

        return apart;
    }

    sealed class EmptyWorld : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => new(0f, 1f, 0f);
    }

    sealed class LinearSky(float baseline, float tilt) : IRadianceSource {
        public Vector3 Sky(Vector3 direction) => new(baseline + (tilt * direction.Y));

        public Vector3 Surface(Vector3 position, Vector3 normal, Vector3 direction) => Vector3.Zero;
    }

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
