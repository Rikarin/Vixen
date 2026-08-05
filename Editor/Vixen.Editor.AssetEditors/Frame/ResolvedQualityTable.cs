// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>Which layer of doc 39's waterfall a resolved quality number came from.</summary>
/// <remarks>
///     The three layers <c>RenderQuality.Resolve</c> folds, named from the bottom up. There is no
///     fourth: the tier itself is chosen above all of them and is not a layer, because it selects
///     which column every layer is read from rather than contributing a value.
/// </remarks>
public enum QualityLayer {
    /// <summary>The engine's own complete table — <c>RenderQuality.EngineDefaults</c>.</summary>
    Engine,

    /// <summary>The project's <c>RenderQuality.vxpreset</c>.</summary>
    Project,

    /// <summary>The frame document's own inline <c>preset:</c>, which is the top vote.</summary>
    Document
}

/// <summary>One resolved quality knob: what it is called, what it resolved to, and who said so.</summary>
/// <param name="Group">The document group it lives in — <c>shadows</c>, <c>post</c>, <c>gi</c>.</param>
/// <param name="Name">The knob's own document name — <c>cascadeResolution</c>.</param>
/// <param name="Value">What it resolved to, formatted the way the document spells it.</param>
/// <param name="Layer">Which layer of the waterfall stated it.</param>
/// <param name="Overridden">Whether anything above the engine table stated it.</param>
public readonly record struct ResolvedQualityKnob(
    string Group,
    string Name,
    string Value,
    QualityLayer Layer,
    bool Overridden
) {
    /// <summary>How a <c>.vxpreset</c> would address it: <c>shadows.cascadeResolution</c>.</summary>
    public string Path => $"{Group}.{Name}";
}

/// <summary>
///     Doc 39's resolved stack for the quality table: every knob, its value, and which of the three
///     layers decided it.
/// </summary>
/// <remarks>
///     <para>
///         <b>The panel exists because the final number is the least useful half of the answer.</b>
///         The waterfall folds <em>per parameter</em> — engine defaults, then the project preset,
///         then the document's inline overlay — so any one number a person is looking at may have
///         come from any of three files, and a table of decided numbers with no provenance answers
///         "what is it" while the actual question is always "why is it that". Unity earned a decade
///         of support load from exactly this gap, with quality values split across three homes and
///         no view that said which one won.
///     </para>
///     <para>
///         ⚠ <b>Enumerated from the override schema by reflection, never from a list written
///         here.</b> <see cref="QualityTierOverrides" />'s nine group records <em>are</em> the knob
///         list — <c>RenderQuality.EngineDefaults</c> is expressed in that same schema and is
///         complete — so walking it finds every knob the engine has, and a knob added tomorrow
///         appears in this panel without anybody touching this file. A hand-written list of sixty-two
///         names would be a second declaration of the quality table, and the second declaration is
///         always the one that drifts.
///     </para>
///     <para>
///         ⚠ <b>And the walk reproduces <c>Pick</c>'s rule rather than diffing resolved values.</b>
///         <c>Of(top) ?? Of(mid) ?? Of(engine)</c> is "the highest layer that <em>states</em> it
///         wins", which is not the same as "the highest layer that changes it": a project preset
///         that pins <c>cascadeCount: 4</c> where the engine already says 4 has still taken
///         ownership of that number, and a panel that reported it as the engine's would send someone
///         to edit the wrong file. <see cref="Cross" /> is what keeps the reproduction honest.
///     </para>
/// </remarks>
public static class ResolvedQualityTable {
    /// <summary>The nine groups, in the order <c>QualityTierOverrides</c> declares them.</summary>
    static readonly PropertyInfo[] Groups = typeof(QualityTierOverrides)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Folds one tier and says, per knob, what it resolved to and which layer stated it.</summary>
    /// <param name="tier">Which column is read, at every layer.</param>
    /// <param name="project">The project's <c>.vxpreset</c>, or null.</param>
    /// <param name="overlay">The document's inline <c>preset:</c>, or null.</param>
    /// <returns>Every knob, grouped in declaration order and named as a document names it.</returns>
    public static IReadOnlyList<ResolvedQualityKnob> Resolve(
        QualityTier tier,
        RenderQualityAsset? project = null,
        RenderQualityAsset? overlay = null
    ) {
        var layers = new (QualityLayer Layer, QualityTierOverrides? Values)[] {
            (QualityLayer.Document, TierOf(overlay, tier)),
            (QualityLayer.Project, TierOf(project, tier)),
            (QualityLayer.Engine, TierOf(RenderQuality.EngineDefaults, tier))
        };

        var knobs = new List<ResolvedQualityKnob>();

        foreach (var group in Groups) {
            var name = Spelled(group.Name);

            foreach (var knob in group.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (!Claim(layers, group, knob, out var value, out var layer)) {
                    // A hole in the engine table, which `RenderQuality.Resolve` throws for by name.
                    // Shown rather than skipped, because a knob missing from the panel reads as a
                    // knob the engine does not have.
                    knobs.Add(new(name, Spelled(knob.Name), "—", QualityLayer.Engine, false));
                    continue;
                }

                knobs.Add(new(name, Spelled(knob.Name), Format(value), layer, layer != QualityLayer.Engine));
            }
        }

        return knobs;
    }

    /// <summary>What the engine itself resolved the same tier to, for a caller that wants both.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="project">The project's preset, or null.</param>
    /// <param name="overlay">The document's overlay, or null.</param>
    /// <returns>The engine's own answer.</returns>
    /// <remarks>
    ///     ⚠ <b>Here so that the panel and the frame cannot disagree without a test noticing.</b>
    ///     <see cref="Resolve" /> walks the override schema and <c>RenderQuality.Resolve</c> walks
    ///     its own sixty-two <c>Pick</c> calls; they are the same waterfall read two ways, and the
    ///     day they stop agreeing is the day the panel starts lying about which file to edit.
    ///     <c>FrameQualityStackTests</c> compares them knob for knob.
    /// </remarks>
    public static ResolvedQuality Cross(
        QualityTier tier,
        RenderQualityAsset? project = null,
        RenderQualityAsset? overlay = null
    ) => RenderQuality.Resolve(tier, project, overlay);

    /// <summary>How many knobs the table has, which is a fact worth stating rather than counting.</summary>
    public static int Count => Resolve(QualityTier.High).Count;

    static bool Claim(
        (QualityLayer Layer, QualityTierOverrides? Values)[] layers,
        PropertyInfo group,
        PropertyInfo knob,
        out object? value,
        out QualityLayer layer
    ) {
        foreach (var (candidate, values) in layers) {
            if (values is null || group.GetValue(values) is not { } opinions) {
                continue;
            }

            if (knob.GetValue(opinions) is { } stated) {
                value = stated;
                layer = candidate;

                return true;
            }
        }

        value = null;
        layer = QualityLayer.Engine;

        return false;
    }

    static QualityTierOverrides? TierOf(RenderQualityAsset? preset, QualityTier tier) => preset is null
        ? null
        : tier switch {
            QualityTier.Low => preset.Low,
            QualityTier.Medium => preset.Medium,
            QualityTier.Epic => preset.Epic,
            _ => preset.High
        };

    /// <summary>The document's spelling of a member name, which is its first letter lowered.</summary>
    /// <remarks>
    ///     The same transform <c>QualityTableSnapshotTests</c> makes, and for the same reason: the
    ///     reader of this panel is somebody about to type the name into a <c>.vxpreset</c>, so the
    ///     panel should be in the vocabulary they will author in rather than in C#'s.
    /// </remarks>
    static string Spelled(string name) =>
        name.Length == 0 ? name : string.Concat(char.ToLowerInvariant(name[0]).ToString(), name.AsSpan(1));

    /// <summary>A value as the document writes it — invariant, so a comma locale does not lie.</summary>
    static string Format(object? value) => value switch {
        null => "—",
        bool flag => flag ? "true" : "false",
        float number => number.ToString("0.####", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "—"
    };
}
