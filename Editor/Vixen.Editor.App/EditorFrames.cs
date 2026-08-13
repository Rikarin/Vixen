// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.IO.Watch;
using Vixen.Graphics;

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
///         ⚠ <b>No <c>EngineLoop</c>, and that is not a gap.</b> The editor runs no system graph — a
///         world is a document until play mode says otherwise, and <see cref="ResolveTransforms" />
///         already resolves the one system it needs by hand for exactly that reason. What a loop would
///         add here is a scheduler for two calls whose order is one line of this file.
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

        if (frame.Degraded is { } degraded) {
            log.Write(LogLevel.Information, degraded);
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

    /// <summary>Drops everything device-shaped, for the application going down.</summary>
    void DisposeFrames() {
        frame?.Dispose();
        frame = null;

        effects?.Dispose();
        effects = null;
    }
}
