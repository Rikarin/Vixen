// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;

namespace Vixen.Editor.Texturing;

/// <summary>Why the preview pane is empty, or that it is not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This enum used to have two members and no third, "because there is no third state in
///         this build".</b> There is one now: <see cref="IEditorGraphics" /> is published, the pane
///         evaluates a plan on the editor's own device and shows the result, and
///         <see cref="None" /> is what it reports. The two remaining members are the two ways a host
///         can still have nothing to draw with, and they are distinguished because the cures are
///         different people's.
///     </para>
/// </remarks>
enum TexturePreviewBlocker {
    /// <summary>Nothing is in the way: the pane shows what the device produced.</summary>
    None,

    /// <summary>This host publishes no <see cref="IEditorGraphics" />, so nothing can be dispatched.</summary>
    /// <remarks>
    ///     What a host that is not the editor looks like — a test, a tool embedding the shell. The
    ///     editor itself publishes one from <c>EditorApplication.PluginPoints</c>.
    /// </remarks>
    NoGraphics,

    /// <summary>There is a graphics service and it has no device right now.</summary>
    /// <remarks>
    ///     ⚠ <b>A separate state from <see cref="NoGraphics" />, and asking for it is what refuted
    ///     the claim this panel used to make.</b> <c>TexturingModule</c> read the answer once at
    ///     activation, on the grounds that "a host does not start publishing a device halfway
    ///     through a session" — and the editor does exactly that: it builds its
    ///     <c>PluginHost</c> in its constructor and acquires a device when the window can present,
    ///     which is afterwards, and releases it when the window goes. So the question is asked every
    ///     time the pane is drawn. <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>.
    /// </remarks>
    NoDevice
}

/// <summary>What a texture-graph panel can and cannot show in this host.</summary>
/// <remarks>
///     ⚠ <b>Asked of the services rather than assumed, because the answer is the host's and moves.</b>
///     A panel that hard-coded "no device" would keep saying so on the day one is published — which
///     is the whole failure mode of a feature that reports a gap instead of testing for it.
/// </remarks>
static class TexturePreview {
    /// <summary>What stops this host previewing a graph.</summary>
    /// <param name="graphics">What the host published, or <see langword="null" /> for nothing.</param>
    /// <returns>The obstacle, or <see cref="TexturePreviewBlocker.None" />.</returns>
    public static TexturePreviewBlocker Blocking(IEditorGraphics? graphics) =>
        graphics is null
            ? TexturePreviewBlocker.NoGraphics
            : graphics.Device is null
                ? TexturePreviewBlocker.NoDevice
                : TexturePreviewBlocker.None;

    /// <summary>What stops this host previewing a graph, asked of the whole service table.</summary>
    /// <param name="services">What the host published.</param>
    /// <returns>The obstacle, or <see cref="TexturePreviewBlocker.None" />.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    public static TexturePreviewBlocker Blocking(PluginServices services) {
        ArgumentNullException.ThrowIfNull(services);

        return Blocking(services.TryGet<IEditorGraphics>(out var graphics) ? graphics : null);
    }

    /// <summary>What to put under the pane.</summary>
    /// <param name="blocker">What is in the way, if anything.</param>
    /// <returns>A sentence naming it and what would close it.</returns>
    /// <remarks>
    ///     ⚠ <b>Each sentence names the change rather than apologising.</b> A reader of this panel is
    ///     either the person who would make that change or the person who has to report it, and
    ///     "preview unavailable" serves neither. The <see cref="TexturePreviewBlocker.None" />
    ///     sentence says what is on screen <i>and</i> what it is not, because a base layer that
    ///     claimed to be the wired graph would hide the one gap left.
    /// </remarks>
    public static string Describe(TexturePreviewBlocker blocker) =>
        blocker switch {
            TexturePreviewBlocker.None =>
                "Preview: the graph's base layer, evaluated on this editor's device. ⚠ Not the wired "
                + "graph — TextureGraphCompiler is internal to Vixen.Editor.TextureGraph, so this "
                + "plugin can offer every node and cannot compile what you wire (#738).",
            TexturePreviewBlocker.NoGraphics =>
                "No preview: this host publishes no IEditorGraphics to plugins, so nothing here can "
                + "dispatch a kernel. The editor publishes one from EditorApplication.PluginPoints.",
            TexturePreviewBlocker.NoDevice =>
                "No preview: this editor has no graphics device right now — it is headless, or the "
                + "window has not come up yet. The pane fills in when one arrives.",
            _ => throw new ArgumentOutOfRangeException(nameof(blocker), blocker, "Not a blocker this build knows.")
        };
}
