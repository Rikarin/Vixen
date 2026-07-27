// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets;

/// <summary>Settings for the importer that takes anything nothing else claimed.</summary>
[DataContract("RawImporter")]
public sealed record RawImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Copies a file verbatim, so that anything at all can be addressable as a byte blob.</summary>
/// <remarks>
///     The fallback, and it exists so that "this format has no importer yet" is a shrug rather than a
///     blocker: a game that wants to ship a CSV, a licence file or a format the engine has never
///     heard of gets an address for it today. It reads its own source and nothing else, which makes
///     it the smallest complete example of what an importer is.
/// </remarks>
[Importer]
public sealed class RawImporter : AssetImporter<RawImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        RawImportSettings settings,
        CancellationToken cancellationToken
    ) {
        await using var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        context.Write(SubAssetId.Main, "Blob", buffer.ToArray());
        return context.Finish();
    }
}

/// <summary>Settings for a folder.</summary>
[DataContract("FolderImporter")]
public sealed record FolderImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Imports a folder, which produces nothing.</summary>
/// <remarks>
///     A folder is an asset because it is where an addressable group is inherited from and where a
///     GUID has to live so that renaming a directory does not orphan everything under it. It has no
///     content, so this reads nothing and writes nothing — and that is the whole implementation
///     rather than an omission.
/// </remarks>
[Importer]
public sealed class FolderImporter : AssetImporter<FolderImportSettings> {
    /// <inheritdoc />
    public override int Version => 1;

    /// <inheritdoc />
    protected override ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        FolderImportSettings settings,
        CancellationToken cancellationToken
    ) =>
        ValueTask.FromResult(context.Finish());
}
