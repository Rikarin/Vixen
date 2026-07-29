// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Core;

/// <summary>What went wrong, or nothing.</summary>
/// <param name="Ok">Whether it happened.</param>
/// <param name="Message">Why not, as a sentence for a person.</param>
/// <remarks>
///     A result rather than an exception, because every one of these failures is an ordinary thing to
///     meet — a name with a slash in it, a file that is open in another program, a folder that
///     already has one of that name. An editor that took the process down for any of them would be
///     one nobody renames anything in twice.
/// </remarks>
public readonly record struct AssetOperation(bool Ok, string? Message) {
    /// <summary>It happened.</summary>
    public static AssetOperation Done { get; } = new(true, null);

    /// <summary>It did not, and this is why.</summary>
    /// <param name="message">Why.</param>
    /// <returns>The failure.</returns>
    public static AssetOperation Failed(string message) => new(false, message);
}

/// <summary>Renaming, moving and deleting a file the project knows about.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>"Rename with reference fixup" is not a rewrite here, and understanding why is the
///         whole of this file.</b> Doc 08 chose a GUID in a prefixed scalar over a path, and doc 20
///         calls a naive rename "the fastest way to corrupt a project" — both are true, and together
///         they say that the referrers need <i>nothing</i> done to them. A scene points at
///         <c>vx:9e8a44c9…</c>, the GUID lives in the sidecar, and the sidecar travels with the file.
///         The corruption a naive implementation causes is not a stale path: it is <b>leaving the
///         <c>.meta</c> behind</b>, at which point the next scan finds an asset with no identity,
///         mints a new one, and every reference in the project is dangling with nothing having
///         reported an error.
///     </para>
///     <para>
///         So the invariant is one sentence — <b>the sidecar moves with the asset, atomically enough
///         that a failure leaves neither moved</b> — and every method here is that sentence plus the
///         bookkeeping to make the database agree afterwards.
///     </para>
///     <para>
///         ⚠ <b>Delete reports before it acts.</b> <see cref="ReferenceIndex.ReferrersOf" /> answers
///         "what breaks if I delete this" already; the caller is expected to ask
///         <see cref="Breakage" /> and put the answer in front of somebody. Deleting first and
///         showing a list of newly-broken scenes afterwards is not a warning.
///     </para>
///     <para>
///         ⚠ <b>Not undoable, and deliberately.</b> These are filesystem operations, not document
///         edits: there is no stack they belong to — the project has none — and a rename that could
///         be undone from a scene's stack would be an undo that reaches outside the document it
///         belongs to. Delete is the one that hurts, and asking first is the mitigation that matches
///         what every editor does. A trash folder is worth having and is
///         <see cref="EditorProject" />'s to own rather than this file's.
///     </para>
/// </remarks>
public static class AssetOperations {
    /// <summary>Gives an asset a new name in the folder it is already in.</summary>
    /// <param name="project">The project.</param>
    /// <param name="asset">Which asset.</param>
    /// <param name="name">The new name, with or without the extension.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     ⚠ <b>The extension is kept when the new name has none.</b> A person typing a name into a
    ///     rename box types "Crate", not "Crate.png" — and an editor that took them at their word
    ///     would produce a file whose importer no longer claims it, which looks like the asset having
    ///     been destroyed.
    /// </remarks>
    public static AssetOperation Rename(EditorProject project, AssetId asset, string name) {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.Assets.TryGetByGuid(asset, out var entry)) {
            return AssetOperation.Failed("That asset is not in the project's index.");
        }

        if (Invalid(name) is { } complaint) {
            return AssetOperation.Failed(complaint);
        }

        var extension = Path.GetExtension(entry.Path);

        var wanted = entry.IsFolder || Path.HasExtension(name) || extension.Length == 0
            ? name
            : name + extension;

        var folder = Path.GetDirectoryName(entry.Path) ?? string.Empty;

        return Relocate(project, entry, Combine(folder, wanted));
    }

    /// <summary>Moves an asset into another folder, keeping its name.</summary>
    /// <param name="project">The project.</param>
    /// <param name="asset">Which asset.</param>
    /// <param name="folder">The destination, relative to the project root — <c>Assets/Props</c>.</param>
    /// <returns>What happened.</returns>
    public static AssetOperation Move(EditorProject project, AssetId asset, string folder) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(folder);

        if (!project.Assets.TryGetByGuid(asset, out var entry)) {
            return AssetOperation.Failed("That asset is not in the project's index.");
        }

        var destination = Combine(folder.Replace('\\', '/').TrimEnd('/'), entry.Name);

        // ⚠ A folder cannot be moved inside itself, and the check is on the *path* rather than on
        // the identity: dropping Props into Props/Crates is a move whose source is an ancestor of
        // its destination, and the file system's answer to it is to delete the tree.
        if (entry.IsFolder && destination.StartsWith(entry.Path + "/", StringComparison.Ordinal)) {
            return AssetOperation.Failed($"'{entry.Name}' cannot be moved inside itself.");
        }

        return Relocate(project, entry, destination);
    }

    /// <summary>Deletes an asset and the sidecar that carries its identity.</summary>
    /// <param name="project">The project.</param>
    /// <param name="asset">Which asset.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     ⚠ <b>The sidecar goes too, and a delete that left one behind is worse than one that left
    ///     the asset.</b> An orphaned <c>.meta</c> is quarantined by the next scan — see
    ///     <c>AssetDatabase.Quarantine</c> — which is a repair the user did not ask for appearing in
    ///     their working tree.
    /// </remarks>
    public static AssetOperation Delete(EditorProject project, AssetId asset) {
        ArgumentNullException.ThrowIfNull(project);

        if (!project.Assets.TryGetByGuid(asset, out var entry)) {
            return AssetOperation.Failed("That asset is not in the project's index.");
        }

        var path = project.Paths.Absolute(entry.Path);

        try {
            if (entry.IsFolder) {
                Directory.Delete(path, recursive: true);
            } else if (File.Exists(path)) {
                File.Delete(path);
            }

            var meta = AssetMetaFile.PathFor(path);

            if (File.Exists(meta)) {
                File.Delete(meta);
            }
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return AssetOperation.Failed(failure.Message);
        }

        Reindex(project);
        return AssetOperation.Done;
    }

    /// <summary>Makes a folder inside another one.</summary>
    /// <param name="project">The project.</param>
    /// <param name="parent">Where, relative to the project root, or empty for <c>Assets</c>.</param>
    /// <param name="name">What it is called.</param>
    /// <returns>What happened.</returns>
    /// <remarks>
    ///     ⚠ <b>The name is made unique rather than refused.</b> Every editor's New Folder makes
    ///     "New Folder 2" when there is already one, because the gesture is "make me somewhere to put
    ///     this" and an error dialog is not an answer to it. Renaming a file to a name that is taken
    ///     is the opposite case and does refuse — there the name is the point.
    /// </remarks>
    public static AssetOperation CreateFolder(EditorProject project, string parent, string name) {
        ArgumentNullException.ThrowIfNull(project);

        if (Invalid(name) is { } complaint) {
            return AssetOperation.Failed(complaint);
        }

        var root = string.IsNullOrEmpty(parent) ? "Assets" : parent.Replace('\\', '/').TrimEnd('/');
        var chosen = Unique(project, root, name);

        try {
            Directory.CreateDirectory(project.Paths.Absolute(Combine(root, chosen)));
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return AssetOperation.Failed(failure.Message);
        }

        Reindex(project);
        return AssetOperation.Done;
    }

    /// <summary>What would be left pointing at nothing if these assets went.</summary>
    /// <param name="project">The project.</param>
    /// <param name="assets">What is about to be deleted.</param>
    /// <returns>The referrers, by name, without the ones being deleted.</returns>
    /// <remarks>
    ///     ⚠ <b>The referrers inside the selection are excluded.</b> Deleting a material and the
    ///     texture it uses together breaks nothing, and reporting "1 scene would break" for a file
    ///     that is itself going is the warning that teaches people to ignore warnings.
    /// </remarks>
    public static IReadOnlyList<string> Breakage(EditorProject project, IReadOnlyList<AssetId> assets) {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);

        HashSet<AssetId> going = [.. assets];
        SortedSet<string> broken = new(StringComparer.Ordinal);

        foreach (var asset in assets) {
            foreach (var referrer in project.References.ReferrersOf(asset)) {
                if (!going.Contains(referrer) && project.Assets.TryGetByGuid(referrer, out var entry)) {
                    broken.Add(entry.Path);
                }
            }
        }

        return [.. broken];
    }

    /// <summary>Moves an asset and the sidecar that carries its identity, together.</summary>
    /// <remarks>
    ///     ⚠ <b>The sidecar is moved <i>after</i> the asset and its failure is reported as a
    ///     failure.</b> The asset arriving without it is the one state that loses an identity, so a
    ///     silent catch here would be exactly the corruption this file exists to prevent. Moving the
    ///     sidecar first would be worse: a failure then leaves the identity pointing at nothing.
    /// </remarks>
    static AssetOperation Relocate(EditorProject project, AssetEntry entry, string destination) {
        if (string.Equals(entry.Path, destination, StringComparison.Ordinal)) {
            return AssetOperation.Done;
        }

        var from = project.Paths.Absolute(entry.Path);
        var to = project.Paths.Absolute(destination);

        if (File.Exists(to) || Directory.Exists(to)) {
            return AssetOperation.Failed($"'{Path.GetFileName(destination)}' is already there.");
        }

        try {
            var folder = Path.GetDirectoryName(to);

            if (folder is { Length: > 0 }) {
                Directory.CreateDirectory(folder);
            }

            if (entry.IsFolder) {
                Directory.Move(from, to);
            } else {
                File.Move(from, to);
            }
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return AssetOperation.Failed(failure.Message);
        }

        var sidecar = AssetMetaFile.PathFor(from);

        if (File.Exists(sidecar)) {
            try {
                File.Move(sidecar, AssetMetaFile.PathFor(to));
            } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
                // Put the asset back, so that the pair is still together and the project is in the
                // state it was in. A half-done move is the one outcome that has to be impossible.
                Restore(entry.IsFolder, to, from);

                return AssetOperation.Failed(
                    $"The file moved but its .meta did not, so the move was undone. {failure.Message}"
                );
            }
        }

        Reindex(project);
        return AssetOperation.Done;
    }

    static void Restore(bool isFolder, string from, string to) {
        try {
            if (isFolder) {
                Directory.Move(from, to);
            } else {
                File.Move(from, to);
            }
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // Nothing better is available: the asset is at its new path with its sidecar at the old
            // one, and saying so is the caller's message rather than a second exception from here.
        }
    }

    /// <summary>Brings the index and the reverse index back into line with the disk.</summary>
    /// <remarks>
    ///     ⚠ <b>Both, and the reverse one is the half that is easy to forget.</b>
    ///     <c>ReferenceIndex</c> is built from the database's entries, so one built against the
    ///     previous scan answers "what breaks if I delete this" about assets that have since moved —
    ///     which is a wrong answer to the one question that must not have one.
    /// </remarks>
    static void Reindex(EditorProject project) {
        project.Assets.Scan();
        project.Assets.Save();
        project.References.Build(project.Assets);
    }

    static string Combine(string folder, string name) =>
        string.IsNullOrEmpty(folder) ? name : folder + "/" + name;

    /// <summary>A name that is free, by adding a number until it is.</summary>
    static string Unique(EditorProject project, string folder, string name) {
        if (!Taken(project, folder, name)) {
            return name;
        }

        for (var suffix = 2; suffix < 1000; suffix++) {
            var candidate = name + " " + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!Taken(project, folder, candidate)) {
                return candidate;
            }
        }

        return name;
    }

    static bool Taken(EditorProject project, string folder, string name) {
        var path = project.Paths.Absolute(Combine(folder, name));

        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>Why a name cannot be used, or null.</summary>
    /// <remarks>
    ///     ⚠ <b>A separator is refused rather than sanitised.</b> "Props/Crate" typed into a rename
    ///     box means one of two things — a name with a slash in it, or a move — and guessing produces
    ///     a file somewhere the user was not looking. The dot cases are the ones a file system
    ///     accepts and no tool can then address.
    /// </remarks>
    static string? Invalid(string? name) {
        if (string.IsNullOrWhiteSpace(name)) {
            return "A name cannot be empty.";
        }

        if (name is "." or "..") {
            return $"'{name}' is not a name.";
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal)) {
            return "A name cannot contain a path separator. Use Move To… to put it in another folder.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
            return "That name has a character the file system will not take.";
        }

        // ⚠ A sidecar is the asset's path plus `.meta`, so a file actually called `Crate.png.meta`
        // and the sidecar of `Crate.png` are the same path. The scan would quarantine one of them.
        if (name.EndsWith(AssetMetaFile.Extension, StringComparison.OrdinalIgnoreCase)) {
            return $"A name cannot end in '{AssetMetaFile.Extension}' — that is what a sidecar is called.";
        }

        return null;
    }
}
