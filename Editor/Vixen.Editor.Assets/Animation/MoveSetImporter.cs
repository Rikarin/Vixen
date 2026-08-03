// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Serialization;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Editor.Assets.Animation;

/// <summary>Settings for the importer that takes a move set.</summary>
[DataContract("MoveSetImporter")]
public sealed record MoveSetImportSettings : IImportSettings {
    /// <inheritdoc />
    public int Version { get; init; } = 1;
}

/// <summary>Compiles a <c>.vxmoveset</c>.</summary>
/// <remarks>
///     <para>
///         A movement vocabulary as a table: rows are moves, columns are facets. What the importer
///         adds over reading the file is the four things a table of a few hundred rows invites, and
///         every one of them is silent at runtime.
///     </para>
///     <para>
///         ⚠ <b>A duplicate name is an error and not a warning.</b> A move's key is hashed from its
///         name, and the overlay composes on that key — two rows with one name means the second
///         silently replaces the first, in a file where the first is still sitting there being read
///         by whoever maintains it.
///     </para>
/// </remarks>
[Importer(MoveSetContent.Extension)]
public sealed class MoveSetImporter : AssetImporter<MoveSetImportSettings> {
    /// <summary>The alias of the type this writes.</summary>
    public const string SetType = "MoveSetContent";

    static MoveSetImporter() => MathScalars.Register();

    /// <inheritdoc />
    public override int Version => MoveSetContent.Current;

    /// <inheritdoc />
    protected override async ValueTask<ImportResult> ImportAsync(
        ImportContext context,
        MoveSetImportSettings settings,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (await ShapeYaml.ReadAsync<MoveSetContent>(context, cancellationToken).ConfigureAwait(false)
            is not { } set) {
            return context.Finish();
        }

        if (!Check(context, set)) {
            return context.Finish();
        }

        context.Write(SubAssetId.Main, SetType, Serializer.ToBytes(set));
        return context.Finish();
    }

    static bool Check(ImportContext context, MoveSetContent set) {
        var ok = true;
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (var entry in set.Entries) {
            if (entry.Name.Length == 0) {
                context.Report(
                    ImportSeverity.Error,
                    "A row has no name. A move's key is hashed from its name, so a nameless row cannot be "
                    + "overlaid, cannot be reported by a selection and cannot be referred to at all."
                );

                ok = false;
                continue;
            }

            if (!names.Add(entry.Name)) {
                context.Report(
                    ImportSeverity.Error,
                    $"'{entry.Name}' appears twice. The overlay composes on the name, so the second row silently "
                    + "replaces the first — in a file where the first is still there to be read."
                );

                ok = false;
            }

            if (entry.Clip.Length == 0) {
                context.Report(ImportSeverity.Error, $"'{entry.Name}' names no clip, so it would play silence.");
                ok = false;
            }

            Traits(context, entry);
        }

        Roles(context, set);

        return ok;
    }

    static void Traits(ImportContext context, MoveEntryRecord entry) {
        if (entry.MinRate > entry.MaxRate) {
            context.Report(
                ImportSeverity.Error,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{entry.Name}' admits rates from {entry.MinRate:0.##} to {entry.MaxRate:0.##}, which is no range at all. The selector clamps into this interval, so it would retime every selection to the wrong end of it."
                )
            );
        }

        if (entry.MinRate <= 0f) {
            context.Report(ImportSeverity.Error, $"'{entry.Name}' admits a rate of {entry.MinRate}, which stops or reverses it.");
        }

        // ⚠ Not an error. A move that goes nowhere — an idle, a gesture — legitimately has no speed
        // and admits no retiming, and the selector already answers a rate of one for it.
        if (entry.Speed <= 1e-4f && (entry.MinRate < 1f || entry.MaxRate > 1f)) {
            context.Report(
                ImportSeverity.Warning,
                $"'{entry.Name}' goes nowhere and yet admits retiming. Nothing can be inferred from a speed of "
                + "zero, so the rate range is ignored and every selection plays it at one."
            );
        }

        if (entry.FootPhase is < 0f or >= 1f) {
            context.Report(
                ImportSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{entry.Name}' plants its foot at {entry.FootPhase:0.###}, which is not inside the clip. A contact-synced transition into it would align on a moment that never happens."
                )
            );
        }
    }

    /// <summary>
    ///     ⚠ <b>The reserved <c>role</c> vocabulary is the one thing a set may not spell its own way.</b>
    /// </summary>
    /// <remarks>
    ///     Everything else in a project's facets is the project's business. Transition rules and phase
    ///     sync both ask what kind of move something is, and a set that answers <c>role=looping</c>
    ///     gets no answer from either — silently, at runtime, as a transition that never matches.
    /// </remarks>
    static void Roles(ImportContext context, MoveSetContent set) {
        foreach (var entry in set.Entries) {
            foreach (var facet in entry.Facets) {
                if (!string.Equals(facet.Key, "role", StringComparison.Ordinal)) {
                    continue;
                }

                if (!MoveRole.IsKnown(Symbol.Intern(facet.Value))) {
                    context.Report(
                        ImportSeverity.Error,
                        $"'{entry.Name}' says role={facet.Value}, which is not one of the reserved values "
                        + $"({string.Join(", ", MoveRole.All)}). A role nothing recognises is worse than no role: "
                        + "the transition rules and the phase sync both read this key, and neither would match."
                    );
                }
            }
        }
    }
}
