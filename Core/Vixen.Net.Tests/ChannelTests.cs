// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Net.Tests;

/// <summary>
///     The channel contract, stated once so that everything deriving behaviour from it — the
///     reliability layer, the simulation decorator — derives it from something tested.
/// </summary>
public sealed class ChannelTests {
    [Theory]
    [InlineData(Channel.Reliable, true, true)]
    [InlineData(Channel.ReliableUnordered, true, false)]
    [InlineData(Channel.Unreliable, false, false)]
    [InlineData(Channel.Sequenced, false, true)]
    public void EachChannelPromisesWhatItsNameSays(Channel channel, bool reliable, bool ordered) {
        Assert.Equal(reliable, channel.IsReliable());
        Assert.Equal(ordered, channel.IsOrdered());
    }

    [Theory]
    [InlineData(Channel.Reliable)]
    [InlineData(Channel.ReliableUnordered)]
    [InlineData(Channel.Unreliable)]
    [InlineData(Channel.Sequenced)]
    public void OnlyAnUnreliableChannelToleratesADuplicate(Channel channel) {
        // A reliable channel deduplicates by acknowledgement and a sequenced one by sequence number,
        // so a duplicate on either is a defect rather than something the layer above must handle.
        Assert.Equal(channel is Channel.Unreliable, channel.MayDuplicate());
        Assert.Equal(!channel.IsReliable(), channel.MayDrop());
    }

    [Fact]
    public void AConnectionIdOfZeroIsNoConnection() {
        Assert.False(ConnectionId.None.IsValid);
        Assert.False(default(ConnectionId).IsValid);
        Assert.True(new ConnectionId(1).IsValid);
        Assert.Equal("none", ConnectionId.None.ToString());
        Assert.Equal("#7", new ConnectionId(7).ToString());
    }
}
