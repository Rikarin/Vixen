// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Xunit;

namespace Vixen.Graphics.Vulkan.Tests;

/// <summary>
///     The driver's own pipeline cache: the header check that decides whether a blob may be reused,
///     and the round trip that proves one survives a run.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Measured as a count and never as a clock.</b> What a warm cache buys is work the
///         driver does not redo, and the only honest reading of that available through the API is how
///         many bytes of a previous run's blob this device was seeded with — zero when the feature
///         does not work, whatever the frame time says. A millisecond budget taken on a developer's
///         machine measures the machine, which is this repository's largest flake source.
///     </para>
///     <para>
///         The header half needs no device at all and is where the sabotage lands: it is pure
///         arithmetic over 32 bytes, and it is the half that decides whether a foreign blob reaches
///         <c>vkCreatePipelineCache</c> — which the specification makes undefined behaviour rather
///         than an error code.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class VulkanPipelineCacheTests {
    const uint Vendor = 0x8086;
    const uint Device = 0x1234;

    static byte[] Uuid(byte seed) {
        var uuid = new byte[VulkanPipelineCacheBlob.UuidSize];
        Array.Fill(uuid, seed);

        return uuid;
    }

    /// <summary>A blob shaped exactly as the specification says a driver writes one.</summary>
    static byte[] Blob(uint vendor, uint device, byte[] uuid, uint version = 1, int payload = 64) {
        var blob = new byte[VulkanPipelineCacheBlob.HeaderSize + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, VulkanPipelineCacheBlob.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(8), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(12), device);
        uuid.CopyTo(blob.AsSpan(16));

        return blob;
    }

    [Fact]
    public void ThisDriversOwnBlobIsAccepted() {
        var uuid = Uuid(0x5a);

        Assert.True(
            VulkanPipelineCacheBlob.Matches(Blob(Vendor, Device, uuid), Vendor, Device, uuid, out var reason),
            reason
        );

        Assert.Equal("", reason);
    }

    /// <summary>The three ways a blob belongs to somebody else, and the two ways it is not one.</summary>
    /// <remarks>
    ///     Each is a real case rather than a shape of test: another machine's GPU (vendor and device),
    ///     the same GPU after a driver update (the UUID — the common one), a file truncated by a
    ///     crash mid-write, and a byte sequence that was never a cache at all.
    /// </remarks>
    [Fact]
    public void EveryBlobThatBelongsToSomebodyElseIsRefused() {
        var mine = Uuid(0x5a);

        Assert.False(
            VulkanPipelineCacheBlob.Matches(Blob(0x10DE, Device, mine), Vendor, Device, mine, out var vendor)
        );

        Assert.Contains("vendor", vendor, StringComparison.Ordinal);

        Assert.False(
            VulkanPipelineCacheBlob.Matches(Blob(Vendor, 0x4321, mine), Vendor, Device, mine, out var device)
        );

        Assert.Contains("device", device, StringComparison.Ordinal);

        Assert.False(
            VulkanPipelineCacheBlob.Matches(Blob(Vendor, Device, Uuid(0x11)), Vendor, Device, mine, out var driver)
        );

        Assert.Contains("driver build", driver, StringComparison.Ordinal);

        Assert.False(
            VulkanPipelineCacheBlob.Matches(Blob(Vendor, Device, mine, version: 2), Vendor, Device, mine, out var old)
        );

        Assert.Contains("header version", old, StringComparison.Ordinal);

        Assert.False(
            VulkanPipelineCacheBlob.Matches(Blob(Vendor, Device, mine).AsSpan(0, 20), Vendor, Device, mine, out var cut)
        );

        Assert.Contains("header alone", cut, StringComparison.Ordinal);

        Assert.False(VulkanPipelineCacheBlob.Matches([], Vendor, Device, mine, out _));
    }

    /// <summary>
    ///     ⚠ A blob whose header claims to be longer than the file, which is what a write interrupted
    ///     by a full disk leaves behind — and which the vendor and UUID comparisons alone would wave
    ///     through, because those bytes are still this driver's.
    /// </summary>
    [Fact]
    public void AHeaderLongerThanTheFileIsRefused() {
        var uuid = Uuid(0x5a);
        var blob = Blob(Vendor, Device, uuid);
        BinaryPrimitives.WriteUInt32LittleEndian(blob, (uint)blob.Length + 1);

        Assert.False(VulkanPipelineCacheBlob.Matches(blob, Vendor, Device, uuid, out var reason));
        Assert.Contains("claims to be", reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A device writes what the driver learned, the next device on the same machine starts from
    ///     it, and both are counted in bytes rather than timed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three devices, because two of them are the instrument. The first compiles nothing, so
    ///         its blob is what this driver writes for an <em>empty</em> cache — 36 bytes on
    ///         MoltenVK, a number no test may hard-code. The second compiles one pipeline, and its
    ///         blob must be larger: that difference, measured on the same machine within the same
    ///         test, is the whole claim, and it is what goes back to 36 the moment a
    ///         <c>vkCreate*Pipelines</c> call passes <c>VK_NULL_HANDLE</c> again. The third is
    ///         handed the second's file and reports how much of it the driver took back.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A driver is allowed to store nothing per pipeline</b> — MoltenVK is exactly the
    ///         shape where that would be plausible, since its "compile" produces a Metal library it
    ///         caches itself — so that outcome goes through <see cref="VulkanRequirement" /> rather
    ///         than through <c>Assert.Fail</c>: a skip on a developer's machine, and a failure
    ///         wherever <c>VIXEN_REQUIRE_VULKAN</c> says a driver was promised. Naming the adapter is
    ///         part of the answer, because the measurement is only true of the one it was taken on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ACacheWrittenByOneDeviceSeedsTheNext() {
        var directory = Directory.CreateTempSubdirectory("vixen-pipeline-cache");
        var empty = Path.Combine(directory.FullName, "empty.vkcache");
        var path = Path.Combine(directory.FullName, "pipelines.vkcache");

        try {
            VulkanRequirement.Available(
                VulkanDevice.TryCreate(new() { PipelineCachePath = empty }, out var cold, out var reason),
                reason ?? "no Vulkan"
            );

            string adapter;

            using (var owned = cold!) {
                adapter = owned.Adapter.Name;

                // Nothing was on disk, so both runs below start cold by construction — the "false
                // before the work" half of every assertion in this test.
                Assert.Equal(0, owned.PipelineCacheSeedBytes);
            }

            Assert.True(VulkanDevice.TryCreate(new() { PipelineCachePath = path }, out var first, out var again), again);

            using (var owned = first!) {
                Assert.Equal(0, owned.PipelineCacheSeedBytes);
                Compile(owned);
            }

            Assert.True(File.Exists(empty), $"no pipeline cache was written to {empty} on '{adapter}'");
            Assert.True(File.Exists(path), $"no pipeline cache was written to {path} on '{adapter}'");

            var nothing = new FileInfo(empty).Length;
            var learned = new FileInfo(path).Length;

            VulkanRequirement.Available(
                learned > nothing,
                $"'{adapter}' stores nothing in a pipeline cache: one pipeline made it {learned} bytes "
                + $"and no pipelines made it {nothing}"
            );

            Assert.True(
                VulkanDevice.TryCreate(new() { PipelineCachePath = path }, out var second, out var third),
                third
            );

            using var warm = second!;

            VulkanRequirement.Available(
                warm.PipelineCacheSeedBytes > 0,
                $"'{adapter}' wrote a {learned}-byte cache and would not take it back"
            );

            Assert.Equal((int)learned, warm.PipelineCacheSeedBytes);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>
    ///     ⚠ A blob from another GPU is discarded and the device still opens.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is not a slow frame: handing a foreign blob to
    ///     <c>vkCreatePipelineCache</c> is undefined behaviour, so the version of this feature
    ///     without the header check crashes on the first boot after a driver update rather than
    ///     reporting anything.
    /// </remarks>
    [Fact]
    public void AForeignCacheIsDiscardedRatherThanHandedToTheDriver() {
        var directory = Directory.CreateTempSubdirectory("vixen-pipeline-cache");
        var path = Path.Combine(directory.FullName, "pipelines.vkcache");

        try {
            File.WriteAllBytes(path, Blob(0x10DE, 0x2204, Uuid(0xAB), payload: 4096));

            VulkanRequirement.Available(
                VulkanDevice.TryCreate(new() { PipelineCachePath = path }, out var device, out var reason),
                reason ?? "no Vulkan"
            );

            using var owned = device!;
            Assert.Equal(0, owned.PipelineCacheSeedBytes);
        } finally {
            directory.Delete(true);
        }
    }

    /// <summary>One pipeline, so that the driver has something to have learned.</summary>
    static void Compile(VulkanDevice device) {
        var shader = device.CreateShader(ShaderStage.Compute, TestShaders.Compute, "multiply");

        var setLayout = device.CreateDescriptorSetLayout(new(
            DescriptorSetSlot.PerFrame,
            [new(0, DescriptorKind.StorageBuffer, ShaderStage.Compute)],
            "data"
        ));

        var layout = device.CreatePipelineLayout(new(
            [setLayout],
            [new(ShaderStage.Compute, 0, sizeof(uint))],
            "multiply layout"
        ));

        device.CreateComputePipeline(new(shader, layout, "multiply"));
    }
}
