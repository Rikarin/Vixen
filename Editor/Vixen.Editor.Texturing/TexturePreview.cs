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
///         <see cref="None" /> is what it reports. <see cref="NoGraphics" /> and
///         <see cref="NoDevice" /> are the two ways a host can still have nothing to draw with, and
///         they are distinguished because the cures are different people's.
///     </para>
///     <para>
///         ⚠ <b><see cref="AnotherPane" /> is not one of those, and mixing it in was
///         <a href="https://github.com/Rikarin/Vixen/issues/831">#831</a>.</b> The first three are
///         answers to "what can this host do"; the fourth is an answer to "what is this particular
///         view for", and a view that borrowed the host's answer told an editor that publishes
///         graphics that it publishes none.
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
    NoDevice,

    /// <summary>The host can draw perfectly well; this view is simply not the one that does.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one member <see cref="TexturePreview.Blocking(PluginServices)" /> never
    ///         returns, because it is not a fact about the host.</b> A view built by an
    ///         <c>IAssetEditorFactory</c> for a double-click has no <see cref="IEditorGraphics" /> of
    ///         its own — the evaluator is the module's, and two of them over one device would be two
    ///         pipeline caches — so the tab lists what is in the file and the plugin's own panel is
    ///         where the map appears. It is chosen by the caller that knows that, and there is no
    ///         service to ask.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it exists because <see cref="NoGraphics" /> is a <em>lie</em> in that
    ///         position.</b> A double-click happens in the editor, and the editor publishes graphics
    ///         — so a tab saying "this host publishes no IEditorGraphics" sends the one reader who
    ///         could act on it to look for a plugin point that is already there.
    ///         <a href="https://github.com/Rikarin/Vixen/issues/831">#831</a>.
    ///     </para>
    /// </remarks>
    AnotherPane
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
    ///     "preview unavailable" serves neither.
    ///     <para>
    ///         ⚠ <b>The <see cref="TexturePreviewBlocker.None" /> sentence named the wrong gap for
    ///         two batches, which is the failure this remark's own first line describes —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>.</b> It said
    ///         <c>TextureGraphCompiler</c> was <c>internal</c> and that the plugin therefore could
    ///         not compile a wired graph; the compiler had been public since
    ///         <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>, so the sentence sent
    ///         the one reader who could act on it to reopen a closed issue. Rewritten, it then said
    ///         truthfully that the pane showed a fixed checkerboard and named
    ///         <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>. That is closed too:
    ///         <c>TextureGraphPreview.Evaluate</c> compiles the document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is <em>not</em> here is "this graph does not compile", and that is
    ///         deliberate.</b> A blocker answers "what can this host do", so a fifth member for it
    ///         would be a fact about the document wearing a host's hat — and the useful half of that
    ///         answer is which node failed, which no enum can carry.
    ///         <see cref="TextureGraphPicture.Status" /> is where a graph's own refusal goes, built
    ///         out of the diagnostics.
    ///     </para>
    /// </remarks>
    public static string Describe(TexturePreviewBlocker blocker) =>
        blocker switch {
            TexturePreviewBlocker.None =>
                "Preview: this graph, compiled and evaluated on this editor's device.",
            TexturePreviewBlocker.NoGraphics =>
                "No preview: this host publishes no IEditorGraphics to plugins, so nothing here can "
                + "dispatch a kernel. The editor publishes one from EditorApplication.PluginPoints.",
            TexturePreviewBlocker.NoDevice =>
                "No preview: this editor has no graphics device right now — it is headless, or the "
                + "window has not come up yet. The pane fills in when one arrives.",
            TexturePreviewBlocker.AnotherPane =>
                "No preview in this tab: a tab opened by a double-click shows what is in the file, "
                + "and the picture is drawn by the Texturing plugin's own panel — Texture Graph or "
                + "Layer Stack — which is the one holding the evaluator. Two evaluators over one "
                + "device would be two pipeline caches.",
            _ => throw new ArgumentOutOfRangeException(nameof(blocker), blocker, "Not a blocker this build knows.")
        };
}
