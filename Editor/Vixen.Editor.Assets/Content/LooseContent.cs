// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Assets;
using Vixen.Core.Serialization;
using Vixen.Core.Serialization.Storage;
using Vixen.Engine.Scenes;

namespace Vixen.Editor.Assets.Content;

/// <summary>What writing a loose catalog did.</summary>
/// <param name="Succeeded">Whether it wrote one.</param>
/// <param name="Addresses">How many addresses it resolves.</param>
/// <param name="Directory">Where it was written, or empty if nothing was.</param>
public readonly record struct LooseContentSummary(bool Succeeded, int Addresses, string Directory);

/// <summary>
///     A catalog over the artefacts an import left in <c>Library/</c>, with nothing packed.
/// </summary>
/// <remarks>
///     <para>
///         <b>The Editor variant's content, and the last of doc 17's five.</b> Debug, Development,
///         Release and Server all read bundles; the editor's own variant is specified to read
///         "loose files, live import", and until now the only thing <c>--vixen-loose-content</c>
///         could point at was another <i>content build</i> — a directory of packed bundles somebody
///         still had to pack. What that costs is the iteration loop: changing one texture meant a
///         full pack of every group before a player could see it.
///     </para>
///     <para>
///         <b>This is a content build with the packing step removed, and almost nothing else.</b>
///         The same <see cref="BuildPlanner" /> decides the same addresses from the same sidecars —
///         so what a player resolves here and what it resolves from a shipped build are the same
///         answer, which is the property that makes testing against this worth anything. The
///         difference is where the bytes are: a catalog entry names no bundle, and the chunks stay in
///         the artefact store the import wrote them to.
///     </para>
///     <para>
///         ⚠ <b>The runtime hook for that already existed.</b> <c>AssetManager.MountFor</c> returns
///         without mounting anything when an entry names no bundle — "a loose chunk at edit time
///         names no bundle and is already reachable" — and <c>ObjectDatabase.Mount</c> adds bundles
///         last so that "a bundle never shadows the loose files an editor is rebuilding into". Both
///         were written for this and neither had anything to serve.
///     </para>
///     <para>
///         ⚠ <b>It is written into <c>Library/</c> rather than beside it.</b> A player is pointed at
///         one directory and has to find both halves in it; <c>Library/</c> is the one that already
///         holds the chunks. It is also the directory that is not committed, which is right — this
///         is a local development artefact and not something a checkout should carry.
///     </para>
/// </remarks>
public static class LooseContent {
    /// <summary>What the artefact store is called inside the directory a player is pointed at.</summary>
    /// <remarks>
    ///     Matches the path <see cref="ProjectWorkspace" /> opens its <c>FileOdbBackend</c> on and
    ///     <c>ContentMount.ArtifactFolderName</c>, which is where a player looks — the same
    ///     name-spelled-twice bargain the catalog and the shader bundle make, and for the same
    ///     reason.
    /// </remarks>
    public const string ArtifactFolderName = "ArtifactDb";

    /// <summary>Writes a catalog over what the last import produced.</summary>
    /// <param name="workspace">The project, already imported.</param>
    /// <param name="report">Where diagnostics go.</param>
    /// <returns>What it produced.</returns>
    /// <remarks>
    ///     ⚠ <b>Nothing is copied and nothing is packed</b>, so this is a plan, a catalog and two
    ///     file writes however large the project is. That is the whole point: the cost of making a
    ///     change visible to a running player should be the import of the one asset that changed.
    /// </remarks>
    public static LooseContentSummary Write(ProjectWorkspace workspace, Action<ContentDiagnostic> report) {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(report);

        var plan = ContentPipeline.Analyse(workspace, report);

        foreach (var diagnostic in plan.Diagnostics) {
            report(new(diagnostic.Severity, ContentStage.Plan, diagnostic.Path, diagnostic.Message));
        }

        if (!plan.Succeeded) {
            return default;
        }

        if (ContentPipeline.SceneManifestFor(workspace, plan, report) is not { } scenes) {
            return default;
        }

        var catalog = Catalog(plan);

        Directory.CreateDirectory(workspace.Paths.Library);

        var catalogPath = Path.Combine(workspace.Paths.Library, ContentPipeline.CatalogFileName);
        var bytes = CatalogFormat.Write(catalog);

        File.WriteAllBytes(catalogPath, bytes);
        File.WriteAllText(catalogPath + ContentPipeline.HashFileSuffix, ContentHash.Compute(bytes).ToString());

        File.WriteAllBytes(
            Path.Combine(workspace.Paths.Library, ContentPipeline.SceneManifestFileName),
            Serializer.ToBytes(scenes)
        );

        return new(true, plan.Assets.Length, workspace.Paths.Library);
    }

    /// <summary>The catalog for a plan, with every entry naming no bundle.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An empty bundle name is the whole of what makes this loose</b>, and it is a state
    ///         the format and the runtime both already understood. Inventing a flag for it would have
    ///         been a second way to say what the absence of a bundle already says.
    ///     </para>
    ///     <para>
    ///         The build hash is the plan's own shape rather than the bytes of any bundle, because
    ///         there are none. It changes when an address, a dependency or a chunk does, which is
    ///         what a session handshake comparing two of these needs it to do.
    ///     </para>
    /// </remarks>
    static ContentCatalog Catalog(BuildPlan plan) {
        var entries = ImmutableArray.CreateBuilder<CatalogEntry>(plan.Assets.Length);
        var hash = new List<byte>();

        foreach (var asset in plan.Assets.OrderBy(asset => asset.Address, StringComparer.Ordinal)) {
            entries.Add(
                new(
                    asset.Address,
                    asset.Id,
                    string.Empty,
                    ContentProvider.Local,
                    asset.Dependencies,
                    asset.Labels,
                    0,
                    asset.Reference
                )
            );

            hash.AddRange(System.Text.Encoding.UTF8.GetBytes(asset.Address));
            hash.AddRange(asset.Id.ToString().Select(static character => (byte)character));
        }

        return new(CatalogFormat.Version, ContentHash.Compute([.. hash]), ProjectWorkspace.HostTarget, entries.ToImmutable(), []);
    }
}
