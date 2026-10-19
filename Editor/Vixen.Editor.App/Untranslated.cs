// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>A title that is the user's own name for something — a project, a file — and never a translator's word.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A null id, which <c>Strings.Get</c> answers with the source text.</b> A directory the
///         user happens to have called "Prototype" is not a string a translator should ever be shown,
///         and giving it a catalogue id would put every project and file name they have opened into the
///         localisation vocabulary.
///     </para>
///     <para>
///         ⚠ <b>One construction for both recent lists</b> (#656), because <c>CheckStrings</c> counts
///         every site that builds a <see cref="StringId" /> out of a run-time value against a ceiling
///         that only falls — and the recent documents' line is the same case as the recent projects',
///         not a ninth kind of one.
///     </para>
/// </remarks>
static class Untranslated {
    /// <summary>The title.</summary>
    /// <param name="text">The user's name for it.</param>
    /// <returns>A string id that shows exactly that text.</returns>
    public static StringId Title(string text) => new StringId(null!, text);
}
