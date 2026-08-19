// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;
using Vixen.Editor.Assets.Audio;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Assets.Models;
using Vixen.Editor.Assets.Navigation;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Assets.Textures;
using Vixen.Editor.Assets.Video;

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
    /// <returns>Every importer that ships and everything contributed, with <see cref="RawImporter" /> as the fallback.</returns>
    /// <remarks>
    ///     ⚠ <b>The contributions are folded in <i>before</i> the fallback and after the built-ins,
    ///     and both halves of that matter.</b> After the built-ins, so a plugin claiming an extension
    ///     one of them already claims is refused with both names rather than silently winning — see
    ///     <c>ImporterRegistry.Add</c>, where last-one-wins is rejected because an artist's PNG being
    ///     imported as a cubemap depending on load order is not a thing anybody can debug. Before the
    ///     fallback, because <c>RawImporter</c> takes anything nothing else claimed and a contributed
    ///     importer is something else claiming it.
    /// </remarks>
    public static ImporterRegistry Create() => Create(ImporterContributions.Default);

    /// <summary>Builds the registry over a particular set of contributions.</summary>
    /// <param name="contributed">What a plugin or a project script added.</param>
    /// <returns>The registry.</returns>
    /// <remarks>
    ///     ⚠ <b>An overload rather than a parameter with a default, because a test needs a set that is
    ///     not the process's.</b> <see cref="ImporterContributions.Default" /> has to be a singleton —
    ///     the callers are static factories in background tasks with no editor to be handed — and a
    ///     suite that mutated it would race every other test in the assembly that builds a registry.
    /// </remarks>
    public static ImporterRegistry Create(ImporterContributions contributed) =>
        (contributed ?? throw new ArgumentNullException(nameof(contributed))).ApplyTo(
            new ImporterRegistry()
                .Add(new TextureImporter())
                .Add(new CubeLutImporter())
                .Add(new ModelImporter())
                .Add(new AudioImporter())
                .Add(new NavMeshImporter())
                .Add(new VideoImporter())
                .Add(new SceneImporter())
                .Add(new MaterialImporter())
                .Add(new Vfx.VfxImporter())
                .Add(new Compositors.CompositorImporter())
                .Add(new Terrain.TerrainAssetImporter())
                .Add(new Terrain.HeightmapImporter())

                // ⚠ [Importer] is a declaration nothing scans for, so an importer absent from this
                // list is not an error anywhere — a .vxwaves fell through to RawImporter and became a
                // byte blob under a type name no runtime reader resolves. The zone naming it then
                // fell back to its inline spectrum and counted into WaterZoneSystem.UnresolvedWaves,
                // which is water that draws and is not the sea anybody authored.
                //
                // ⚠ That comment was written after .vxwaves and did not prevent the next five: .cube,
                // .vxbt, .vxgoap, .vxquery and .vxutility were all attributed and all absent, so the
                // grading pipeline and the whole of the AI authoring pipeline were unreachable through
                // the registry. A warning is not a gate; the gate is
                // EveryAttributedImporterTests.EveryImporterInThisAssemblyIsRegistered, which walks
                // this assembly's [Importer] types and fails on the one that is not here.
                .Add(new Water.WaterWavesImporter())
                .Add(new Ai.BehaviorTreeImporter())
                .Add(new Ai.UtilitySetImporter())
                .Add(new Ai.QueryImporter())
                .Add(new Ai.GoapDomainImporter())
                .Add(new Animation.AnimationClipImporter())
                .Add(new Animation.ShapeVocabularyImporter())
                .Add(new Animation.ProxyShapeSetImporter())
                .Add(new Animation.PriorityLadderImporter())
                .Add(new Animation.ConstraintTemplateImporter())
                .Add(new Animation.HarnessPlanImporter())
                .Add(new Animation.MoveSetImporter())
                .Add(new Gameplay.DefinitionImporter())
                .Add(new NativeFormatImporter())
                .Add(new FolderImporter())
        ).AddFallback(new RawImporter());
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
