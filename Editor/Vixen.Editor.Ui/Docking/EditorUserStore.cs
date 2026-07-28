// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Ui;

/// <summary>Where the editor keeps what belongs to the user rather than to the project.</summary>
/// <remarks>
///     <para>
///         <b>Window arrangements, keybindings and the chosen theme are the user's</b>, so they do
///         not go in the project and are not checked in — two people sharing a repository do not
///         share a window layout. Project settings are the other thing and live in
///         <c>ProjectSettings/</c>, which is <c>Vixen.Editor.Core</c>'s business.
///     </para>
///     <para>
///         ⚠ <b>It is handed a directory rather than finding one.</b> Where a user's data lives is a
///         platform question — <c>%APPDATA%</c>, <c>~/Library/Application Support</c>,
///         <c>$XDG_CONFIG_HOME</c> — and <c>Vixen.Platform</c> answers it. A store that called
///         <c>Environment.GetFolderPath</c> would be one that could not be pointed at a temporary
///         directory by a test, and one whose answer disagreed with the rest of the application's.
///     </para>
///     <para>
///         ⚠ <b>A read that fails returns <c>null</c>; a write that fails throws.</b> A missing or
///         unreadable layout means the default layout, which is a fine thing to happen at start-up
///         in front of somebody who wanted to open a project. A write that silently did nothing
///         would lose the arrangement they spent an afternoon on and say so at no point.
///     </para>
/// </remarks>
public sealed class EditorUserStore {
    /// <summary>What a saved arrangement is called on disk.</summary>
    public const string LayoutExtension = ".vxlayout";

    /// <summary>The file the keymap's user overrides go in.</summary>
    public const string KeyMapFile = "keybindings.yaml";

    /// <summary>The file the shell's own preferences go in.</summary>
    public const string PreferencesFile = "preferences.yaml";

    /// <summary>The arrangement the editor was left in.</summary>
    public const string CurrentLayout = "current";

    /// <summary>Opens a store over a directory, which need not exist yet.</summary>
    /// <param name="directory">Where the files go.</param>
    public EditorUserStore(string directory) {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    /// <summary>Where the files go.</summary>
    public string Directory { get; }

    /// <summary>Reads a file, or <c>null</c> if it is not there or cannot be read.</summary>
    /// <param name="name">Its name within the store.</param>
    /// <returns>The text, or <c>null</c>.</returns>
    public string? Read(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var path = Path.Combine(Directory, name);

        try {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Writes a file, creating the directory if it is not there.</summary>
    /// <param name="name">Its name within the store.</param>
    /// <param name="text">What goes in it.</param>
    public void Write(string name, string text) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(text);

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, name), text);
    }

    /// <summary>Deletes a file.</summary>
    /// <param name="name">Its name within the store.</param>
    /// <returns>Whether it was there.</returns>
    public bool Delete(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var path = Path.Combine(Directory, name);

        if (!File.Exists(path)) {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>The named arrangements the user has saved, in name order.</summary>
    /// <remarks>
    ///     <see cref="CurrentLayout" /> is left out: it is where the editor puts the arrangement on
    ///     the way down rather than something the user named, and offering it in a "layouts" menu
    ///     beside their own would be offering them "the one you already have".
    /// </remarks>
    public IReadOnlyList<string> Layouts() {
        if (!System.IO.Directory.Exists(Directory)) {
            return [];
        }

        return [
            .. System.IO.Directory.EnumerateFiles(Directory, "*" + LayoutExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is { Length: > 0 } && !string.Equals(name, CurrentLayout, StringComparison.Ordinal))
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
        ];
    }

    /// <summary>Saves an arrangement under a name.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="yaml">The arrangement.</param>
    public void SaveLayout(string name, string yaml) => Write(FileFor(name), yaml);

    /// <summary>Reads a named arrangement back.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The arrangement, or <c>null</c> if there is none.</returns>
    public string? LoadLayout(string name) => Read(FileFor(name));

    /// <summary>Forgets a named arrangement.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>Whether it was there.</returns>
    public bool DeleteLayout(string name) => Delete(FileFor(name));

    /// <summary>What a named arrangement's file is called.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The file name within the store.</returns>
    /// <remarks>
    ///     ⚠ <b>Path separators are refused rather than escaped.</b> A layout named
    ///     <c>../../keybindings.yaml</c> is a layout that overwrites the keymap, and the name comes
    ///     from a text box.
    /// </remarks>
    public static string FileFor(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
            throw new ArgumentException($"'{name}' is not a usable layout name.", nameof(name));
        }

        return name + LayoutExtension;
    }
}
