// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>
///     User channels (Patina #141): maps that mean nothing to a material, which a person names and uses for whatever a
///     target needs.
/// </summary>
public sealed class UserChannelTests {
    /// <summary>A user channel's name and its colour flag survive the stack file, and a default writes neither.</summary>
    [Fact]
    public void A_user_channel_keeps_its_name_and_whether_it_is_a_colour() {
        LayerStackAsset stack = new() {
            Name = "Crate",
            Sets = [
                new() {
                    Name = "Body",
                    Channels = [
                        new() { Usage = "baseColor" },
                        new() { Usage = "user0", Name = "Dirt: where it gathers" },
                        new() { Usage = "user3", Name = "Tint", Colour = true }
                    ]
                }
            ]
        };

        var text = LayerStackYaml.Write(stack);
        var read = LayerStackYaml.Read(text).Sets[0].Channels;

        Assert.Equal(["", "Dirt: where it gathers", "Tint"], read.Select(static channel => channel.Name));
        Assert.Equal([false, false, true], read.Select(static channel => channel.Colour));
        // The stack's and the set's names are plain; only a channel's, a person's words, is double-quoted.
        Assert.Equal(2, text.Split("name: \"").Length - 1);
        Assert.Equal(2, text.Split("colour:").Length);
    }

    /// <summary>
    ///     Every user channel is a usage an <c>Output</c> node takes, so a stack compiles one to a map of that name, and
    ///     each starts black, as nothing painted.
    /// </summary>
    [Fact]
    public void A_set_with_a_user_channel_compiles_it_to_a_map_of_its_usage() {
        TextureSetAsset set = new() {
            Name = "Body", Channels = [new() { Usage = "baseColor" }, new() { Usage = "user2", Name = "Wetness" }]
        };
        LayerStackAsset stack = new() { Name = "Crate", Sets = [set] };

        var compilation = LayerStackCompiler.Compile(stack, set);

        Assert.True(compilation.Succeeded, string.Join("; ", compilation.Diagnostics.Select(static d => d.Message)));
        Assert.Contains("user2", compilation.Outputs.Select(static output => output.Usage));
        Assert.All(
            ChannelAsset.UserUsages,
            static usage => Assert.Equal([0f, 0f, 0f, 1f], ChannelAsset.Untouched(usage)!)
        );
        Assert.False(ChannelAsset.IsUser("mask"));
    }
}
