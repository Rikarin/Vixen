// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui.Documents;

/// <summary>A document's view that moves by itself — a playing timeline, a simulating effect — and is stepped once a frame.</summary>
/// <remarks>
///     <para>
///         <b>What the application finds by walking the tree</b>, so it steps a VFX graph and a
///         sequence without naming either (#1297). It named both until then — two <c>case</c> patterns
///         in <c>EditorWorlds.AuthoringUpdate</c> that compiled away silently when the asset editors
///         were dereferenced, because a type pattern on a type nobody references is no diagnostic at
///         all: doc 36 § P3 counted them as the two names the compiler does not report.
///     </para>
///     <para>
///         ⚠ <b>Found by walking rather than held.</b> A view is rebuilt every time its panel reopens,
///         so a reference to one would be a reference to a control that has left the document.
///     </para>
/// </remarks>
public interface ITickingView {
    /// <summary>Advances whatever this view plays by one frame.</summary>
    /// <param name="delta">How long the frame was.</param>
    void Tick(TimeSpan delta);
}
