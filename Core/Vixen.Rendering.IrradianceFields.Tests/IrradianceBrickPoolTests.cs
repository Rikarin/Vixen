// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>The pool as a volume texture that has not been uploaded: slots, texels and addressing.</summary>
public class IrradianceBrickPoolTests {
    [Fact]
    public void ABrickIsFourProbesInFiveTexels() {
        Assert.Equal(4, IrradianceBrickPool.BrickResolution);
        Assert.Equal(5, IrradianceBrickPool.PaddedResolution);
        Assert.Equal(125, IrradianceBrickPool.TexelsPerBrick);
    }

    [Fact]
    public void APoolIsItsSlotsTimesTheFootprint() {
        var pool = new IrradianceBrickPool(new(2, 3, 4));

        Assert.Equal(24, pool.Capacity);
        Assert.Equal(new Int3(10, 15, 20), pool.TexelResolution);
        Assert.Equal(24 * 125, pool.Texels.Length);
    }

    [Fact]
    public void SlotsComeOutInOrderAndGoBackIn() {
        var pool = new IrradianceBrickPool(new(1, 1, 2));

        Assert.True(pool.TryAllocate(out var first));
        Assert.True(pool.TryAllocate(out var second));

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(2, pool.Count);

        Assert.False(pool.TryAllocate(out var none));
        Assert.Equal(-1, none);

        pool.Release(first);

        Assert.Equal(1, pool.Count);
        Assert.True(pool.TryAllocate(out var again));
        Assert.Equal(first, again);
    }

    /// <summary>
    ///     A released slot forgets what was in it, so the next brick to land there does not show one
    ///     frame of somewhere else's colour — which reads as a flicker and gets blamed on the temporal
    ///     filter.
    /// </summary>
    [Fact]
    public void AReleasedSlotForgetsWhatWasInIt() {
        var pool = new IrradianceBrickPool(new(1));

        Assert.True(pool.TryAllocate(out var slot));

        pool[slot, 2, 2, 2] = Probes.Of(7f);
        pool.Release(slot);

        Assert.True(pool.TryAllocate(out slot));
        Assert.Equal(IrradianceProbe.Empty, pool[slot, 2, 2, 2]);
    }

    [Fact]
    public void ReleasingASlotNobodyHasIsRefused() {
        var pool = new IrradianceBrickPool(new(1));

        Assert.Throws<InvalidOperationException>(() => pool.Release(0));
    }

    /// <summary>Slots tile the pool in X first, so an origin is arithmetic and not a lookup.</summary>
    [Fact]
    public void SlotOriginsTileTheVolume() {
        var pool = new IrradianceBrickPool(new(2, 2, 2));

        Assert.Equal(new Int3(0, 0, 0), pool.OriginOf(0));
        Assert.Equal(new Int3(5, 0, 0), pool.OriginOf(1));
        Assert.Equal(new Int3(0, 5, 0), pool.OriginOf(2));
        Assert.Equal(new Int3(0, 0, 5), pool.OriginOf(4));
        Assert.Equal(new Int3(5, 5, 5), pool.OriginOf(7));
    }

    /// <summary>Two bricks in one pool do not read each other's texels.</summary>
    [Fact]
    public void BricksDoNotOverlap() {
        var pool = new IrradianceBrickPool(new(2, 1, 1));

        Assert.True(pool.TryAllocate(out var a));
        Assert.True(pool.TryAllocate(out var b));

        for (var z = 0; z < IrradianceBrickPool.PaddedResolution; z++) {
            for (var y = 0; y < IrradianceBrickPool.PaddedResolution; y++) {
                for (var x = 0; x < IrradianceBrickPool.PaddedResolution; x++) {
                    pool[a, x, y, z] = Probes.Of(1f);
                }
            }
        }

        for (var z = 0; z < IrradianceBrickPool.PaddedResolution; z++) {
            for (var y = 0; y < IrradianceBrickPool.PaddedResolution; y++) {
                for (var x = 0; x < IrradianceBrickPool.PaddedResolution; x++) {
                    Assert.Equal(0f, pool[b, x, y, z].Value());
                }
            }
        }
    }

    /// <summary>A sample landing exactly on a probe is that probe, at every one of the five planes.</summary>
    [Fact]
    public void SamplingOnAProbeIsThatProbe() {
        var pool = Filled(out var slot);

        for (var z = 0; z <= IrradianceBrickPool.BrickResolution; z++) {
            for (var y = 0; y <= IrradianceBrickPool.BrickResolution; y++) {
                for (var x = 0; x <= IrradianceBrickPool.BrickResolution; x++) {
                    var local = new Vector3(x, y, z) / IrradianceBrickPool.BrickResolution;

                    Assert.Equal(pool[slot, x, y, z].Value(), pool.Sample(slot, local).Value(), 4);
                }
            }
        }
    }

    /// <summary>Between two probes the answer is the average, and validity comes along.</summary>
    [Fact]
    public void SamplingBetweenTwoProbesIsTheirBlend() {
        var pool = new IrradianceBrickPool(new(1));

        Assert.True(pool.TryAllocate(out var slot));

        pool[slot, 0, 0, 0] = new(Probes.Of(0f).Radiance, 1f, 1f);
        pool[slot, 1, 0, 0] = new(Probes.Of(4f).Radiance, 0f, 0.5f);

        var middle = pool.Sample(slot, new(0.125f, 0f, 0f));

        Assert.Equal(2f, middle.Value(), 5);
        Assert.Equal(0.5f, middle.Validity, 5);
        Assert.Equal(0.75f, middle.SunShadow, 5);
    }

    /// <summary>
    ///     <b>Where a sample reads, stated as arithmetic.</b> Every texel of the pool is filled with a
    ///     linear function of its own coordinate, so an interpolated sample has a closed form — and a
    ///     slot origin, a probe spacing or a half-texel dropped anywhere shows up as a number that is
    ///     wrong by exactly the mistake.
    /// </summary>
    [Fact]
    public void SamplingIsExactForALinearFunctionOfTexelCoordinates() {
        var pool = Filled(out var slot);
        var origin = pool.OriginOf(slot);

        foreach (var local in (Vector3[]) [
            new(0f), new(1f), new(0.3f, 0.7f, 0.1f), new(0.5f), new(0.99f, 0.01f, 0.5f)
        ]) {
            var texel = new Vector3(origin.X, origin.Y, origin.Z)
                + (local * IrradianceBrickPool.BrickResolution);

            Assert.Equal(Texel(texel), pool.Sample(slot, local).Value(), 3);
        }
    }

    /// <summary>
    ///     <b>The convention the CPU and the shader have to share, written down once.</b> A texture
    ///     coordinate scaled by the pool's resolution and shifted back by half a texel is the texel
    ///     address a hardware fetch interpolates around — and it has to be the same address
    ///     <see cref="IrradianceBrickPool.Sample" /> interpolates around. Dropping the half shifts
    ///     every probe half a texel, which is lighting subtly in the wrong place and a defect two
    ///     implementations tested separately against arithmetic would each pass.
    /// </summary>
    [Fact]
    public void TheTextureCoordinateAddressesWhatSampleReads() {
        var pool = Filled(out var slot);
        var origin = pool.OriginOf(slot);
        var resolution = pool.TexelResolution;

        foreach (var local in (Vector3[]) [
            new(0f), new(1f), new(0.3f, 0.7f, 0.1f), new(0.5f)
        ]) {
            var coordinate = pool.TextureCoordinate(slot, local);
            var address = (coordinate * new Vector3(resolution.X, resolution.Y, resolution.Z))
                - new Vector3(0.5f);

            var expected = new Vector3(origin.X, origin.Y, origin.Z)
                + (local * IrradianceBrickPool.BrickResolution);

            Assert.Equal(expected.X, address.X, 4);
            Assert.Equal(expected.Y, address.Y, 4);
            Assert.Equal(expected.Z, address.Z, 4);
        }
    }

    /// <summary>A pool whose every texel carries a linear function of where it is.</summary>
    /// <remarks>
    ///     Every texel, not one brick's worth, and the second slot rather than the first — so a
    ///     misplaced origin reads a real number that is wrong rather than a zero that could have come
    ///     from anywhere.
    /// </remarks>
    static IrradianceBrickPool Filled(out int slot) {
        var pool = new IrradianceBrickPool(new(2, 2, 2));

        Assert.True(pool.TryAllocate(out _));
        Assert.True(pool.TryAllocate(out slot));

        for (var index = 0; index < pool.Capacity; index++) {
            var origin = pool.OriginOf(index);

            for (var z = 0; z < IrradianceBrickPool.PaddedResolution; z++) {
                for (var y = 0; y < IrradianceBrickPool.PaddedResolution; y++) {
                    for (var x = 0; x < IrradianceBrickPool.PaddedResolution; x++) {
                        pool[index, x, y, z] = Probes.Of(Texel(new(origin.X + x, origin.Y + y, origin.Z + z)));
                    }
                }
            }
        }

        return pool;
    }

    /// <summary>What a texel at a coordinate holds — linear, so trilinear reproduces it exactly.</summary>
    static float Texel(Vector3 coordinate) =>
        coordinate.X + (10f * coordinate.Y) + (100f * coordinate.Z);
}
