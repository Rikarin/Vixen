// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D14's second prediction, as a value rather than as a sentence in a report.</summary>
/// <remarks>
///     ⚠ <b>This suite is the evidence for a gap, so its own instrument is the thing to check
///     first.</b> A test that only asserted "no device" against a bare <c>PluginServices</c> would
///     pass on the day a host started publishing one, because it never asked the second question.
///     Both branches are exercised, and the one that needs a device gets a real
///     <see cref="IGraphicsDevice" /> — the Null backend, which is enough because nothing here
///     dispatches anything.
/// </remarks>
public class TexturePreviewTests {
    [Fact]
    public void A_host_that_publishes_no_device_is_named_as_the_obstacle() {
        var services = new PluginServices();

        Assert.Equal(TexturePreviewBlocker.NoDevice, TexturePreview.Blocking(services));
        Assert.Contains("IGraphicsDevice", TexturePreview.Describe(TexturePreviewBlocker.NoDevice), StringComparison.Ordinal);
    }

    /// <summary>⚠ And with a device it is still blocked, on the half nobody predicted.</summary>
    /// <remarks>
    ///     <c>TextureGraphCompiler</c> is <c>internal</c> to <c>Vixen.Editor.TextureGraph</c>, whose
    ///     <c>InternalsVisibleTo</c> names only its own test project — so publishing a device would
    ///     not by itself give this plugin a picture. Doc 48 § D14 named the device and did not name
    ///     this, which is what makes it a finding rather than a restatement.
    /// </remarks>
    [Fact]
    public void A_host_that_publishes_one_is_still_blocked_on_the_compiler() {
        using var device = new NullDevice();

        var services = new PluginServices().Add<IGraphicsDevice>(device);

        Assert.Equal(TexturePreviewBlocker.NoCompiler, TexturePreview.Blocking(services));
        Assert.Contains(
            "TextureGraphCompiler",
            TexturePreview.Describe(TexturePreviewBlocker.NoCompiler),
            StringComparison.Ordinal
        );
    }

    /// <summary>
    ///     The editor's own host is the one this claim is about, and it publishes no device.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Asserted here rather than in <c>Vixen.Editor.App.Tests</c>, because this assembly may
    ///     not reference the application.</b> What it can say is the half that matters to a plugin:
    ///     the contract has no way to hand one over that does not go through
    ///     <c>PluginServices.Add</c>, so a host that has not called it publishes none, and
    ///     <c>Require</c> refuses with a sentence rather than a null reference. The line that would
    ///     close it is one <c>.Add(device)</c> in <c>EditorApplication.PluginPoints</c>.
    /// </remarks>
    [Fact]
    public void The_contract_refuses_a_device_by_name_when_the_host_published_none() {
        var services = new PluginServices();

        Assert.False(services.Contains<IGraphicsDevice>());

        var refusal = Assert.Throws<PluginException>(services.Require<IGraphicsDevice>);

        Assert.Contains("IGraphicsDevice", refusal.Message, StringComparison.Ordinal);
    }
}
