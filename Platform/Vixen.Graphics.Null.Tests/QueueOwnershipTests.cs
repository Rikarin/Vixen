// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Null.Tests;

/// <summary>What a barrier that names two queues means, and what the backend refuses.</summary>
/// <remarks>
///     A queue-family ownership transfer is the one piece of Vulkan synchronisation that is undefined
///     rather than invalid when it is got wrong: a release without its acquire, or either half on the
///     wrong list, leaves the destination reading whatever the memory held and produces no validation
///     message anywhere. Checking the pairing on a backend with no GPU is the only place the mistake
///     is cheap.
/// </remarks>
public sealed class QueueOwnershipTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });

    public void Dispose() => device.Dispose();

    BufferHandle Buffer() => device.CreateBuffer(new(256, BufferUsage.Storage, Name: "shared"));

    /// <summary>A barrier nobody said anything about does not transfer anything.</summary>
    [Fact]
    public void AnOrdinaryBarrierNamesNoQueues() {
        var barrier = new BufferBarrier(Buffer(), ResourceState.ShaderWrite, ResourceState.ShaderRead);

        Assert.False(barrier.TransfersOwnership);
        Assert.Equal(QueueKind.Graphics, barrier.SourceQueue);
        Assert.Equal(QueueKind.Graphics, barrier.DestinationQueue);
    }

    /// <summary>A texture barrier's subresource range does not change what its queues mean.</summary>
    [Fact]
    public void ATextureBarrierCarriesItsQueuesAfterItsRange() {
        var barrier = new TextureBarrier(
            TextureHandle.Null,
            ResourceState.ShaderWrite,
            ResourceState.ShaderRead,
            2,
            1,
            0,
            0,
            QueueKind.Compute,
            QueueKind.Graphics
        );

        Assert.True(barrier.TransfersOwnership);
        Assert.Equal(2, barrier.BaseMipLevel);
    }

    /// <summary>The release half is legal on the source queue's list.</summary>
    [Fact]
    public void TheReleaseIsRecordedOnTheSourceQueue() {
        var buffer = Buffer();
        using var list = device.BeginCommandList(QueueKind.Graphics, "release");
        BufferBarrier[] barriers = [new(buffer, ResourceState.ShaderWrite, ResourceState.ShaderRead, QueueKind.Graphics, QueueKind.Compute)];

        list.Barrier(new(barriers, []));
        list.Finish();

        Assert.True(list.IsRecorded);
    }

    /// <summary>The acquire half is legal on the destination queue's list.</summary>
    [Fact]
    public void TheAcquireIsRecordedOnTheDestinationQueue() {
        var buffer = Buffer();
        using var list = device.BeginCommandList(QueueKind.Compute, "acquire");
        BufferBarrier[] barriers = [new(buffer, ResourceState.ShaderWrite, ResourceState.ShaderRead, QueueKind.Graphics, QueueKind.Compute)];

        list.Barrier(new(barriers, []));
        list.Finish();

        Assert.True(list.IsRecorded);
    }

    /// <summary>A list at neither end of the transfer records neither half of it, and is refused.</summary>
    [Fact]
    public void AThirdQueueIsRefused() {
        var buffer = Buffer();
        using var list = device.BeginCommandList(QueueKind.Transfer, "bystander");
        BufferBarrier[] barriers = [new(buffer, ResourceState.ShaderWrite, ResourceState.ShaderRead, QueueKind.Graphics, QueueKind.Compute)];

        var failure = Assert.Throws<InvalidOperationException>(() => {
            list.Barrier(new(barriers, []));
        });

        Assert.Contains("neither end", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A texture transfer is checked the same way a buffer one is.</summary>
    [Fact]
    public void AThirdQueueIsRefusedForTexturesToo() {
        using var list = device.BeginCommandList(QueueKind.Graphics, "bystander");

        TextureBarrier[] barriers = [
            new(
                TextureHandle.Null,
                ResourceState.ShaderWrite,
                ResourceState.ShaderRead,
                SourceQueue: QueueKind.Compute,
                DestinationQueue: QueueKind.Transfer
            )
        ];

        Assert.Throws<InvalidOperationException>(() => {
            list.Barrier(new([], barriers));
        });
    }
}
