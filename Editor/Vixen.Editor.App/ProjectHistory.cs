// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;
using Vixen.Editor.Ui;

namespace Vixen.Editor.App;

/// <summary>One project the editor has had open.</summary>
/// <param name="Path">Its root directory, absolute.</param>
/// <param name="Opened">When it was last opened, in UTC.</param>
/// <remarks>
///     ⚠ <b>The time is what makes the list readable rather than decoration.</b> Doc 20's A2 asks
///     the startup browser for "recent projects with their last-opened time", and the reason is that
///     six directories called <c>Game</c>, <c>Game2</c>, <c>game-old</c> and so on are told apart by
///     when they were touched and by almost nothing else.
/// </remarks>
sealed record RecentProject(string Path, DateTime Opened) {
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

/// <summary>Which projects the editor has opened, newest first.</summary>
/// <remarks>
///     <para>
///         <b>The list behind File ▸ Open Recent and behind the startup browser</b> — doc 20's A2
///         says the first question an editor is asked is "which project", and that <c>--project</c>
///         is not an answer for a user. It lives in the user's data directory beside the layout and
///         the keymap, because which projects <i>this person</i> has open is not something to check
///         into any one of them.
///     </para>
///     <para>
///         ⚠ <b>Reading a broken file gives an empty list rather than throwing.</b> This is read
///         before the window is up, in the same position <see cref="WindowPlacement" /> is and for
///         the same reason: a stray character in a file about recent projects must not be a process
///         that exits with a stack trace in front of somebody who wanted to open one.
///     </para>
/// </remarks>
sealed class ProjectHistory {
    readonly EditorUserStore store;
    readonly List<RecentProject> entries = [];

    /// <summary>Opens the history over a user data directory.</summary>
    /// <param name="directory">Where the user's files live.</param>
    public ProjectHistory(string directory) {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        store = new EditorUserStore(directory);
        Load();
    }

    /// <summary>What has been opened, newest first.</summary>
    public IReadOnlyList<RecentProject> Entries => entries;

    /// <summary>How many are kept.</summary>
    public int Limit { get; set; } = 12;

    /// <summary>Records that a project has been opened, and writes the list.</summary>
    /// <param name="root">Its root directory.</param>
    /// <param name="now">When, so that a test does not have to wait a second to see an order change.</param>
    public void Record(string root, DateTime now) {
        ArgumentException.ThrowIfNullOrEmpty(root);

        var full = Path.GetFullPath(root);

        // ⚠ Compared case-insensitively on the platforms whose file systems are, which is what stops
        // `C:\Game` and `c:\game` becoming two entries for one project. `OrdinalIgnoreCase` on Linux
        // would merge two directories that genuinely differ, so the comparison follows the platform.
        entries.RemoveAll(entry => string.Equals(entry.Path, full, PathComparison));
        entries.Insert(0, new RecentProject(full, now));

        Trim();
        Save();
    }

    /// <summary>How a path is compared with another on this platform.</summary>
    static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    void Trim() {
        if (entries.Count > Limit) {
            entries.RemoveRange(Limit, entries.Count - Limit);
        }
    }

    void Load() {
        if (store.Read(EditorUserStore.RecentProjectsFile) is not { } yaml) {
            return;
        }

        try {
            if (YamlReader.Read(yaml) is not YamlMapping document || document["projects"] is not YamlSequence list) {
                return;
            }

            foreach (var node in list) {
                if (node is not YamlMapping entry || entry["path"] is not YamlScalar { Value: { Length: > 0 } path }) {
                    continue;
                }

                var opened = entry["opened"] is YamlScalar when
                    && DateTime.TryParse(when.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                        ? parsed
                        : DateTime.UnixEpoch;

                entries.Add(new RecentProject(path, opened));
            }

            Trim();
        } catch (YamlParseException) {
            entries.Clear();
        }
    }

    void Save() {
        var list = new YamlSequence();

        foreach (var entry in entries) {
            list.Add(
                new YamlMapping()
                    .Set("path", new YamlScalar(entry.Path, YamlScalarStyle.DoubleQuoted))
                    .Set(
                        "opened",
                        new YamlScalar(entry.Opened.ToString("O", CultureInfo.InvariantCulture), YamlScalarStyle.DoubleQuoted)
                    )
            );
        }

        try {
            store.Write(EditorUserStore.RecentProjectsFile, YamlWriter.Write(new YamlMapping().Set("projects", list)));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            // A recent-projects list that could not be written is not worth failing anything over.
        }
    }
}
