// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>The user's editor data directory: preferences, layouts, recents, and the second plugin folder.</summary>
/// <remarks>
///     <para>
///         <b>Published so a module can offer it as the place a file goes</b> (#1298). The
///         diagnostics report starts its save picker there, and it used to be the one fact that kept
///         the report on the application: everything else it reads — the project, the scene, the log
///         ring, the capture — a module could already be handed.
///     </para>
///     <para>
///         ⚠ <b>A type of its own rather than a <see cref="string" /></b>, because services are keyed
///         by type: a string published once would be the only string any plugin could ever be handed,
///         and whoever asked for it would get this by accident.
///     </para>
/// </remarks>
/// <param name="path">The directory, which need not exist yet.</param>
public sealed class EditorDataDirectory(string path) {
    /// <summary>The directory.</summary>
    public string Path { get; } = path;
}
