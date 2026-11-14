// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Texturing.Layers;
using Xunit;

namespace Vixen.Texturing.Tests;

/// <summary>A stack's format, which a build reads up to and refuses past (Patina #279).</summary>
public sealed class LayerStackYamlVersionTests {
    /// <summary>
    ///     ⚠ A stack at a later format is refused with what it was written at, and not read as far as it goes: a host
    ///     that saved what it read would write it back at this build's format and drop what was new.
    /// </summary>
    [Fact]
    public void A_stack_at_a_later_format_is_refused_with_its_format() {
        var later = LayerStackAsset.CurrentVersion + 1;
        var text = LayerStackYaml.Write(new() { Name = "Later", Version = later });

        var refused = Assert.Throws<LayerStackNewerException>(() => LayerStackYaml.Read(text));

        Assert.Equal(later, refused.Written);
        Assert.Equal(LayerStackAsset.CurrentVersion, refused.Reads);
        Assert.IsAssignableFrom<NotSupportedException>(refused);
    }

    /// <summary>The format this build writes, and one with no version at all, read as they always did.</summary>
    [Fact]
    public void A_stack_at_this_format_or_none_reads() {
        var text = LayerStackYaml.Write(new() { Name = "Now" });

        Assert.Equal(LayerStackAsset.CurrentVersion, LayerStackYaml.Read(text).Version);
        Assert.Equal("Bare", LayerStackYaml.Read("name: Bare\n").Name);
    }
}
