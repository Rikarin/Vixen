// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     OpenPBR's layer maps (Patina #142): a coat, a sheen and transmission, which a texture set can hold as channels
///     beside the standard maps.
/// </summary>
public sealed class OpenPbrChannelTests {
    /// <summary>
    ///     Every OpenPBR layer map is a usage an <c>Output</c> node takes, so a set holding them all compiles each to a
    ///     map of its name; none is a user channel.
    /// </summary>
    [Fact]
    public void A_set_with_every_openpbr_channel_compiles_each_to_a_map_of_its_usage() {
        TextureSetAsset set = new() {
            Name = "Body",
            Channels = [
                new() { Usage = "baseColor" },
                ..ChannelAsset.OpenPbrUsages.Select(static usage => new ChannelAsset { Usage = usage })
            ]
        };
        LayerStackAsset stack = new() { Name = "Car paint", Sets = [set] };

        var compilation = LayerStackCompiler.Compile(stack, set);

        Assert.True(compilation.Succeeded, string.Join("; ", compilation.Diagnostics.Select(static d => d.Message)));
        var outputs = compilation.Outputs.Select(static output => output.Usage).ToArray();
        Assert.All(ChannelAsset.OpenPbrUsages, usage => Assert.Contains(usage, outputs));
        Assert.All(
            compilation.Outputs.Where(static output => output.Usage != "baseColor"),
            static output => Assert.True(output.OpenPbr && !output.User, output.Usage)
        );
        Assert.All(ChannelAsset.OpenPbrUsages, static usage => Assert.False(ChannelAsset.IsUser(usage)));
    }

    /// <summary>
    ///     Each starts at OpenPBR's own default, so adding one changes nothing until it is painted: no coat, sheen or
    ///     transmission, a smooth coat, a white sheen colour and a mid-grey sheen roughness.
    /// </summary>
    [Fact]
    public void Each_openpbr_channel_starts_at_openpbrs_default() {
        Assert.Equal([0f, 0f, 0f, 1f], ChannelAsset.Untouched("coat")!);
        Assert.Equal([0f, 0f, 0f, 1f], ChannelAsset.Untouched("coatRoughness")!);
        Assert.Equal([0f, 0f, 0f, 1f], ChannelAsset.Untouched("sheen")!);
        Assert.Equal([1f, 1f, 1f, 1f], ChannelAsset.Untouched("sheenColor")!);
        Assert.Equal([0.5f, 0.5f, 0.5f, 1f], ChannelAsset.Untouched("sheenRoughness")!);
        Assert.Equal([0f, 0f, 0f, 1f], ChannelAsset.Untouched("transmission")!);
        Assert.All(ChannelAsset.OpenPbrUsages, static usage => Assert.True(ChannelAsset.IsOpenPbr(usage)));
        Assert.False(ChannelAsset.IsOpenPbr("baseColor"));
    }
}
