// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Animation;

/// <summary>Settings for the importer that takes a priority ladder.</summary>
[DataContract("PriorityLadderImporter")]
public sealed record PriorityLadderImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Settings for the importer that takes a constraint template.</summary>
[DataContract("ConstraintTemplateImporter")]
public sealed record ConstraintTemplateImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxpriorities</c>.</summary>
/// <remarks>
///     The names a project's authors pick priorities from. What the importer adds over reading the
///     file is the two things that make a ladder a ladder rather than a list: no two rungs may share a
///     name, and no two may share a value — because a rung that resolves to the same integer as
///     another is a rung whose position in the order is decided by whichever clip loaded first.
/// </remarks>
[Importer(PriorityLadderContent.Extension)]
public sealed class PriorityLadderImporter : AssetImporter<PriorityLadderImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string LadderType = "PriorityLadderContent";

    /// <inheritdoc />
    public override int Version => PriorityLadderContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        PriorityLadderImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await ShapeYaml.ReadAsync<PriorityLadderContent>(context, cancellationToken).ConfigureAwait(false)
            is not { } ladder) {
            return context.Finish();
        }

        Check(context, ladder);
        context.Write(SubAssetId.Main, LadderType, Serializer.ToBytes(ladder));

        return context.Finish();
    }

    static void Check(ImportContext context, PriorityLadderContent ladder) {
        HashSet<string> names = new(StringComparer.Ordinal);
        Dictionary<int, string> values = [];

        foreach (var rung in ladder.Rungs) {
            if (!names.Add(rung.Name)) {
                context.Report(
                    ImportSeverity.Error,
                    $"'{rung.Name}' is declared twice. Which of them a clip means would depend on the order the "
                    + "file was written in."
                );
            }

            if (!values.TryAdd(rung.Value, rung.Name)) {
                context.Report(
                    ImportSeverity.Error,
                    $"'{rung.Name}' and '{values[rung.Value]}' are both {rung.Value}. Two rungs at one value have "
                    + "no order between them, which is the only thing a ladder is for."
                );
            }

            // ⚠ A sub-step is clamped to ±99, so any two rungs closer than a hundred apart can be
            // crossed by one — `look+50` outranking `aim` silently, and only for the clips that used a
            // sub-step. The declared step is what the editor spaces *new* rungs by and says nothing
            // about the ones already here, which is why the test is against 100 and not against it.
            foreach (var other in ladder.Rungs) {
                var apart = other.Value - rung.Value;

                if (apart is > 0 and < 100) {
                    context.Report(
                        ImportSeverity.Warning,
                        $"'{rung.Name}' and '{other.Name}' are only {apart} apart, and a sub-step may be up to 99. "
                        + $"A clip asking for '{rung.Name}+{apart}' would outrank '{other.Name}' without saying so."
                    );
                }
            }
        }
    }
}

/// <summary>Compiles a <c>.vxconstraints</c> template.</summary>
/// <remarks>
///     A named, versioned bundle of tags with relative timings. The check is that the timings really
///     are relative: a template whose tags run past one is a template captured from a clip and never
///     re-based, and it will place its tags off the end of every clip it is applied to.
/// </remarks>
[Importer(ConstraintTemplateContent.Extension)]
public sealed class ConstraintTemplateImporter : AssetImporter<ConstraintTemplateImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string TemplateType = "ConstraintTemplateContent";

    /// <inheritdoc />
    public override int Version => ConstraintTemplateContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        ConstraintTemplateImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await ShapeYaml.ReadAsync<ConstraintTemplateContent>(context, cancellationToken).ConfigureAwait(false)
            is not { } template) {
            return context.Finish();
        }

        if (!Check(context, template)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, TemplateType, Serializer.ToBytes(template));
        return context.Finish();
    }

    static bool Check(ImportContext context, ConstraintTemplateContent template) {
        var ok = true;

        if (string.IsNullOrWhiteSpace(template.Name)) {
            context.Report(
                ImportSeverity.Error,
                "It has no name. The name is written into every tag it produces and is how a re-apply finds them "
                + "again, so a nameless template can be applied once and never maintained."
            );

            ok = false;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var tag in template.Tags) {
            if (string.IsNullOrWhiteSpace(tag.Name)) {
                context.Report(ImportSeverity.Error, "One of its tags has no name, so a re-apply could not match it.");
                ok = false;

                continue;
            }

            if (!seen.Add(tag.Name)) {
                context.Report(
                    ImportSeverity.Error,
                    $"It has two tags called '{tag.Name}'. A re-apply matches on the name, so one of them would "
                    + "shadow the other for ever."
                );

                ok = false;
            }

            if (tag.Begin is < 0f or > 1f || tag.End is < 0f or > 1f) {
                context.Report(
                    ImportSeverity.Error,
                    $"'{tag.Name}' runs from {tag.Begin:0.###} to {tag.End:0.###}, and a template's timings are "
                    + "fractions of its own span. This looks like a template captured from a clip and never "
                    + "re-based; it would place its tags off the end of every clip it is applied to."
                );

                ok = false;
            }
        }

        return ok;
    }
}
