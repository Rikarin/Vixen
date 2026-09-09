// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.IO;
using Vixen.Editor.Core;

namespace Vixen.Editor.Assets;

/// <summary>The project's own guid index, as the thing an import resolves through.</summary>
/// <remarks>
///     <para>
///         <b>A wrapper and not a method on <see cref="AssetDatabase" />, because the database is the
///         editor's and the seam is the pipeline's.</b> An import is allowed to ask where an asset is
///         and nothing else — not to enumerate the project, not to scan it, not to write a sidecar —
///         and an interface with one method is what makes that statement checkable.
///     </para>
///     <para>
///         ⚠ <b>Read-only by construction, deliberately.</b> <see cref="AssetDatabase.Scan" /> writes
///         — it creates a missing <c>.meta</c> with a fresh GUID and moves an orphaned one into
///         <c>Library/OrphanMeta</c> — so a resolver that scanned on demand would let an importer
///         change the project it is importing, from inside a parallel loop. This only ever reads what
///         the coordinator has already scanned.
///     </para>
///     <para>
///         ⚠ <b>A folder resolves to nothing.</b> A folder asset has a GUID and a <c>.meta</c> like
///         everything else, and it has no source file to open — so answering with its directory would
///         hand an importer a path every read of which fails, which is worse than the miss.
///     </para>
/// </remarks>
/// <param name="database">The project's assets.</param>
public sealed class ProjectAssetSources(AssetDatabase database) : IAssetSources {
    readonly AssetDatabase database = database ?? throw new ArgumentNullException(nameof(database));

    /// <inheritdoc />
    public bool TryGetPath(AssetId asset, out VirtualPath path) {
        if (!asset.IsEmpty && database.TryGetByGuid(asset, out var entry) && !entry.IsFolder) {
            // The same construction ImportPipeline uses for an asset's own source, so a resolved path
            // and a SourcePath are the same shape and one file provider serves both.
            path = new("/" + entry.Path);
            return true;
        }

        path = default;
        return false;
    }
}
