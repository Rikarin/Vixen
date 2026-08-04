// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Null.Tests;

public sealed class AccelerationStructureTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    /// <summary>
    ///     A device that claims no ray tracing must refuse every acceleration-structure entry
    ///     point, or the distance-field fallback the capability exists for never gets taken.
    /// </summary>
    [Fact]
    public void ADeviceWithoutRayTracingRefusesEveryEntryPoint() {
        using var limited = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });
        var input = BottomLevelInput(limited, indexCount: 36);

        Assert.Throws<NotSupportedException>(() => limited.GetAccelerationStructureSizes(input));

        Assert.Throws<NotSupportedException>(
            () => limited.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, 1024, "Mesh"))
        );

        Assert.Throws<NotSupportedException>(
            () => limited.GetAccelerationStructureAddress(default)
        );
    }

    /// <summary>The command list refuses too, so a host that skipped its check finds out in a
    ///     test rather than on a driver.</summary>
    [Fact]
    public void ACommandListWithoutRayTracingRefusesABuild() {
        using var limited = new NullDevice(new() { Features = GraphicsDeviceFeatures.Minimum });
        var input = BottomLevelInput(limited, indexCount: 36);
        var scratch = limited.CreateBuffer(new(1024, BufferUsage.Storage, Name: "Scratch"));

        using var list = limited.BeginCommandList();

        Assert.Throws<NotSupportedException>(() => list.BuildAccelerationStructure(default, input, scratch));
    }

    /// <summary>
    ///     The synthetic sizes are arbitrary but deterministic — see the device's remarks — so the
    ///     assertion writes the arithmetic down: a caller sizing two structures from the same input
    ///     must get the same answer, and a bigger input must never get a smaller one.
    /// </summary>
    [Fact]
    public void SizesAreDeterministicAndGrowWithTheInput() {
        var small = device.GetAccelerationStructureSizes(BottomLevelInput(device, indexCount: 36));
        var again = device.GetAccelerationStructureSizes(BottomLevelInput(device, indexCount: 36));
        var large = device.GetAccelerationStructureSizes(BottomLevelInput(device, indexCount: 360));

        Assert.Equal(small, again);
        Assert.True(small.Structure > 0);
        Assert.True(small.Scratch > 0);
        Assert.True(large.Structure > small.Structure);
        Assert.True(large.Scratch > small.Scratch);
    }

    /// <summary>A top-level build is sized by its instance count, not by triangle fields it does
    ///     not read.</summary>
    [Fact]
    public void ATopLevelBuildIsSizedByItsInstances() {
        var instances = device.CreateBuffer(
            new(64 * 4, BufferUsage.AccelerationStructureInput | BufferUsage.ShaderDeviceAddress, Name: "Instances")
        );

        var two = device.GetAccelerationStructureSizes(
            new(AccelerationStructureKind.TopLevel, Instances: new(instances, 0, 2))
        );

        var four = device.GetAccelerationStructureSizes(
            new(AccelerationStructureKind.TopLevel, Instances: new(instances, 0, 4))
        );

        Assert.True(four.Structure > two.Structure);
    }

    /// <summary>
    ///     The assertion a leak test wants, extended to the new resource: a create-and-destroy
    ///     cycle comes back to where it started.
    /// </summary>
    [Fact]
    public void CreateAndDestroyRoundTrips() {
        var before = device.LiveResourceCount;
        var structure = device.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, 1024, "Mesh"));

        Assert.Equal(before + 1, device.LiveResourceCount);

        device.Destroy(structure);
        Assert.Equal(before, device.LiveResourceCount);
    }

    /// <summary>A size the device did not answer is refused at creation — the mistake is
    ///     corruption on a real backend, so here it is an exception.</summary>
    [Fact]
    public void AnInventedZeroSizeIsRefused() {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, 0, "Empty"))
        );
    }

    /// <summary>
    ///     Addresses go into instance buffers and get compared later, so they must be nonzero,
    ///     stable for the handle's lifetime, and never shared between two structures.
    /// </summary>
    [Fact]
    public void AddressesAreStableAndDistinct() {
        var first = device.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, 512, "First"));
        var second = device.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, 512, "Second"));

        var address = device.GetAccelerationStructureAddress(first);

        Assert.NotEqual(0ul, address);
        Assert.Equal(address, device.GetAccelerationStructureAddress(first));
        Assert.NotEqual(address, device.GetAccelerationStructureAddress(second));
    }

    /// <summary>An address asked of a destroyed structure is caught here, rather than being a
    ///     stale pointer a top-level build reads.</summary>
    [Fact]
    public void ADestroyedStructureHasNoAddress() {
        var structure = device.CreateAccelerationStructure(new(AccelerationStructureKind.BottomLevel, 512, "Gone"));
        device.Destroy(structure);

        Assert.Throws<ArgumentException>(() => device.GetAccelerationStructureAddress(structure));
    }

    /// <summary>A build records what an assertion is about: which structure, which level, and how
    ///     much geometry.</summary>
    [Fact]
    public void ABuildRecordsItsTargetKindAndPrimitives() {
        var input = BottomLevelInput(device, indexCount: 36);
        var sizes = device.GetAccelerationStructureSizes(input);

        var structure = device.CreateAccelerationStructure(
            new(AccelerationStructureKind.BottomLevel, sizes.Structure, "Mesh")
        );

        var scratch = device.CreateBuffer(
            new(sizes.Scratch, BufferUsage.Storage | BufferUsage.ShaderDeviceAddress, Name: "Scratch")
        );

        using var list = device.BeginCommandList();
        list.BuildAccelerationStructure(structure, input, scratch, scratchOffset: 64);
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var built = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BuildAccelerationStructure));

        Assert.Equal((long)structure.Value.Packed, built.A);
        Assert.Equal((long)AccelerationStructureKind.BottomLevel, built.B);
        Assert.Equal(12, built.C);
        Assert.Equal((long)scratch.Value.Packed, built.D);
        Assert.Equal(64, built.E);
    }

    /// <summary>A build inside a render pass is rejected everywhere — no API allows it — so the
    ///     strictest backend says so first.</summary>
    [Fact]
    public void ABuildInsideARenderPassIsRefused() {
        var input = BottomLevelInput(device, indexCount: 3);
        var sizes = device.GetAccelerationStructureSizes(input);

        var structure = device.CreateAccelerationStructure(
            new(AccelerationStructureKind.BottomLevel, sizes.Structure, "Mesh")
        );

        var scratch = device.CreateBuffer(new(sizes.Scratch, BufferUsage.Storage, Name: "Scratch"));

        var target = device.CreateTextureView(
            device.CreateTexture(new(PixelFormat.Rgba8UNorm, 8, 8, TextureUsage.ColourTarget))
        );

        using var list = device.BeginCommandList();
        list.BeginRenderPass(new([new(target)]));

        Assert.Throws<InvalidOperationException>(() => list.BuildAccelerationStructure(structure, input, scratch));
    }

    /// <summary>One bottom-level input, with the counts sizing reads filled in.</summary>
    static AccelerationStructureBuildInput BottomLevelInput(NullDevice on, int indexCount) {
        var usage = BufferUsage.AccelerationStructureInput | BufferUsage.ShaderDeviceAddress;
        var vertices = on.CreateBuffer(new(24 * 12, usage, Name: "Positions"));
        var indices = on.CreateBuffer(new(indexCount * 4L, usage, Name: "Indices"));

        return new(
            AccelerationStructureKind.BottomLevel,
            new(vertices, 0, 24, 12, indices, 0, indexCount)
        );
    }

    public void Dispose() => device.Dispose();
}
