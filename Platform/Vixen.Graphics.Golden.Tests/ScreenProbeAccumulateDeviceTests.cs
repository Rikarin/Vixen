// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The temporal accumulation dispatched, held frame by frame against the CPU history.</summary>
/// <remarks>
///     Each frame runs the whole device chain — upload, resolve, accumulate, swap — and the same
///     resolved atlas goes through <c>ScreenProbeHistory</c> on the CPU under the same camera. The
///     comparison is per probe, coefficients and accumulated weight alike, after every frame: a
///     drift that compounds is caught at the frame it starts, not at the end where it reads as a
///     tolerance problem.
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenProbeAccumulateDeviceTests {
    static readonly Matrix4x4 Camera = Matrix4x4.Orthographic(4f, 4f, 1f, 9f);

    /// <summary>Constant scenes, a flip, then a camera pan — the CPU tests' scenarios, on a device.</summary>
    [Fact]
    public void TheDispatchAgreesWithTheCpuHistoryFrameByFrame() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var atlas = new ScreenProbeAtlas(new(new(64, 64)));

        using var allocator = new DescriptorAllocator(device);
        using var texture = new ScreenProbeTexture(atlas);
        using var history = new ScreenProbeHistoryTexture(atlas.Layout);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core", "DistanceFields", "IrradianceFields", "ScreenProbes"]))
        );

        var pipelines = new ComputePipelineCache(device);

        using var resolve = new ScreenProbeResolve(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator
        };

        using var accumulate = new ScreenProbeAccumulateFill(device) {
            Effects = effects,
            Pipelines = pipelines,
            Descriptors = allocator
        };

        var reference = new ScreenProbeHistory(atlas.Layout);
        var count = atlas.Layout.ProbeCount;
        var accumulated = new SphericalHarmonicsL1[count];
        var weights = new float[count];

        // Three constant frames converge, a flip follows the running mean, and a pan finds the
        // neighbour — every scenario the CPU recurrences pin, through the dispatch.
        var frames = new (Action<ScreenProbeAtlas> Fill, Matrix4x4 View)[] {
            (a => Seed(a, _ => 1f, 0f), Camera),
            (a => Seed(a, _ => 1f, 0f), Camera),
            (a => Seed(a, _ => 1f, 0f), Camera),
            (a => Seed(a, _ => 0f, 0f), Camera),
            (a => Seed(a, probe => probe.X, 0f), Camera),
            (a => Seed(a, _ => 0f, -1f), Matrix4x4.FromTranslation(new(1f, 0f, 0f)) * Camera)
        };

        foreach (var (fill, view) in frames) {
            fill(atlas);

            accumulate.ViewProjection = view;

            allocator.BeginFrame();
            VulkanDiagnostics.Reset();
            device.BeginFrame();

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "accumulate")) {
                texture.Upload(device, commands);

                Assert.Equal(count, resolve.Record(commands, texture));
                Assert.Equal(count, accumulate.Record(commands, atlas, texture, history));
                Assert.True(history.RecordReadback(commands));

                commands.Finish();
                device.GraphicsQueue.Submit([commands]);
            }

            device.EndFrame();
            device.WaitIdle();

            Assert.Null(resolve.Skipped);
            Assert.Null(accumulate.Skipped);
            Assert.Empty(effects.Misses);
            AssertClean();

            reference.Accumulate(atlas, view);

            Assert.True(history.TryRead(accumulated, weights));

            for (var y = 0; y < atlas.Layout.GridSize.Y; y++) {
                for (var x = 0; x < atlas.Layout.GridSize.X; x++) {
                    var probe = new Int2(x, y);
                    var index = atlas.Layout.ProbeIndex(probe);
                    var expected = reference.Resolved(probe);
                    var actual = accumulated[index];
                    var what = $"probe {probe} after frame {reference.Frames}";

                    Assert.True(
                        MathF.Abs(expected.L00.X - actual.L00.X) < 1e-4f
                        && MathF.Abs(expected.L1m1.X - actual.L1m1.X) < 1e-4f
                        && MathF.Abs(expected.L10.X - actual.L10.X) < 1e-4f
                        && MathF.Abs(expected.L11.X - actual.L11.X) < 1e-4f,
                        $"{what}: the reference holds {expected.L00} and the device {actual.L00}"
                    );

                    Assert.True(
                        MathF.Abs(reference.Weight(probe) - weights[index]) < 1e-4f,
                        $"{what}: the reference weighs {reference.Weight(probe)} and the device {weights[index]}"
                    );
                }
            }
        }
    }

    /// <summary>Valid probes on the z = −5 plane, each map one constant, surfaces shifted by an offset.</summary>
    static void Seed(ScreenProbeAtlas atlas, Func<Int2, float> radiance, float shift) {
        var layout = atlas.Layout;

        for (var y = 0; y < layout.GridSize.Y; y++) {
            for (var x = 0; x < layout.GridSize.X; x++) {
                var probe = new Int2(x, y);
                var anchor = layout.Anchor(probe);

                atlas.SetSurface(
                    probe,
                    new(((anchor.X - 32) / 16f) + shift, (anchor.Y - 32) / 16f, -5f),
                    new(0f, 0f, 1f)
                );

                for (var ty = 0; ty < layout.MapResolution; ty++) {
                    for (var tx = 0; tx < layout.MapResolution; tx++) {
                        atlas[probe, new(tx, ty)] = new(radiance(probe));
                    }
                }
            }
        }

        atlas.Resolve();
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

        Assert.Skip(reason ?? "no Vulkan");

        return false;
    }
}
