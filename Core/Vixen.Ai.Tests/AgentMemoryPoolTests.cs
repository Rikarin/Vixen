// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Ai;
using Xunit;

namespace Vixen.Ai.Tests;

public class AgentMemoryPoolTests {
    [Fact]
    public void ABlockIsZeroedOnRental() {
        var pool = new AgentMemoryPool(256);
        var first = pool.Rent(16);

        pool.Resolve(first).Fill(0xAB);
        pool.Return(first);

        var second = pool.Rent(16);

        Assert.All(pool.Resolve(second).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void AStaleHandleNamesNothing() {
        var pool = new AgentMemoryPool();
        var handle = pool.Rent(8);

        pool.Return(handle);

        Assert.False(pool.TryResolve(handle, out _));
        Assert.True(pool.Resolve(handle).IsEmpty);
        Assert.False(pool.Return(handle));
        Assert.False(pool.TryResolve(AgentMemoryHandle.Null, out _));
    }

    [Fact]
    public void ABlockOfTheSameSizeIsRecycledRatherThanCarved() {
        var pool = new AgentMemoryPool();

        for (var pass = 0; pass < 1_000; pass++) {
            pool.Return(pool.Rent(24));
        }

        Assert.Equal(1, pool.BlockCount);
        Assert.Equal(0, pool.RentedCount);
    }

    /// <summary>
    ///     The property the paged arena exists for: a span handed out earlier stays valid when the
    ///     pool grows. A single doubling arena would have moved these bytes.
    /// </summary>
    [Fact]
    public void ASpanSurvivesTheAllocationOfMorePages() {
        var pool = new AgentMemoryPool(64);
        var first = pool.Rent(32);

        MemoryMarshal.Write(pool.Resolve(first), 0x1234_5678);

        for (var pass = 0; pass < 200; pass++) {
            pool.Rent(32);
        }

        Assert.Equal(0x1234_5678, MemoryMarshal.Read<int>(pool.Resolve(first)));
        Assert.True(pool.Capacity >= 200 * 32);
    }

    [Fact]
    public void BlocksDoNotOverlap() {
        var pool = new AgentMemoryPool(128);
        var handles = new AgentMemoryHandle[40];

        for (var index = 0; index < handles.Length; index++) {
            handles[index] = pool.Rent(16);
            pool.Resolve(handles[index]).Fill((byte)(index + 1));
        }

        for (var index = 0; index < handles.Length; index++) {
            Assert.All(pool.Resolve(handles[index]).ToArray(), value => Assert.Equal((byte)(index + 1), value));
        }
    }

    [Fact]
    public void AZeroSizedBlockIsLegalAndEmpty() {
        var pool = new AgentMemoryPool();
        var handle = pool.Rent(0);

        Assert.True(pool.TryResolve(handle, out var state));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void ABlockLargerThanAPageIsRefused() {
        var pool = new AgentMemoryPool(64);

        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(65));
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Rent(-1));
    }

    [Fact]
    public void RentedCountTracksWhatIsOut() {
        var pool = new AgentMemoryPool();
        var handles = new AgentMemoryHandle[8];

        for (var index = 0; index < handles.Length; index++) {
            handles[index] = pool.Rent(8);
        }

        Assert.Equal(8, pool.RentedCount);

        foreach (var handle in handles) {
            pool.Return(handle);
        }

        Assert.Equal(0, pool.RentedCount);
    }
}
