// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
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
            //
            // ⚠ Grouped by the property AND the shape, because that pair is what identifies a curve.
            // Grouping by the property alone said that a face driving twenty shapes had nineteen
            // duplicate weight curves and that "the first of each is the one that is sampled" — which
            // was a warning describing a correct file, and would have been the first thing an author
            // saw on the first facial clip anybody wrote.
            var duplicated = target.Curves
                .GroupBy(curve => (curve.Property, curve.Shape), TupleComparer)
                .Where(group => group.Count() > 1)
                .Select(group => Describe(group.Key.Property, group.Key.Shape))
                .ToArray();

            if (duplicated.Length > 0) {
                context.Report(
                    ImportSeverity.Warning,
                    $"'{target.Target}' has more than one curve for {string.Join(", ", duplicated)}. The first of "
                    + "each is the one that is sampled and the rest are ignored."
                );
            }

            // ⚠ An error rather than a warning, and it is the one mistake this format makes easy: a
            // weight curve is bound by the shape's name, so one with no name binds to nothing. It
            // would import, ship, play, and hold a face perfectly still — the failure a name-bound
            // channel exists to make impossible, arrived at by leaving the name out.
            if (target.Curves.Any(curve => curve.Property == AnimationProperty.Weight && curve.Shape.Length == 0)) {
                context.Report(
                    ImportSeverity.Error,
                    $"'{target.Target}' has a weight curve that names no blend shape. A weight is bound by the "
                    + "shape's name — the ordinal a source file used is not the one the mesh ended up with — so "
                    + "a curve with no name would drive nothing and say nothing."
                );

                return false;
            }

            var misplaced = target.Curves
                .Where(curve => curve.Property != AnimationProperty.Weight && curve.Shape.Length > 0)
                .Select(curve => curve.Property.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (misplaced.Length > 0) {
                // Information rather than an error: the shape is ignored and the curve is otherwise
                // fine, so nothing is broken. It is said because a shape typed onto a position curve
                // is somebody expecting it to do something.
                context.Report(
                    ImportSeverity.Information,
                    $"'{target.Target}' names a blend shape on {string.Join(", ", misplaced)}, which is not a "
                    + "weight curve. Only a Weight curve is bound to a shape; the name is ignored here."
                );
            }
        }

        // ⚠ Non-shortcutting, so a clip with a bad constraint *and* a bad note is reported once with
        // both. The build already reports every broken file in one pass; reporting half of one file
        // would be the same mistake one level down.
        return Constraints(context, clip) & Metadata(context, clip);
    }

    /// <summary>Compares the pair that identifies a curve, ordinally on the name half.</summary>
    static IEqualityComparer<(AnimationProperty Property, string Shape)> TupleComparer { get; } =
        new PropertyShapeComparer();

    /// <summary>What a curve is called in a message: the property, and the shape when it has one.</summary>
    static string Describe(AnimationProperty property, string shape) =>
        shape.Length > 0 ? $"{property} '{shape}'" : property.ToString();

    sealed class PropertyShapeComparer : IEqualityComparer<(AnimationProperty Property, string Shape)> {
        public bool Equals((AnimationProperty Property, string Shape) left, (AnimationProperty Property, string Shape) right) =>
            left.Property == right.Property && string.Equals(left.Shape, right.Shape, StringComparison.Ordinal);

        public int GetHashCode((AnimationProperty Property, string Shape) value) =>
            HashCode.Combine(value.Property, StringComparer.Ordinal.GetHashCode(value.Shape));
    }

    /// <summary>Checks the extension blocks a kind was registered for, and names the ones nobody reads.</summary>
    /// <remarks>
    ///     ⚠ <b>An unread block is information and not a warning.</b> Carrying what this build has no
    ///     type for is the contract that makes the block worth having; the line exists because "this
    ///     kind is spelled wrong" and "this kind belongs to a plugin that is not loaded here" look
    ///     identical in a file, and an author who is told which kinds went unread can tell them apart
    ///     in a second.
    /// </remarks>
    static bool Metadata(ImportContext context, AnimationClipAsset clip) {
        List<string> problems = [];
        List<string> unread = [];

        var ok = ClipMetadataExtensions.Default.Check(clip.Extensions, problems, unread);

        foreach (var problem in problems) {
            context.Report(ImportSeverity.Error, problem);
        }

        if (unread.Count > 0) {
            context.Report(
                ImportSeverity.Information,
                $"It carries metadata nothing in this build reads: {string.Join(", ", unread)}. That is preserved "
                + "byte for byte, and is only worth mentioning because a misspelled kind looks exactly like this."
            );
        }

        return ok;
    }

    /// <summary>What is worth saying about a clip's constraint track at import.</summary>
    /// <remarks>
    ///     ⚠ <b>Nothing here resolves against a rig, and that is the limit of what an importer can
    ///     check.</b> Whether <c>hand_r</c> exists is a question about the skeleton the clip will be
    ///     played on, which an importer does not have and must not guess at — a clip is played on
    ///     several. What it <em>can</em> check is that the markup is internally coherent, and the
    ///     rig-dependent half is reported by <c>AnimationClipContent.Bake</c> to whoever loads it.
    /// </remarks>
    static bool Constraints(ImportContext context, AnimationClipAsset clip) {
        var ok = true;

        foreach (var tag in clip.Constraints) {
            var name = tag.Name.Length > 0 ? tag.Name : tag.Effector;

            if (tag.Effector.Length == 0) {
                context.Report(
                    ImportSeverity.Error,
                    $"The constraint '{name}' names no effector. A goal is about a joint."
                );

                ok = false;
            }

            if (tag.Begin is < 0f or > 1f || tag.End is < 0f or > 1f) {
                context.Report(
                    ImportSeverity.Error,
                    $"The constraint '{name}' runs from {tag.Begin:0.###} to {tag.End:0.###}, and a span is a "
                    + "fraction of the clip. Marks outside [0, 1] are never reached."
                );

                ok = false;
            }

            // A span that ends before it begins is legitimate — it straddles the loop point, which is
            // the ordinary case for a foot that plants near the end of a cycle. What is not is easing
            // that is longer than the span it is easing.
            var span = tag.End >= tag.Begin ? tag.End - tag.Begin : (tag.End + 1f) - tag.Begin;

            if (tag.EaseIn + tag.EaseOut > span + 1e-4f) {
                context.Report(
                    ImportSeverity.Warning,
                    $"The constraint '{name}' eases in over {tag.EaseIn:0.###} and out over {tag.EaseOut:0.###} of "
                    + $"a span that is only {span:0.###} long, so it never reaches its full weight."
                );
            }

            if (tag.Kind is GoalKind.Distance && tag.Other.Length == 0) {
                context.Report(
                    ImportSeverity.Error,
                    $"The constraint '{name}' is a distance goal and names no second joint."
                );

                ok = false;
            }

            if (tag.Kind is GoalKind.Aim && tag.AuthoredDistance <= 0f) {
                context.Report(
                    ImportSeverity.Warning,
                    $"The constraint '{name}' is an aim goal with no authored distance, so its deviation is applied "
                    + "as it stands. That is right for a fixed offset and wrong for a target that moves nearer."
                );
            }
        }

        return ok;
    }
}
