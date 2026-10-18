// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Diagnostics.Overlays;

namespace Vixen.Editor.SceneView;

/// <summary>Play mode as a service: what the transport does, callable without a window.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2's first service (#1289).</b> Play, Pause, Step and Stop used to be private
///         methods on the application, closed over its fields, so a plugin that wanted "enter play
///         the way F5 does" could not call it and a test that wanted it had to open a whole editor.
///         The transport's four commands are now names for the four verbs here, registered by
///         <c>PlayModule</c> through the same <c>PluginContext</c> a third party uses.
///     </para>
///     <para>
///         ⚠ <b>It extends <see cref="IActivePlaySession" /> rather than standing beside it.</b>
///         That seam (#470) is the read half — "is a session running, and what is it" — and was
///         already published; a second interface answering the same question would be two seams for
///         one thing. A reader that only wants the session keeps asking for the narrow one.
///     </para>
///     <para>
///         ⚠ <b>What play does to the rest of the editor is not here.</b> Maximising the viewport,
///         clearing the console, the notification and the "not running everything" report are the
///         workspace's and the diagnostics' concerns; they subscribe to <see cref="Entered" /> and
///         <see cref="Left" />, which is the callback doc 50 says the composition root supplies
///         until those contexts are services of their own.
///     </para>
/// </remarks>
public interface IPlaySession : IActivePlaySession {
    /// <summary>Editing, playing, or paused.</summary>
    PlayState State { get; }

    /// <summary>Whether a session is running, paused or not.</summary>
    bool IsPlaying { get; }

    /// <summary>The registry a session's diagnostic overlays are registered in and switched through.</summary>
    /// <remarks>
    ///     The editor's rather than a session's, because a switch has to be settable before Play is
    ///     pressed — see <c>PlayOverlaySwitches</c>.
    /// </remarks>
    DiagnosticOverlays Overlays { get; }

    /// <summary>Raised after a session has started, so the rest of the editor can follow.</summary>
    event Action<IPlaySession>? Entered;

    /// <summary>Raised after a session has stopped and the scene has been restored.</summary>
    event Action<IPlaySession>? Left;

    /// <summary>Enters play mode: snapshots the scene and starts stepping it.</summary>
    /// <returns>
    ///     Whether a session started. A press while already playing resumes rather than restarting,
    ///     and reports <see langword="false" /> because nothing was entered.
    /// </returns>
    bool Enter();

    /// <summary>Stops, restores the scene, and translates the selection back through the restore.</summary>
    /// <returns>The selection as it now reads, in the restored world's handles.</returns>
    IReadOnlyList<Entity> Leave();

    /// <summary>Pauses a running session, or resumes a paused one.</summary>
    void TogglePause();

    /// <summary>Advances a paused session by a frame, entering play paused if nothing was running.</summary>
    void StepFrame();
}
