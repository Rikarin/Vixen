// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;

namespace Vixen.Editor.App;

/// <summary>Play ▸ Options ▸ Maximise: the preference, and the arrangement stopping comes back to.</summary>
/// <remarks>
///     <para>
///         <b>Out of the application (#1296).</b> The tick, the remembered arrangement and the two halves
///         of the gesture were <c>maximiseOnPlay</c>, <c>restore</c>, <c>Remember</c> and <c>Take</c> on
///         <c>EditorApplication</c>, and all they touched of it was the scene panel's arrangement, which
///         is <see cref="ScenePanes.Arrangement" /> now. <c>PlayModule</c> builds one and follows the
///         session with it, so switching Play off takes the line and the behaviour together.
///     </para>
///     <para>
///         ⚠ <b>A preference, not session state</b> — which is why it is not on <see cref="IPlaySession" />,
///         whose remarks keep maximising out of it. It changes what the <i>next</i> Play does. It is not
///         written to disk, and it never was.
///     </para>
/// </remarks>
sealed class PlayMaximise {
    readonly ScenePanes panes;

    /// <summary>
    ///     What arrangement stopping goes back to, while a maximise is in force; held rather than assumed
    ///     to be Quad, so somebody's two-pane layout does not become four panes the first time they press Play.
    /// </summary>
    ViewportArrangement? restore;

    /// <summary>Follows the scene panel's arrangement.</summary>
    /// <param name="panes">The scene panel's model.</param>
    public PlayMaximise(ScenePanes panes) {
        ArgumentNullException.ThrowIfNull(panes);

        this.panes = panes;
    }

    /// <summary>Whether entering play maximises the scene panel.</summary>
    public bool IsOn { get; private set; }

    /// <summary>Whether a maximise made by entering play is in force.</summary>
    public bool IsMaximised => restore is not null;

    /// <summary>Flips the preference.</summary>
    public void Toggle() => IsOn = !IsOn;

    /// <summary>What entering play does: a single pane, remembering what it was.</summary>
    /// <remarks>
    ///     ⚠ <b>Skipped when the panel is already single</b>, because remembering Single would leave the
    ///     arrangement claiming a maximise when nothing was changed.
    /// </remarks>
    public void Entered() {
        if (!IsOn || panes.Arrangement == ViewportArrangement.Single) {
            return;
        }

        restore = panes.Arrangement;
        panes.Arrangement = ViewportArrangement.Single;
    }

    /// <summary>What leaving play does: back to whatever play maximised, and forgotten.</summary>
    /// <remarks>
    ///     ⚠ <b>Forgotten on the way back</b>, so a maximise undone does not stay "in force" and the next
    ///     Stop does not come back to the arrangement it is already in. Also what unloading the module
    ///     does, so switching Play off mid-session gives the panes back.
    /// </remarks>
    public void Left() {
        if (restore is not { } previous) {
            return;
        }

        restore = null;
        panes.Arrangement = previous;
    }
}
