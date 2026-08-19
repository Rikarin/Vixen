// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Assets;
using Vixen.Core.IO;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;

namespace Vixen.Editor.App;

/// <summary>The project's own content, opened the way a player opens a build.</summary>
/// <remarks>
///     <para>
///         <b>The step between an import and a viewport that can draw what was imported.</b>
///         <see cref="LooseContent" /> writes a catalog over the artefacts an import already left in
///         <c>Library/</c> — no packing, no copying, "a plan, a catalog and two file writes however
///         large the project is" — and <c>ContentMount.Open</c> turns that into an
///         <see cref="AssetManager" />. Both were built for this and neither had a caller in the
///         editor, so nothing in the editor could resolve a mesh, a material or a texture by address.
///     </para>
///     <para>
///         ⚠ <b>The same addresses a shipped build resolves, which is the property that makes any of
///         this worth testing against.</b> The catalog comes from the same <c>BuildPlanner</c> reading
///         the same sidecars; what differs is that an entry names no bundle and the chunks stay where
///         the import wrote them.
///     </para>
///     <para>
///         ⚠ <b>This paragraph used to end "a viewport drawing from a second, editor-shaped path
///         would be a viewport that agrees with the game by coincidence", and as an argument about
///         <em>geometry</em> that is wrong.</b> It is wrong because a catalog is what <em>ships</em>
///         and the editor has to draw what is in the project, which is a larger set.
///         <c>BuildPlanner.AddressOf</c> gives an excluded asset no address, so it gets no entry here
///         and an <c>AssetMeshSource</c> over this manager cannot resolve it — while
///         <c>ProjectMeshSource</c>, matching on the id in the import record, reads it perfectly well.
///         A sub-asset whose <c>.meta</c> does not name it, and two sub-assets whose names collide,
///         refuse the <em>whole</em> asset for the same reason. All of it is silent:
///         <see cref="Rebuild" /> succeeds and the asset is simply absent. So the viewport's geometry
///         stays on the import cache, deliberately, and
///         <c>EditorContentTests.An_excluded_asset_is_absent_from_the_catalog_and_still_in_the_import_cache</c>
///         is what holds that decision in place.
///     </para>
///     <para>
///         <b>What is still owed here is the other half of a frame, and it is the reason this type
///         is worth keeping.</b> <c>WorldRenderer.Mount</c> is the only thing in the engine that
///         builds an <c>IMaterialSource</c>, an <c>AssetTextureSource</c>, a vfx source and the two
///         terrain seams, and it takes exactly one argument: an <see cref="AssetManager" />. The
///         editor has no editor-shaped equivalent for any of them — <c>ProjectSurfaceSource</c> is
///         four numbers for the tool renderer and does not satisfy <c>IMaterialSource</c> — which is
///         why <c>EditorWorldRenderer.Degraded</c> says every drawable is painted with the fallback.
///         <c>WorldRenderer.Source</c> and <c>Painter</c> are both settable, so the arrangement that
///         reconciles the two halves is to mount this and then put <c>ProjectMeshSource</c> back as
///         the geometry: the game's answer where the editor has none, the project's answer where the
///         catalog would silently narrow it.
///     </para>
///     <para>
///         ⚠ <b>Reopened after an import rather than kept live.</b> A catalog is a snapshot of what
///         the plan resolved, so an asset imported after the mount was opened is not in it — and the
///         failure that produces is a material that silently resolves to nothing, on one asset, until
///         the editor is restarted. <see cref="Refresh" /> is what an import completing calls.
///     </para>
///     <para>
///         ⚠ <b>A project that has never been imported is a refusal, not an exception.</b> It is the
///         state every new project is in, and an editor that would not open one is an editor nobody
///         can start a project with.
///     </para>
/// </remarks>
sealed class EditorContent : IDisposable {
    readonly EditorProject project;
    readonly VirtualFileSystem files = new();

    /// <summary>Where the project's <c>Library/</c> is mounted.</summary>
    /// <remarks>
    ///     ⚠ <b>Mounting is the editor's to do, not the factory's.</b> Engine code addresses files
    ///     through a <see cref="VirtualPath" /> and the architecture analyzer enforces it; a head is
    ///     where a physical directory becomes one, which is the same division that put
    ///     <see cref="LooseContentSource" /> in <c>Vixen.Assets</c> rather than in a host.
    /// </remarks>
    static VirtualPath MountPoint { get; } = new("/project-content");

    bool mounted;
    bool disposed;

    /// <summary>Opens whatever the project's last import produced.</summary>
    /// <param name="project">The project.</param>
    /// <exception cref="ArgumentNullException"><paramref name="project" /> is null.</exception>
    public EditorContent(EditorProject project) {
        ArgumentNullException.ThrowIfNull(project);

        this.project = project;

        Open();
    }

    /// <summary>What resolves an address, or null while nothing does.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is a viewport that draws no meshes and is not broken.</b> Every consumer treats
    ///     a missing asset as "ask again next frame" already — that is <c>MeshExtractionSystem</c>'s
    ///     own arrangement — so the absence degrades rather than throws.
    /// </remarks>
    public AssetManager? Assets { get; private set; }

    /// <summary>Why there is no content, or null when there is.</summary>
    public string? Refusal { get; private set; }

    /// <summary>How many addresses the catalog resolves.</summary>
    public int Addresses { get; private set; }

    /// <summary>Re-reads the catalog, which is what an import having finished means.</summary>
    /// <returns>Whether there is content to draw from.</returns>
    public bool Refresh() {
        ObjectDisposedException.ThrowIf(disposed, this);

        Close();
        Open();

        return Assets is not null;
    }

    /// <summary>Writes a catalog over the last import, then opens it.</summary>
    /// <param name="report">Where the planner's diagnostics go.</param>
    /// <returns>Whether there is content to draw from.</returns>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Refresh" /> because writing is not free and reopening
    ///     is.</b> A frame that only wants to see a newly imported asset re-reads; a build that has
    ///     just imported writes first. Conflating them would put the planner on the frame thread.
    /// </remarks>
    public bool Rebuild(Action<ContentDiagnostic>? report = null) {
        ObjectDisposedException.ThrowIf(disposed, this);

        var problems = new List<string>();

        try {
            // ⚠ A workspace has to be *opened* before a planner can see anything in it: the import
            // cache is a file and the asset database is a scan, and a freshly constructed one has
            // neither. Without this the catalog is written successfully and is empty, and every
            // address in the project resolves to "the build did not include it" — a message about a
            // build, for a project that was imported perfectly well.
            var workspace = new ProjectWorkspace(project.Paths);

            workspace.Cache.TryLoad(workspace.CacheFile);
            workspace.Database.Scan();

            var summary = LooseContent.Write(
                workspace,
                diagnostic => {
                    report?.Invoke(diagnostic);

                    if (diagnostic.Severity == Vixen.Editor.Assets.ImportSeverity.Error) {
                        problems.Add(diagnostic.Message);
                    }
                }
            );

            if (!summary.Succeeded) {
                Close();

                Refusal = problems.Count > 0
                    ? string.Join(Environment.NewLine, problems)
                    : "The content plan did not succeed. Import the project first.";

                return false;
            }
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            Close();
            Refusal = failure.Message;

            return false;
        }

        return Refresh();
    }

    void Open() {
        try {
            if (!Directory.Exists(project.Paths.Library)) {
                Refusal = $"There is nothing under {project.Paths.Library}. Import the project first.";

                return;
            }

            if (!mounted) {
                files.Mount(MountPoint, new PhysicalFileProvider(Path.GetFullPath(project.Paths.Library), isReadOnly: true));
                mounted = true;
            }

            Assets = LooseContentSource.Open(files, MountPoint, out var refusal);
            Refusal = refusal;
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            Assets = null;
            Refusal = failure.Message;
        }
    }

    void Close() {
        // ⚠ The manager is dropped and the mount is kept. A mount is a provider over a directory that
        // has not moved; re-mounting it per refresh would leave the file system holding one provider
        // per import for the life of the session.
        Assets = null;
        Addresses = 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        Close();
    }
}
