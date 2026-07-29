// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.AssetEditors;

/// <summary>Reading and writing the text of an asset, without losing it halfway.</summary>
/// <remarks>
///     <para>
///         Every document in this assembly writes a text file back, and every one of them wants the
///         same two things: LF regardless of the platform, and a write that cannot leave a truncated
///         file where the work was. <c>SceneSerializer.Save</c> already does both; this is the same
///         two lines, in the one place the rest of the asset editors can reach.
///     </para>
///     <para>
///         ⚠ <b>LF on every platform.</b> <c>.gitattributes</c> declares these files text, and a
///         Windows checkout that wrote CRLF would make every asset it touched a whole-file diff —
///         which is the same reason <c>AssetMetaFile.WriteFile</c> says so.
///     </para>
/// </remarks>
public static class AssetFile {
    /// <summary>Reads a file, or an empty string if it is not there.</summary>
    /// <param name="path">Where it is.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     A missing file reads as empty rather than throwing, because the ordinary way to make one
    ///     of these assets is to create the file and open it — and an editor that refused to open a
    ///     zero-byte <c>.vxmat</c> would be an editor that cannot be used to write the first one.
    /// </remarks>
    public static string Read(string path) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>Writes a file, atomically, creating its directory if it is not there.</summary>
    /// <param name="path">Where to put it.</param>
    /// <param name="text">What to write. A trailing newline is added if it has none.</param>
    public static void Write(string path, string text) {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        var normalised = text.Replace("\r\n", "\n", StringComparison.Ordinal);

        if (normalised.Length > 0 && !normalised.EndsWith('\n')) {
            normalised += "\n";
        }

        // ⚠ Through a temporary and then moved. A save interrupted halfway — a full disk, a crash, a
        // pulled cable — otherwise leaves a truncated asset where the work was, and the file it
        // destroyed is the one thing that cannot be rebuilt from anything else in the project.
        var temporary = path + ".tmp";

        File.WriteAllText(temporary, normalised);
        File.Move(temporary, path, overwrite: true);
    }
}
