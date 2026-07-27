// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.Core;

/// <summary>Where the four top-level directories of a project are.</summary>
/// <remarks>
///     <para>
///         <c>Assets/</c> and <c>ProjectSettings/</c> are committed; <c>Library/</c> and
///         <c>Build/</c> are not. That is Unity's split and it is right: everything under
///         <c>Library/</c> is reproducible from a source file plus its <c>.meta</c>, so committing it
///         is pure churn and a permanent source of merge conflicts on binary blobs.
///     </para>
///     <para>
///         Paths are held as absolute and handed out as absolute; anything stored in a file — the
///         GUID index, a reference — is project-relative with forward slashes, so the same
///         <c>Library/</c> works on a Mac and a Windows machine sharing a checkout.
///     </para>
/// </remarks>
public sealed class ProjectPaths {
    /// <summary>The project's root directory.</summary>
    public string Root { get; }

    /// <summary>Everything that gets imported.</summary>
    public string Assets { get; }

    /// <summary>Per-project settings assets, committed.</summary>
    public string ProjectSettings { get; }

    /// <summary>The local, disposable, machine-specific import cache.</summary>
    public string Library { get; }

    /// <summary>Content build output.</summary>
    public string Build { get; }

    /// <summary>The persisted GUID index.</summary>
    public string GuidIndexFile { get; }

    /// <summary>Where a <c>.meta</c> whose asset has gone goes.</summary>
    public string OrphanMeta { get; }

    /// <summary>Names a project's directories.</summary>
    /// <param name="root">The project's root directory.</param>
    public ProjectPaths(string root) {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
        Assets = Path.Combine(Root, "Assets");
        ProjectSettings = Path.Combine(Root, "ProjectSettings");
        Library = Path.Combine(Root, "Library");
        Build = Path.Combine(Root, "Build");
        GuidIndexFile = Path.Combine(Library, "GuidIndex");
        OrphanMeta = Path.Combine(Library, "OrphanMeta");
    }

    /// <summary>Turns an absolute path into the project-relative, forward-slashed form things are stored as.</summary>
    /// <param name="absolute">The path.</param>
    /// <returns>The relative path.</returns>
    public string Relative(string absolute) {
        ArgumentException.ThrowIfNullOrEmpty(absolute);
        return Path.GetRelativePath(Root, absolute).Replace('\\', '/');
    }

    /// <summary>Turns a project-relative path back into an absolute one.</summary>
    /// <param name="relative">The path.</param>
    /// <returns>The absolute path.</returns>
    public string Absolute(string relative) {
        ArgumentException.ThrowIfNullOrEmpty(relative);
        return Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
