// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>One project the editor has had open.</summary>
/// <param name="Path">Its root directory, absolute.</param>
/// <param name="Opened">When it was last opened, in UTC.</param>
/// <remarks>
///     ⚠ <b>The time is what makes the list readable rather than decoration.</b> Doc 20's A2 asks
///     the startup browser for "recent projects with their last-opened time", and the reason is that
///     six directories called <c>Game</c>, <c>Game2</c>, <c>game-old</c> and so on are told apart by
///     when they were touched and by almost nothing else.
///     <para>
///         Here rather than beside the list that persists it (<c>ProjectHistory</c>, in the
///         application) because it is what <see cref="IProjectSession.Recent" /> answers with, and a
///         module reading the recent list must not have to reference the application to name a row.
///     </para>
/// </remarks>
public sealed record RecentProject(string Path, DateTime Opened) {
    /// <summary>What the directory is called, which is what a row's first line says.</summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, '/'));

    /// <summary>Whether the directory is still there.</summary>
    /// <remarks>
    ///     ⚠ <b>Kept in the list when it is not.</b> A project on a disconnected volume is one the
    ///     user wants to see greyed rather than silently forgotten — a recent list that prunes
    ///     itself is one that loses the entry the moment somebody unplugs a drive, and there is no
    ///     way back to it afterwards.
    /// </remarks>
    public bool Exists => Directory.Exists(Path);
}
