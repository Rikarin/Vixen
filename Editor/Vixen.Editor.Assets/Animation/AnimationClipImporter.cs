// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml.Meta;
using Vixen.Core.Yaml;
using Vixen.Core;
using Vixen.Rendering;

namespace Vixen.Editor.Assets.Animation;

/// <summary>Settings for the importer that takes an authored clip.</summary>
/// <remarks>
///     Empty, for <c>NativeFormatImportSettings</c>'s reason: a <c>.vxanim</c> <em>is</em> engine
///     data, so there is nothing to decide about how to read it. The frame rate that looks like a
///     candidate is the editor's timeline snap and not a property of the clip.
/// </remarks>
[DataContract("AnimationClipImporter")]
public sealed record AnimationClipImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxanim</c> into the artefact a build loads.</summary>
/// <remarks>
///     <para>
///         <b>What this closes.</b> A <c>.vxanim</c> used to go through <c>NativeFormatImporter</c>,
///         which carries the YAML text forward unchanged — enough for the reference scan and for the
///         editor to open the file, and not enough for a game to play one. Nothing turned the
///         authored curves into the <see cref="AnimationClipData" /> that
///         <see cref="AnimationClip.Create" /> bakes against a skeleton, so no clip could be loaded
///         by address. <c>.vxscene</c>, <c>.vxmat</c> and <c>.vxcompositor</c> had all made this move
///         already; this is the same move.
///     </para>
///     <para>
///         <b>The curves are sampled here rather than at load.</b> Baking is the pipeline's job and
///         it happens once; a build that shipped tangents would evaluate the same Hermite segments on
///         every machine that ever plays the clip. What ships is
///         <see cref="AnimationClipContent" /> — channels, events, wrap mode and any metadata this
///         build did not interpret.
///     </para>
///     <para>
///         ⚠ <b>The written type string is <see cref="AnimationClipContent" />'s alias, and the old
///         one was not any type's.</b> <c>NativeFormatImporter</c> wrote these bytes under
///         <c>"AnimationClip"</c> — the name of the <i>runtime</i> class, which is not what the bytes
///         are and which nothing could have resolved. It never surfaced because no project had ever
///         loaded a clip at run time. This is the third occurrence of that mistake in this pipeline
///         after <c>"Material"</c> and <c>"Mesh"</c>, and the first one a loader will actually meet.
///     </para>
/// </remarks>
[Importer(AnimationClipAsset.Extension)]
public sealed class AnimationClipImporter : AssetImporter<AnimationClipImportSettings> {
    /// <summary>The alias of the type this writes. The <em>compiled</em> clip, not the authored one.</summary>
    public const string ClipType = "AnimationClipContent";

    /// <inheritdoc />
    /// <remarks>
    ///     Tied to the compiled format's version, so that a change to what an artefact holds
    ///     re-imports every clip in every project.
    /// </remarks>
    public override int Version => AnimationClipContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        AnimationClipImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        string text;

        await using (var source = await context.OpenSourceAsync(cancellationToken).ConfigureAwait(false)) {
            using var reader = new StreamReader(source);
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        YamlNode document;

        try {
            document = YamlReader.Read(text);
        } catch (YamlParseException failure) {
            // Reported rather than thrown, so the run continues and the author gets every broken file
            // in one pass instead of one per build.
            context.Report(ImportSeverity.Error, $"It is not valid YAML: {failure.Message}");
            return context.Finish();
        }

        if (document is not YamlMapping root) {
            context.Report(
                ImportSeverity.Error,
                "Its root is not a mapping. A clip is a mapping of fields, whatever its extension."
            );

            return context.Finish();
        }

        // An extension block is allowed to name assets — a constraint that points at a shape set, a
        // tag that points at a prefab — so the scan runs over the whole document and not only the
        // parts this build binds.
        if (AssetReferenceScan.Declare(root, context) > 0) {
            return context.Finish();
        }

        AnimationClipAsset clip;

        try {
            clip = AnimationClipAsset.FromYaml(text);
        } catch (Exception failure) when (failure is YamlBindingException or FormatException or NotSupportedException) {
            context.Report(ImportSeverity.Error, failure.Message);
            return context.Finish();
        }

        if (!Compiles(context, clip)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, ClipType, Serializer.ToBytes(clip.ToContent()));
        return context.Finish();
    }

    /// <summary>Says what is wrong with the clip, and whether it is wrong enough to stop.</summary>
    /// <returns>Whether an artefact should be written.</returns>
    /// <remarks>
    ///     Warnings do not stop the build. A clip with no targets is what a file somebody just created
    ///     looks like, and failing on it would break the ordinary way of making one — but it is worth
    ///     saying, because it is also what a save that did not finish looks like.
    /// </remarks>
    static bool Compiles(ImportContext context, AnimationClipAsset clip) {
        if (clip.Duration <= 0f) {
            context.Report(
                ImportSeverity.Error,
                $"Its duration is {clip.Duration}. A clip of no length has no frame to sample and would divide "
                + "by zero the first time anything played it."
            );

            return false;
        }

        if (clip.Targets.Count == 0) {
            context.Report(
                ImportSeverity.Warning,
                "It moves nothing. That is what a new clip looks like, and it will load as a clip that poses no "
                + "joint."
            );
        }

        foreach (var target in clip.Targets) {
            if (target.Target.Length == 0) {
                context.Report(
                    ImportSeverity.Error,
                    "One of its targets names no joint. A channel is resolved against a skeleton by name, and an "
                    + "empty name resolves against nothing on every rig."
                );

                return false;
            }

            // Reported per target rather than per curve: a file with fifty duplicated properties is
            // one mistake, and fifty messages about it buries whatever else the build found.
            var duplicated = target.Curves
                .GroupBy(curve => curve.Property)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key.ToString())
                .ToArray();

            if (duplicated.Length > 0) {
                context.Report(
                    ImportSeverity.Warning,
                    $"'{target.Target}' has more than one curve for {string.Join(", ", duplicated)}. The first of "
                    + "each is the one that is sampled and the rest are ignored."
                );
            }
        }

        return true;
    }
}
