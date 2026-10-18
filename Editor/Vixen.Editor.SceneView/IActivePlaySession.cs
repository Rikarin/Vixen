// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.SceneView;

/// <summary>The in-editor play session that is running right now, if one is.</summary>
/// <remarks>
///     <para>
///         <b><see cref="IActiveScene" />'s shape, for the services that only exist while Play is
///         pressed.</b> An <see cref="IPlaySystems" /> contribution creates its systems on Play and
///         destroys them on Stop, and hands them over with <see cref="PlaySession.Provide{T}" /> —
///         so a panel that wants the running <c>AiSystem</c> or <c>PhysicsScene</c> has to be able
///         to reach the session, and nothing let it (#470).
///     </para>
///     <para>
///         ⚠ <b>Published once and long-lived, with the answer moving under it — not the session
///         published per Play.</b> <c>PluginServices</c> has no removal, so a session put in it
///         would be a handle to a torn-down world for every reader after the first Stop: the hazard
///         <c>PlayPhysics</c> exists to avoid, one layer over. A panel asks every time it needs the
///         answer and gets <see langword="null" /> between sessions.
///     </para>
/// </remarks>
public interface IActivePlaySession {
    /// <summary>The running session, or <see langword="null" /> when nothing is playing.</summary>
    PlaySession? Current { get; }
}
