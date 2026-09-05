// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>Doc 48 § D14's second prediction, and what closing it left behind.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This suite is the evidence for a gap, so its own instrument is the thing to check
///         first.</b> A test that only asserted "no device" against a bare <c>PluginServices</c>
///         would pass on the day a host started publishing one, because it never asked the second
///         question. All three states are exercised, and the one that needs a device gets a real
///         <see cref="IGraphicsDevice" /> — the Null backend, which is enough because nothing here
///         dispatches anything.
///     </para>
///     <para>
///         ⚠ <b>Two states rather than one, and the second is the refutation.</b> A host can publish
///         <see cref="IEditorGraphics" /> and have no device: the editor builds its plugin host in
///         its constructor and acquires a device when the window can present, so "is one published"
///         and "is there one" are different questions with different answers at different moments.
///         The module used to ask the first once, at activation.
///     </para>
/// </remarks>
public class TexturePreviewTests {
    [Fact]
    public void A_host_that_publishes_no_graphics_is_named_as_the_obstacle() {
        var services = new PluginServices();

        Assert.Equal(TexturePreviewBlocker.NoGraphics, TexturePreview.Blocking(services));
        Assert.Contains(
            "IEditorGraphics",
            TexturePreview.Describe(TexturePreviewBlocker.NoGraphics),
            StringComparison.Ordinal
        );
    }

    /// <summary>⚠ Published and empty is its own answer, not the same one.</summary>
    [Fact]
    public void A_host_whose_graphics_have_no_device_yet_says_so() {
        var services = new PluginServices().Add<IEditorGraphics>(new Graphics(null));

        Assert.Equal(TexturePreviewBlocker.NoDevice, TexturePreview.Blocking(services));
        Assert.Contains("device", TexturePreview.Describe(TexturePreviewBlocker.NoDevice), StringComparison.Ordinal);
    }

    /// <summary>And with a device nothing is in the way — which is the half #737 closed.</summary>
    /// <remarks>
    ///     ⚠ <b>The sentence still names the compiler</b>, because the picture is the graph's base
    ///     layer rather than the wired graph: <c>TextureGraphCompiler</c> is <c>internal</c> to
    ///     <c>Vixen.Editor.TextureGraph</c>, whose <c>InternalsVisibleTo</c> names only its own test
    ///     project. Doc 48 § D14 named the device and did not name this, which is what makes it a
    ///     finding rather than a restatement — <c>#738</c>.
    /// </remarks>
    [Fact]
    public void A_host_with_a_device_is_not_blocked_and_says_what_it_is_showing() {
        using var device = new NullDevice();

        var services = new PluginServices().Add<IEditorGraphics>(new Graphics(device));

        Assert.Equal(TexturePreviewBlocker.None, TexturePreview.Blocking(services));

        var sentence = TexturePreview.Describe(TexturePreviewBlocker.None);

        Assert.Contains("base layer", sentence, StringComparison.Ordinal);
        Assert.Contains("TextureGraphCompiler", sentence, StringComparison.Ordinal);
    }

    /// <summary>The contract refuses a service the host did not publish, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Asserted here rather than in <c>Vixen.Editor.App.Tests</c>, because this assembly may
    ///     not reference the application.</b> What it can say is the half that matters to a plugin:
    ///     there is no way to hand a service over that does not go through <c>PluginServices.Add</c>,
    ///     so a host that has not called it publishes none and <c>Require</c> refuses with a sentence
    ///     rather than a null reference.
    /// </remarks>
    [Fact]
    public void The_contract_refuses_the_graphics_by_name_when_the_host_published_none() {
        var services = new PluginServices();

        Assert.False(services.Contains<IEditorGraphics>());

        var refusal = Assert.Throws<PluginException>(services.Require<IEditorGraphics>);

        Assert.Contains("IEditorGraphics", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A host's graphics, as much of them as this suite needs.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing is uploaded, and that is deliberate rather than a shortcut.</b> An upload is
    ///     the host's three barriers and a copy inside a frame; a double that answered with a number
    ///     would be a double more permissive than the runtime, which is the failure mode this
    ///     repository names by that phrase. What this suite asserts is which question is asked of the
    ///     service, and the answers to that are its two states.
    /// </remarks>
    sealed class Graphics(IGraphicsDevice? device) : IEditorGraphics {
        public IGraphicsDevice? Device => device;

        public IEditorImage? Upload(int width, int height, ReadOnlySpan<byte> rgba) => null;
    }
}
