// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.ScreenProbes;
using Vixen.Shaders;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>The nearest reduction on the device, held texel by texel against its CPU referee.</summary>
/// <remarks>
///     <c>NearestReduce</c> and <see cref="ScreenDepthPyramid.Build" /> are one reduction written
///     twice — the same floor-halved mip sizes, the same clamped three-by-three taps, the same
///     maximum — so the chain a kernel's march skips by and the pyramid the CPU march skips by hold
///     the same texels, and the comparison is exact: a maximum over identical floats has no
///     arithmetic to drift in.
/// </remarks>
[Collection("Vulkan")]
public sealed class ScreenPyramidDeviceTests {
    [Fact]
    public void TheNearestReductionMatchesTheRefereeMipForMip() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        // Odd on both axes, deliberately: floor-halving leaves the trailing row and column that the
        // clamped ring exists for, and a power of two would never read it.
        const int Width = 67;
        const int Height = 43;

        var seed = 11u;

        float Next() {
            seed = (seed * 1664525u) + 1013904223u;

            return (seed >> 8) * (1f / 16777216f);
        }

        // A depth with structure and sky: zero on a third of the texels, quantised elsewhere so
        // ties exist for the maximum to break the same way twice.
        var depth = new float[Width * Height];

        for (var i = 0; i < depth.Length; i++) {
            var value = Next();

            depth[i] = value < 0.33f ? 0f : MathF.Round(value * 8f) / 8f;
        }

        var reference = new ScreenDepthPyramid(new(Width, Height));

        reference.Build(depth);

        var loader = new EffectLoader(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(loader, _ => RavenEffects.Only(["Core"], Path.Combine("Pipeline", "NearestReduce.rvn")))
        );

        var chain = new HiZPyramid(device) {
            Reduction = HiZReduction.Nearest,
            Effects = effects,
            Pipelines = new ComputePipelineCache(device)
        };

        owned.Owns(chain.Dispose);

        var screen = owned.Owned(
            "nearest-depth",
            TextureUsage.Sampled | TextureUsage.CopyDestination,
            PixelFormat.R32Float,
            Width,
            Height
        );

        var staging = owned.Buffer<float>(depth, BufferUsage.CopySource);

        // Every mip back to back, each level's texels at its own offset.
        var texels = 0;

        for (var level = 1; level < reference.Levels; level++) {
            texels += reference.SizeOf(level).X * reference.SizeOf(level).Y;
        }

        var readback = device.CreateBuffer(
            new BufferDescription(texels * 4L, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "nearest-readback")
        );

        VulkanDiagnostics.Reset();
        device.BeginFrame();

        using (var commands = device.BeginCommandList(QueueKind.Graphics, "nearest-reduce")) {
            commands.Barrier(
                new([], [new TextureBarrier(screen.Texture, ResourceState.Undefined, ResourceState.CopyDestination)])
            );

            commands.CopyBufferToTexture(staging, 0, new TextureRegion(screen.Texture), new(Width, Height, 1));

            commands.Barrier(
                new([], [new TextureBarrier(screen.Texture, ResourceState.CopyDestination, ResourceState.ShaderRead)])
            );

            Assert.True(chain.Build(commands, screen.View, new(Width, Height)), "the chain did not build");

            commands.Barrier(
                new([], [new TextureBarrier(chain.Texture, ResourceState.ShaderRead, ResourceState.CopySource)])
            );

            var offset = 0L;

            for (var level = 1; level < reference.Levels; level++) {
                var size = reference.SizeOf(level);

                commands.CopyTextureToBuffer(
                    new TextureRegion(chain.Texture, level - 1),
                    new(size.X, size.Y, 1),
                    readback,
                    offset
                );

                offset += size.X * size.Y * 4L;
            }

            commands.Finish();
            device.GraphicsQueue.Submit([commands]);
        }

        device.EndFrame();
        device.WaitIdle();

        Assert.Empty(effects.Misses);
        AssertClean();

        // The CPU counts the depth itself as level zero; the chain's mips are everything above it.
        Assert.Equal(reference.Levels, chain.Levels + 1);

        var values = new float[texels];

        device.Read(readback, 0, MemoryMarshal.AsBytes(values.AsSpan()));
        device.Destroy(readback);

        var at = 0;

        for (var level = 1; level < reference.Levels; level++) {
            var size = reference.SizeOf(level);

            Assert.Equal(GpuCulling.LevelSize(chain.Size, level - 1), size);

            for (var y = 0; y < size.Y; y++) {
                for (var x = 0; x < size.X; x++) {
                    var expected = reference.Nearest(level, new(x, y));
                    var actual = values[at++];

                    Assert.True(
                        expected == actual,
                        $"level {level} cell {x},{y}: the referee says {expected} and the device holds {actual}"
                    );
                }
            }
        }
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
