// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Graphics;

namespace Vixen.Editor.Texturing;

/// <summary>Why the preview pane is empty. There is no member for "it is not".</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two members and no third, because there is no third state in this build.</b> An
///         enum with a <c>None</c> nobody can reach would read as though the feature worked
///         somewhere, which is the shape of claim doc 48 § D14 exists to test. When either half
///         below is closed this type grows a member and <see cref="TexturePreview.Blocking" /> grows
///         a branch — and until then the panel says which of the two it is looking at, by name.
///     </para>
/// </remarks>
enum TexturePreviewBlocker {
    /// <summary>This host publishes no <see cref="IGraphicsDevice" />, so nothing can be dispatched.</summary>
    /// <remarks>
    ///     <b>Doc 48 § D14's second prediction, confirmed.</b> <c>EditorApplication.PluginPoints</c>
    ///     publishes the project, the scene, the drawers, the importers, the contribution registry,
    ///     the editing state, the work plane, two mesh services, the mesh-map baker, the shown scene,
    ///     the shown view, the deploy target, the asset-editor registry, the reload host and the
    ///     plugin host itself — and no device. So a third party cannot write a plugin that draws,
    ///     which is a real gap in the extensibility claim rather than an oversight in this panel —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>.
    /// </remarks>
    NoDevice,

    /// <summary>There is a device, and no way to turn this graph into a plan to run on it.</summary>
    /// <remarks>
    ///     <c>TextureGraphCompiler</c> is <c>internal</c> to <c>Vixen.Editor.TextureGraph</c>, whose
    ///     <c>InternalsVisibleTo</c> names its own test project alone. The generated
    ///     <c>NodeTypes.Register</c> is public, so the node <i>library</i> crosses the boundary and
    ///     the compiler does not — <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>.
    /// </remarks>
    NoCompiler
}

/// <summary>What a texture-graph panel can and cannot show in this host.</summary>
/// <remarks>
///     ⚠ <b>Asked of the services rather than assumed, because the answer is the host's and moves.</b>
///     A panel that hard-coded "no device" would keep saying so on the day one is published — which
///     is the whole failure mode of a feature that reports a gap instead of testing for it.
/// </remarks>
static class TexturePreview {
    /// <summary>What stops this host previewing a graph.</summary>
    /// <param name="services">What the host published.</param>
    /// <returns>The nearer of the two obstacles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services" /> is null.</exception>
    /// <remarks>
    ///     The device first, because it is the one a host can fix in a line and the one doc 36 § F2
    ///     was written to find. A host that publishes one still gets an empty pane, and then the
    ///     sentence names the other half rather than repeating the one that has been dealt with.
    /// </remarks>
    public static TexturePreviewBlocker Blocking(PluginServices services) {
        ArgumentNullException.ThrowIfNull(services);

        return services.Contains<IGraphicsDevice>() ? TexturePreviewBlocker.NoCompiler : TexturePreviewBlocker.NoDevice;
    }

    /// <summary>What to put under the empty pane.</summary>
    /// <param name="blocker">What is in the way.</param>
    /// <returns>A sentence naming it and what would close it.</returns>
    /// <remarks>
    ///     ⚠ <b>Each sentence names the change rather than apologising.</b> A reader of this panel is
    ///     either the person who would make that change or the person who has to report it, and
    ///     "preview unavailable" serves neither.
    /// </remarks>
    public static string Describe(TexturePreviewBlocker blocker) =>
        blocker switch {
            TexturePreviewBlocker.NoDevice =>
                "No preview: this editor publishes no IGraphicsDevice to plugins, so nothing here can "
                + "dispatch a kernel. Publishing one in EditorApplication.PluginPoints is the fix.",
            TexturePreviewBlocker.NoCompiler =>
                "No preview: TextureGraphCompiler is internal to Vixen.Editor.TextureGraph, so this "
                + "plugin can offer every node and cannot compile what you wire. Making the compiler "
                + "public is the fix.",
            _ => throw new ArgumentOutOfRangeException(nameof(blocker), blocker, "Not a blocker this build knows.")
        };
}
