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
                .Add(new Net.NetworkRulesImporter())
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

/// <summary>The extensions somebody expects to mean something, that nothing here claims.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The fallback's silence is the defect this table exists to end.</b>
///         <see cref="RawImporter" /> is right to take a CSV or a licence file — that is what it is
///         for. It is wrong to take a <c>.vxfont</c> the editor's own Create menu wrote thirty
///         seconds ago, and the two are indistinguishable from the outside: both succeed, both write
///         a chunk called <c>Blob</c>, and neither says anything. Seven formats have now shipped that
///         way (<c>.vxwaves</c>, <c>.cube</c>, <c>.vxbt</c>, <c>.vxgoap</c>, <c>.vxquery</c>,
///         <c>.vxutility</c>, <c>.dds</c>), each found by somebody wondering why an asset did
///         nothing.
///     </para>
///     <para>
///         <b>Two kinds of entry, and the severity is the difference.</b> A format handled
///         <i>elsewhere</i> — a <c>.rvn</c> compiled by the effect cache, a <c>.vxml</c> read by a
///         source generator — works today, so it is an <see cref="ImportSeverity.Information" />: what
///         the reader needs is "this is not imported, here is what does read it". A format nothing at
///         all handles is an <see cref="ImportSeverity.Warning" />, because the blob really is
///         unreachable.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is an <see cref="ImportSeverity.Error" />, deliberately.</b>
///         <c>VideoImporter</c> and <c>AudioImporter</c> claim-and-refuse an <c>.mp4</c> or an
///         <c>.ogg</c> because a decoder is genuinely missing and the file cannot be used at all.
///         These are different: a <c>.rvn</c> under <c>Assets/</c> is a supported, working
///         arrangement — <c>EditorEffects</c> enumerates exactly that — and failing it would break
///         projects that are correct today.
///     </para>
///     <para>
///         ⚠ <b>And it is a table read by the fallback rather than a set of real importers, which is
///         not a shortcut.</b> An importer claiming <c>.ttf</c> would be written into every
///         <c>.meta</c> beside one, and <c>ImportPipeline.TryChooseImporter</c> honours a named
///         importer and disregards only the fallback — so the day a real <c>FontImporter</c> ships,
///         every font imported before it would stay a placeholder for ever in every checkout that has
///         the sidecar. That is the <c>CompositorImporter</c> trap, and the fallback is the one entry
///         that cannot spring it.
///     </para>
/// </remarks>
static class UnimportedFormats {
    /// <summary>What the fallback says about a file, keyed by extension.</summary>
    static readonly Dictionary<string, (ImportSeverity Severity, string Reason)> Known =
        new(StringComparer.OrdinalIgnoreCase) {
            // ── Handled outside the asset database. Doc 08 § Import's "Importer set for 1.0" names an
            //    importer for each of these and there is none; the work happens somewhere else.
            [".rvn"] = (ImportSeverity.Information,
                "A .rvn is compiled by the shader pipeline rather than imported — Tools/Vixen.ShaderCompiler and "
                + "the CheckShaders target build the library's, and EditorEffects compiles a project's out of "
                + "Assets/. Doc 08's table names a ShaderImporter producing an .rvnlib and effect registration; "
                + "there is none, so this file ships as bytes and registers no effect."),

            [".vxml"] = (ImportSeverity.Information,
                "A .vxml is read at compile time by Vixen.Ui.Markup.Generators, which writes the component's C# "
                + "partial. Doc 08's table names a MarkupImporter; there is none, so this file ships as bytes and "
                + "nothing in the content build reads it as markup."),

            [".vcss"] = (ImportSeverity.Information,
                "A .vcss is read at compile time by Vixen.Ui.Styling.Utilities for utility-class extraction, and "
                + "loaded at run time by the application's own cascade. Doc 08's table names a StyleImporter; "
                + "there is none, so this file ships as bytes and no stylesheet is parsed at build time."),

            [".cs"] = (ImportSeverity.Information,
                "A .cs under an Editor/ folder is compiled in-process by ScriptCompiler, and a game's own code by "
                + "its .csproj. Doc 08's table names a ScriptImporter producing execution order and default field "
                + "values; there is none, so no script metadata exists and this file ships as bytes."),

            [".ttf"] = (ImportSeverity.Information, FontReason),
            [".otf"] = (ImportSeverity.Information, FontReason),
            [".woff2"] = (ImportSeverity.Information, FontReason),

            // ── Nothing handles these at all. Each is a format this build can *author* and cannot
            //    read back, which is the shape the seven previous instances had.
            [".vxfont"] = (ImportSeverity.Warning,
                "The editor creates and opens a .vxfont and nothing imports one, so it ships as a chunk called "
                + "Blob that no typed reader resolves. Doc 08's table names a FontImporter; FontAsset's faces are "
                + "read straight off disk by the editor instead."),

            [".vxanimgraph"] = (ImportSeverity.Warning,
                "The editor creates and opens a .vxanimgraph and nothing imports one, so an authored state "
                + "machine cannot be loaded by address at run time."),

            [".vxseq"] = (ImportSeverity.Warning,
                "The editor creates and opens a .vxseq and nothing imports one, so an authored sequence cannot be "
                + "loaded by address at run time."),

            [".vxmixer"] = (ImportSeverity.Warning,
                "The editor creates and opens a .vxmixer and nothing imports one. AudioEngine.LoadMixer takes a "
                + "MixerAsset and nothing builds one from a file, so the two halves exist and do not meet."),

            [".vxshadergraph"] = (ImportSeverity.Warning,
                "Nothing imports a .vxshadergraph — see Vixen.Editor.AssetEditors/README.md § Known gaps. What a "
                + "build needs from a graph is the compiled shader, which is ShaderGraphDocument.Compile handed "
                + "to Raven."),

            [".vxplacement"] = (ImportSeverity.Warning,
                "Nothing imports a .vxplacement. The extension belongs to Live/Vixen.Live.Orchestrator, which "
                + "CheckArchitecture refuses to let Editor/ reference — see docs/overview.md § 1.13 for the two "
                + "options and why neither has been picked.")
        };

    const string FontReason =
        "A font is loaded straight from its bytes by FontFace.Load, so this blob is readable. What is missing is "
        + "the bake: doc 08's table names a FontImporter producing an MSDF atlas, metrics and kerning, and there "
        + "is none, so every consumer shapes from the raw face at run time.";

    /// <summary>Every extension the table has something to say about.</summary>
    internal static IEnumerable<string> Extensions => Known.Keys;

    /// <summary>What to say about a file the fallback has just claimed, if anything.</summary>
    /// <param name="path">The source path.</param>
    /// <param name="severity">How much attention it needs.</param>
    /// <param name="reason">What to say.</param>
    /// <returns>Whether anybody expects this extension to mean something.</returns>
    internal static bool TryExplain(string path, out ImportSeverity severity, out string reason) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (Known.TryGetValue(Path.GetExtension(path), out var known)) {
            (severity, reason) = known;
            return true;
        }

        severity = ImportSeverity.Information;
        reason = string.Empty;
        return false;
    }
}

/// <summary>Copies a file verbatim, so that anything at all can be addressable as a byte blob.</summary>
/// <remarks>
///     <para>
///         The fallback, and it exists so that "this format has no importer yet" is a shrug rather
///         than a blocker: a game that wants to ship a CSV, a licence file or a format the engine has
///         never heard of gets an address for it today. It reads its own source and nothing else,
///         which makes it the smallest complete example of what an importer is.
///     </para>
///     <para>
///         ⚠ <b>It says so when the shrug is wrong.</b> The same shrug that is right for a CSV was
///         how <c>.vxwaves</c>, <c>.cube</c> and the four AI formats each shipped unreachable — an
///         extension somebody meant something by, taken as bytes, with no diagnostic anywhere.
///         <see cref="UnimportedFormats" /> is the list of extensions that are somebody's, and this
///         is where the silence ends.
///     </para>
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

        // ⚠ Reported before the write rather than instead of it. The bytes are still what this
        // importer produces — a .rvn or a .ttf under Assets/ is a working arrangement and refusing it
        // would break projects that are correct today — so this adds a sentence and takes nothing
        // away.
        if (UnimportedFormats.TryExplain(context.SourcePath.ToString(), out var severity, out var reason)) {
            context.Report(severity, reason);
        }

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
