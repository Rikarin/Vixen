// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Audio;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Assets.Navigation;
using Vixen.Editor.Assets.Textures;

namespace Vixen.Editor.Assets;

/// <summary>The importers this build of the engine has.</summary>
/// <remarks>
///     <para>
///         <b>One list, in one place, and told rather than discovered.</b> An assembly scan for
///         <c>[Importer]</c> would read metadata a trimmed publish has already deleted; worse, it
///         would make "which importers imported this project" a question with a different answer in
///         the editor, in the CLI and in a worker process.
///     </para>
///     <para>
///         That last one is why this moved out of the CLI. A worker whose registry differs from its
///         coordinator's produces different artefacts for the same file, and the disagreement shows
///         up as a cache that never hits — or, worse, as a build whose output depends on how many
///         cores the machine has.
///     </para>
/// </remarks>
public static class BuiltInImporters {
    /// <summary>Builds the registry.</summary>
    /// <returns>Every importer that ships, with <see cref="RawImporter" /> as the fallback.</returns>
    public static ImporterRegistry Create() =>
        new ImporterRegistry()
            .Add(new TextureImporter())
            .Add(new ModelImporter())
            .Add(new AudioImporter())
            .Add(new NavMeshImporter())
            .Add(new NativeFormatImporter())
            .Add(new FolderImporter())
            .AddFallback(new RawImporter());
}

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
