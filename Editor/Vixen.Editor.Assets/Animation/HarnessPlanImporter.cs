// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Animation;

/// <summary>Settings for the importer that takes a variation harness plan.</summary>
[DataContract("HarnessPlanImporter")]
public sealed record HarnessPlanImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxharness</c>.</summary>
/// <remarks>
///     <para>
///         A declared variation run: which clip, on which rig, across which bodies, props and ground,
///         and how far off is too far. The thresholds are the point of the file existing — numbers
///         written into a test are numbers the person authoring the clip cannot see and will not
///         believe.
///     </para>
///     <para>
///         ⚠ <b>Three checks, and two of them are about a run nobody meant to ask for.</b> Axes
///         multiply, so a plan is very easy to write that takes an hour; and a plan with thresholds
///         all left at zero judges nothing at all, which is a green build that means nothing.
///     </para>
/// </remarks>
[Importer(HarnessPlanContent.Extension)]
public sealed class HarnessPlanImporter : AssetImporter<HarnessPlanImportSettings> {
    /// <summary>How many configurations a plan may declare before it is worth mentioning.</summary>
    /// <remarks>
    ///     Chosen against the exit criterion rather than from nothing: doc 34's claim is three bodies,
    ///     and a project checking a prop class across a body range lands in the dozens. A hundred and
    ///     twenty-eight is where a run stops being something somebody waits for.
    /// </remarks>
    public const int BusyRun = 128;

    /// <summary>The alias of the type this writes.</summary>
    public const string PlanType = "HarnessPlanContent";

    static HarnessPlanImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => HarnessPlanContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        HarnessPlanImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await ShapeYaml.ReadAsync<HarnessPlanContent>(context, cancellationToken).ConfigureAwait(false)
            is not { } plan) {
            return context.Finish();
        }

        if (!Check(context, plan)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, PlanType, Serializer.ToBytes(plan));
        return context.Finish();
    }

    static bool Check(ImportContext context, HarnessPlanContent plan) {
        var ok = true;

        // ⚠ `IsNullOrWhiteSpace` and not `.Length`, because a key written with no value after it
        // binds as null rather than as empty — `clip:` on a line of its own is exactly what a
        // half-filled plan looks like, and a check that threw on it would take the import down
        // instead of reporting the one thing it is there to report.
        if (string.IsNullOrWhiteSpace(plan.Clip) || string.IsNullOrWhiteSpace(plan.Rig)) {
            context.Report(
                ImportSeverity.Error,
                "It names no clip or no rig. A harness with nothing to play is a build step that always passes."
            );

            ok = false;
        }

        // ⚠ Not an error. A plan may legitimately be run for its report rather than as a gate — an
        // author sweeping a body range wants the matrix, not a verdict — and refusing that would make
        // the file useless for the thing it is most used for.
        if (!plan.Thresholds.Bake().Any) {
            context.Report(
                ImportSeverity.Warning,
                "Every threshold is zero, so this plan judges nothing: it will produce a report and always pass. "
                + "Set at least one if it is meant to be a gate."
            );
        }

        if (plan.Samples < 2) {
            context.Report(
                ImportSeverity.Error,
                $"It asks for {plan.Samples} sample(s). Two is the fewest that has a velocity between them, and "
                + "the velocity is what catches a hand that snaps."
            );

            ok = false;
        }

        foreach (var axis in plan.Props) {
            if (string.IsNullOrWhiteSpace(axis.Slot)) {
                context.Report(ImportSeverity.Error, "A prop axis names no slot, so no goal could reach it.");
                ok = false;
            }

            if (axis.Values.Any(static prop => string.IsNullOrWhiteSpace(prop.Name))) {
                context.Report(
                    ImportSeverity.Warning,
                    $"A prop on the '{axis.Slot}' axis has no name, so the report will not say which one failed."
                );
            }
        }

        foreach (var scale in plan.Bodies) {
            if (scale <= 0f) {
                context.Report(ImportSeverity.Error, $"A body scale of {scale} is not a body.");
                ok = false;
            }
        }

        var configurations = plan.Configurations;

        if (configurations > BusyRun) {
            context.Report(
                ImportSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"It declares {configurations} configurations × {plan.Samples} samples. Axes multiply, and a "
                    + $"run this size is one somebody starts by accident — {plan.Bodies.Count} bodies, "
                    + $"{plan.Ground.Count} ground step(s) and {plan.Props.Count} prop axis(es)."
                )
            );
        }

        return ok;
    }
}
