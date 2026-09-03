// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.IO.Watch;
using Vixen.Editor.AssetEditors.Frame;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.App;

/// <summary>What a compositor-driven viewport is fed from.</summary>
/// <remarks>
///     <para>
///         <b>Three things that were built and never constructed.</b> <see cref="EditorEffects" /> is a
///         complete Raven-compile-on-demand tier with a project disk cache whose only callers were its
///         own tests; <see cref="EditorWorldRenderer" /> is the standard renderer over a scene
///         document's world; and the two extraction systems are public methods documented "so a test, a
///         tool or an editor can draw a scene without standing up a runner", which no editor called.
///         This file is the calling.
///     </para>
///     <para>
///         ⚠ <b>No <c>EngineLoop</c> <i>here</i>, and that is still not a gap.</b> A world is a
///         document until play mode says otherwise, and <see cref="ResolveTransforms" /> resolves the
///         one system an editing frame needs by hand for exactly that reason. Play mode does own a
///         loop now — <c>PlayModeController.Loop</c> — and these two extractions are deliberately
///         <i>not</i> in it: <c>MeshExtractionSystem</c> writes <c>RenderHandle</c> structurally and
///         claims geometry residency per entity, so registering it would run both twice a frame. What
///         a loop would add on this side is a scheduler for two calls whose order is one line of this
///         file.
///     </para>
///     <para>
///         ⚠ <b>Nothing here draws.</b> A frame document, a render target and a pane to present it are
///         the next piece of work; every input below is assertable without one, which is why they are
///         separable at all.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The editor's shader variants, once there is a device to load them onto.</summary>
    EditorEffects? effects;

    /// <summary>The standard renderer over the open scene, on the same terms.</summary>
    EditorWorldRenderer? frame;

    /// <summary>The variant tier, or null while the host has no device.</summary>
    /// <remarks>Internal, for the harness and for the panel that will report its refusal.</remarks>
    internal EditorEffects? Effects => effects;

    /// <summary>The renderer and its extraction, or null while the host has no device.</summary>
    internal EditorWorldRenderer? Frame => frame;

    /// <summary>Builds or tears down everything device-shaped, as the host acquires and loses one.</summary>
    /// <param name="device">The device, or null for a host that is releasing.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A refusal rather than a throw, and it is logged rather than swallowed.</b> A
    ///         project whose shaders do not parse is an ordinary state of a project — it is the state
    ///         somebody opens the editor to get out of — so the editor comes up and says why the
    ///         viewport cannot shade. An editor that refused to start would be one nobody could use to
    ///         fix the shader.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mesh source is the project's import cache, not a content build's catalog.</b>
    ///         It is the same source the tool renderer already reads — see <see cref="SceneGeometry" />
    ///         — so the compositor path and the tool path cannot disagree about what a mesh is.
    ///     </para>
    /// </remarks>
    void AttachRenderer(IGraphicsDevice? device) {
        frame?.Dispose();
        frame = null;

        effects?.Dispose();
        effects = null;

        if (device is null) {
            return;
        }

        try {
            effects = new EditorEffects(device, project);
            frame = new EditorWorldRenderer(device, effects.System, SceneGeometry);
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            log.Write(LogLevel.Warning, $"The viewport's renderer could not be built. {failure.Message}");

            return;
        }

        if (effects.Refusal is { } refusal) {
            log.Write(LogLevel.Warning, $"No shader variants: {refusal}");
        }

        // ⚠ Before the degradation is read, because mounting is what decides which sentence it is —
        // and before the view modes, because a pane registering a tree is the first thing that draws.
        MountContent();

        if (frame.Degraded is { } degraded) {
            log.Write(LogLevel.Information, degraded);
        }

        RegisterViewModes();
    }

    /// <summary>Hands the renderer the project's content, so a material means what it names.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The other end of <see cref="EditorContent" />, which nothing outside its own tests
    ///         had ever constructed.</b> <c>WorldRenderer.Mount</c> is the only thing in the engine
    ///         that builds an <c>IMaterialSource</c>, a texture source, a vfx source and the terrain
    ///         seams, and it takes exactly one argument: an <c>AssetManager</c>. Nothing in the editor
    ///         produced one, so the viewport painted every drawable with
    ///         <c>EditorWorldRenderer.CompileFallback</c>'s grey metal-roughness surface whatever
    ///         material it named — and reported it, honestly and unread, through
    ///         <c>EditorWorldRenderer.Degraded</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A project with no import is not a failure and must not be treated as one.</b> It is
    ///         the state every new project is in; <c>EditorContent.Refusal</c> says so and the viewport
    ///         stays on the fallback until somebody imports. That is why this is a call that may do
    ///         nothing rather than a precondition on the renderer being built at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called from two places and it has to be both.</b> The device can arrive before a
    ///         project has ever been imported — cold start — and an import can finish long after the
    ///         renderer was built. Either one alone leaves the case the other covers permanently grey.
    ///     </para>
    /// </remarks>
    void MountContent() {
        if (frame is null || projectContent.Assets is not { } assets) {
            return;
        }

        frame.Mount(assets);
    }

    /// <summary>Tells every open pane which of its view modes a compositor draws.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The whole of "a view mode is a compositor", from the side that knows both halves.</b>
    ///         The trees belong to the renderer and the modes belong to the pane, and this is the only
    ///         object that holds a reference to each — which is why it is here rather than in the host
    ///         or in <c>ViewportLayout</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called from three places and it has to be all three.</b> The renderer arrives
    ///         after the panes on a cold start and before them on every rearrangement, and a rebuilt
    ///         frame replaces trees that panes are still holding — so it runs when the device arrives,
    ///         when the arrangement changes, and after a reload. A pane that missed it is one whose
    ///         View menu still works, still persists, and never changes a pixel.
    ///     </para>
    /// </remarks>
    internal void RegisterViewModes() {
        for (var index = 0; index < Viewports.Count; index++) {
            var pane = Viewports[index];

            // ⚠ Cleared first: a rebuild may declare fewer modes than the build before it, and a
            // registration that outlived its tree names a node of a compositor nothing else refers
            // to. See `ViewModes.Clear`.
            pane.Modes.Clear();

            if (frame is null) {
                continue;
            }

            // ⚠ By index, because a tree names a view and a pair of targets and both belong to the
            // pane. Registering one tree against every pane is four panes aiming one view into one
            // texture — which is what the arrangement before this looked like from the outside: four
            // panes nominally in the same mode that do not match. A pane past the document's slots
            // gets nothing here and keeps the tool renderer, which draws.
            foreach (var (mode, tree) in frame.Trees(index)) {
                pane.Modes.Register(mode, tree);
            }
        }
    }

    /// <summary>Recompiles the shader library after a <c>.rvn</c> under the project changed.</summary>
    /// <param name="changed">What the watcher drained, which is empty for an overflow.</param>
    /// <returns>Whether anything was rebuilt.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>What changed <em>is</em> used here, unlike the rescan beside it.</b> A rescan is
    ///         cheap and idempotent; a rebuild reparses every shader in the library and throws away
    ///         every variant the session has compiled, so doing it because somebody saved a texture
    ///         would be a stall per import.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overflow rebuilds.</b> Losing events is the one case where the drained list
    ///         cannot be trusted to describe what changed — the same argument the rescan makes, and it
    ///         reaches the opposite conclusion for the same reason: when in doubt, do the work.
    ///     </para>
    ///     <para>
    ///         Only the project's shaders are watched. The engine's library ships beside the editor and
    ///         nothing under the project can change it, so a developer editing <c>Shaders/Library</c>
    ///         restarts — which is what they are already doing, since it is a build output.
    ///     </para>
    /// </remarks>
    internal bool ReloadShaders(IReadOnlyList<FileChange> changed) {
        if (effects is null) {
            return false;
        }

        var wanted = changed.Count == 0;

        foreach (var change in changed) {
            if (change.Path.Extension.Equals(".rvn", StringComparison.OrdinalIgnoreCase)
                || change.OldPath.Extension.Equals(".rvn", StringComparison.OrdinalIgnoreCase)) {
                wanted = true;
                break;
            }
        }

        if (!wanted) {
            return false;
        }

        effects.Rebuild();

        log.Write(
            LogLevel.Information,
            effects.Refusal is { } refusal
                ? $"Shaders reloaded and refused: {refusal}"
                : $"Shaders reloaded: {effects.SourceCount} source(s) recompile on demand."
        );

        return true;
    }

    /// <summary>Fills the frame's object and light lists from the scene, once a frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The active scene's world, which is the one <see cref="ResolveTransforms" /> resolved.</b>
    ///     A scene or prefab opened as an asset has a world of its own whose transforms nothing here
    ///     brings up to date, so extracting from it would place every object at the identity — and a
    ///     second <c>WorldTransform</c> pass over every open document is a cost the viewport does not
    ///     yet have a reason to pay.
    /// </remarks>
    void ExtractFrame() => frame?.Extract(scene.World);

    /// <summary>What the composed pane's last frame reported, so a repeat is not logged twice.</summary>
    string? reportedDegradations;

    /// <summary>Says out loud what the frame drew differently than the document asked for.</summary>
    /// <param name="degradations">
    ///     <c>GraphicsCompositor.Degradations</c>' pairs, in tree order. Empty is the normal case and
    ///     is reported once, so that a reader sees the pane recover as well as fail.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The difference between a preview that is wrong and one that says it is wrong.</b>
    ///         A node that declined to run draws a picture that is plausible and is not the one the
    ///         document describes — a reflection pass with no effect system, a clipmap with no field —
    ///         and nothing else in the frame reports it. Every renderer already had the answer and it
    ///         was collectable from nowhere until <c>Degradations</c> existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>On change rather than per frame</b>, because this is called from the record loop
    ///         and a reason that held for a minute would otherwise be three thousand console lines.
    ///         <see cref="EditorWorldRenderer.Degraded" /> is the separate half and is logged once,
    ///         where the renderer is built: it is a fact about the host before any document is
    ///         loaded, so no node could be the condition of it.
    ///     </para>
    /// </remarks>
    internal void ReportDegradations(IReadOnlyList<(string Node, string Reason)> degradations) {
        ArgumentNullException.ThrowIfNull(degradations);

        var reported = degradations.Count == 0
            ? string.Empty
            : string.Join("; ", degradations.Select(pair => $"{pair.Node}: {pair.Reason}"));

        if (reported == reportedDegradations) {
            return;
        }

        var first = reportedDegradations is null;

        reportedDegradations = reported;

        if (reported.Length == 0) {
            // Nothing to say the first time there was never anything wrong; a recovery is worth a
            // line, because it is what tells a reader the earlier one stopped applying.
            if (!first) {
                log.Write(LogLevel.Information, "The viewport's frame is drawing everything it was asked for.");
            }

            return;
        }

        log.Write(LogLevel.Warning, $"The viewport's frame drew something other than the document asked for. {reported}");
    }

    /// <summary>The frame document the viewport is drawing, or null for the editor's own.</summary>
    StandardFrameDocument? authored;

    /// <summary>Starts drawing the panes through a frame document somebody opened, and keeps up with it.</summary>
    /// <param name="document">The document.</param>
    /// <remarks>
    ///     ⚠ <b>Subscribed on open rather than polled, and the event is the document's own.</b>
    ///     <c>StandardFrameDocument.Changed</c> fires from <c>Restate</c>, which both the inspector's
    ///     writes and a reload from disk go through — so a knob turned in the panel and a file saved
    ///     underneath the editor reach the pane by the same path, and neither needs a restart.
    /// </remarks>
    internal void Author(StandardFrameDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (ReferenceEquals(authored, document)) {
            return;
        }

        if (authored is not null) {
            authored.Changed -= Reframe;
        }

        authored = document;
        document.Changed += Reframe;

        Reframe(document);
    }

    /// <summary>Rebuilds the viewport's frame from the document, and says so if it will not build.</summary>
    /// <remarks>
    ///     ⚠ <b>The old frame is kept when the new one would not draw</b>, because the alternative is
    ///     an editor whose viewport goes dark while somebody is halfway through authoring the frame
    ///     that made it go dark. <c>Expanded</c> rather than <c>Document</c>: the presets are a
    ///     document rewrite and the builder applies the same transformer seam either way, so handing
    ///     over the un-expanded form would be a second opinion of it.
    /// </remarks>
    void Reframe(StandardFrameDocument document) {
        if (frame is null) {
            return;
        }

        try {
            frame.Reload(document.Expanded, scene.World);
            // ⚠ `NotSupportedException` is in this list because it is the one a *file* causes rather
            // than a bug: `CompositorBuilder.Build` throws it for a document written by another
            // version of the engine, and without it a double-click on an old `.vxcompositor` comes
            // out of `AssetEditorRegistry.Opened` and takes the editor down.
        } catch (Exception failure)
            when (failure is InvalidOperationException or CompositorBindingException or NotSupportedException) {
            log.Write(
                LogLevel.Warning,
                $"The viewport kept its own frame: '{document.Title.Value}' would not build. {failure.Message}"
            );

            return;
        }

        // ⚠ Every pane, because a rebuild replaces the trees they were registered against — see
        // `ViewModes.Clear`. A pane that kept the old registration would resolve a subtree of a
        // compositor nothing else in the editor still refers to.
        RegisterViewModes();

        log.Write(LogLevel.Information, $"The viewport is drawing '{document.Title.Value}'.");
    }

    /// <summary>Drops everything device-shaped, for the application going down.</summary>
    void DisposeFrames() {
        if (authored is not null) {
            authored.Changed -= Reframe;
            authored = null;
        }

        frame?.Dispose();
        frame = null;

        effects?.Dispose();
        effects = null;
    }
}
