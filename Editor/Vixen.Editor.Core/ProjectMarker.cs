// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Yaml;

namespace Vixen.Editor.Core;

/// <summary>What a <c>.vxproj</c> says: which format it is, and what made it.</summary>
/// <param name="Format">The marker's own schema version.</param>
/// <param name="Engine">The engine version the project was created with.</param>
/// <param name="Path">Where the file is, or empty when this is a default.</param>
public readonly record struct ProjectMarkerFile(int Format, string Engine, string Path);

/// <summary>The file that says a directory is a Vixen project.</summary>
/// <remarks>
///     <para>
///         <b>Doc 08 has named <c>MyGame.vxproj</c> since it was written, and it was never built.</b>
///         What it described — "Vixen project settings (YAML)" — turned out to be
///         <see cref="ProjectSettingsStore" />, one file per <c>[DataContract]</c> type, which doc
///         20's A4 built so that adding a setting is declaring a type. A single file beside that
///         holding <i>other</i> project settings is the second mechanism the split makes
///         unnecessary, so this is deliberately not that.
///     </para>
///     <para>
///         <b>What it is is a marker, and that half of the question was genuinely unanswered.</b>
///         <c>ProjectWorkspace.IsProject</c> said "it has an <c>Assets/</c> folder", which is weak in
///         both directions: any directory with a folder of that name qualifies — a source tree with
///         an <c>Assets/</c> in it is not a project — and a project whose assets have all been
///         deleted stops being one, which is a project the editor refuses to reopen precisely when
///         somebody most needs it to.
///     </para>
///     <para>
///         ⚠ <b>Two fields, and each has a reader, which is the bar a shipped setting has to
///         clear.</b> <see cref="ProjectMarkerFile.Format" /> is read by <see cref="TryRead" />,
///         which refuses a file from a future it does not understand rather than binding half of it;
///         <see cref="ProjectMarkerFile.Engine" /> is read by <see cref="IsFromTheFuture" />, which
///         is what lets the editor say "this project was made with a newer Vixen" instead of failing
///         later and stranger. A third field with nothing behind it would teach people that the file
///         does not matter.
///     </para>
///     <para>
///         ⚠ <b>It does not record the project's <i>name</i>.</b> That is
///         <c>ProjectInfoSettings.ProductName</c>, which the title bar and About already read — and
///         two files answering "what is this called" is exactly the disagreement doc 20's A4 spends
///         a page preventing.
///     </para>
/// </remarks>
public static class ProjectMarker {
    /// <summary>What the file is called after the project's own name.</summary>
    public const string Extension = ".vxproj";

    /// <summary>The format this build writes and is the newest it understands.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Finds a project's marker.</summary>
    /// <param name="directory">The project directory.</param>
    /// <param name="path">Where it is.</param>
    /// <returns>Whether there is exactly one.</returns>
    /// <remarks>
    ///     ⚠ <b>Exactly one, for <c>PlayerBuild.TryFindProjectFile</c>'s reason.</b> Two markers in
    ///     one directory is two projects sharing an <c>Assets/</c>, and picking between them is how
    ///     a tool silently works on the one nobody meant.
    /// </remarks>
    public static bool TryFind(string directory, out string path) {
        var candidates = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly)
            : [];

        path = candidates.Length == 1 ? candidates[0] : string.Empty;

        return path.Length > 0;
    }

    /// <summary>Reads a project's marker.</summary>
    /// <param name="directory">The project directory.</param>
    /// <param name="marker">What it says.</param>
    /// <returns>Whether there was one this build understands.</returns>
    /// <remarks>
    ///     ⚠ <b>A file that will not parse is <see langword="false" /> rather than an exception.</b>
    ///     The same bargain <c>KeyMap.Load</c> and the preferences file make: a mistyped line must
    ///     not be an editor that will not open a project. What is lost is the marker, and what
    ///     answers in its place is the <c>Assets/</c> rule that answered before this existed.
    /// </remarks>
    public static bool TryRead(string directory, out ProjectMarkerFile marker) {
        marker = default;

        if (!TryFind(directory, out var path)) {
            return false;
        }

        try {
            var document = YamlReader.Read(File.ReadAllText(path)) as YamlMapping;
            var format = Scalar(document, "format") is { } text && int.TryParse(text, out var value) ? value : 0;

            // A file from a future format is *found* and not *read*: binding the half of it this
            // build recognises would be worse than saying so, because what a later format changes
            // may be what a field means rather than which fields there are.
            if (format is < 1 or > CurrentFormat) {
                return false;
            }

            marker = new(format, Scalar(document, "engine") ?? string.Empty, path);

            return true;
        } catch (Exception exception) when (exception is YamlParseException or IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Whether a project was made by an engine newer than this one.</summary>
    /// <param name="marker">The marker.</param>
    /// <param name="engine">This build's version. Defaults to the scaffold's.</param>
    /// <returns>Whether to warn.</returns>
    /// <remarks>
    ///     ⚠ <b>Newer only, and never older.</b> Opening an old project in a new editor is the
    ///     ordinary thing that has to keep working; opening a new project in an old editor is the
    ///     one that produces confusing failures further in, and is worth a sentence at the door.
    ///     An unparseable version on either side is not a warning — a version nobody can compare is
    ///     not evidence of anything.
    /// </remarks>
    public static bool IsFromTheFuture(ProjectMarkerFile marker, string? engine = null) =>
        Version.TryParse(marker.Engine, out var wrote)
        && Version.TryParse(engine ?? ProjectScaffold.SdkVersion, out var running)
        && wrote > running;

    /// <summary>What a marker's text is, for the version and project name given.</summary>
    /// <param name="engine">The engine version to record.</param>
    /// <returns>The file's contents.</returns>
    /// <remarks>
    ///     Written by hand rather than through <c>YamlSerializer</c>, because the comment is half the
    ///     point: this is the first file somebody opens when they are working out what a Vixen
    ///     project is, and a serialiser has nowhere to put a sentence.
    /// </remarks>
    public static string Write(string engine) =>
        $"""
         # A Vixen project. The editor and the `vixen` tool find a project by this file.
         # Settings live in ProjectSettings/, one file per settings type — not here.
         format: {CurrentFormat}
         engine: {engine}

         """;

    static string? Scalar(YamlMapping? mapping, string key) =>
        mapping?[key] is YamlScalar scalar ? scalar.Value : null;
}
