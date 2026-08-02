// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;
using Vixen.Animation;
using Vixen.Animation.Motions;
using Vixen.Animation.Moves;

namespace Vixen.Benchmarks.Animation;

/// <summary>What it costs to ask a move set what to play.</summary>
/// <remarks>
///     <para>
///         <b>The number this exists to defend is 5 µs for a 500-entry set</b>, which is the budget
///         the design was accepted on. A set that size is a large character's whole vocabulary
///         several times over, and the pass is linear — so if 500 fits, everything real fits.
///     </para>
///     <para>
///         ⚠ <b>The pass is not per frame.</b> A query is a value and the selector only runs when it
///         changes, so a hundred characters standing still cost nothing at all. What is measured here
///         is the frame where the question <i>does</i> change, which for a player is a few times a
///         second and for a crowd is spread across them.
///     </para>
///     <para>
///         <see cref="Required" /> is a parameter because the two paths are genuinely different work:
///         a required facet most candidates lack is rejected on its first comparison, so the filter
///         does most of the job and the scorer never runs. The unfiltered case is the honest worst
///         case and the one the budget is quoted against.
///     </para>
/// </remarks>
[MemoryDiagnoser]
public class MoveSelectionBenchmarks {
    static readonly string[] Gaits = ["idle", "walk", "jog", "run", "sprint"];
    static readonly string[] Conditions = ["normal", "injured", "tired", "carrying", "drunk"];
    static readonly string[] Surfaces = ["ground", "ice", "snow", "sand", "water"];

    MoveSet set = null!;
    MoveQuery query;

    /// <summary>How many moves the set holds.</summary>
    [Params(50, 500)]
    public int Moves { get; set; }

    /// <summary>Whether the query narrows the field before scoring.</summary>
    [Params(false, true)]
    public bool Required { get; set; }

    [GlobalSetup]
    public void Setup() {
        var skeleton = Rigs.Humanoid();
        var clip = AnimationClip.Create(Rigs.Clip(skeleton, "Move", 1, 30), skeleton);
        var motion = new ClipMotion(clip);
        var entries = new MoveEntry[Moves];

        for (var index = 0; index < Moves; index++) {
            var gait = Gaits[index % Gaits.Length];

            entries[index] = new(
                $"move-{index}",
                motion,
                FacetSet.Of(
                    ("role", "loop"),
                    ("gait", gait),
                    ("condition", Conditions[index / Gaits.Length % Conditions.Length]),
                    ("surface", Surfaces[index / (Gaits.Length * Conditions.Length) % Surfaces.Length])
                ),
                new() { Speed = 0.5f + (index % Gaits.Length * 1.5f), MinRate = 0.85f, MaxRate = 1.15f }
            );
        }

        set = MoveSet.Of("bench", entries);

        WeightedFacet[] preferred = [
            new(Facet.Of("condition", "injured"), 2f),
            new(Facet.Of("surface", "ice"), 1.5f),
            new(Facet.Of("gait", "jog"), 1f)
        ];

        query = new MoveQuery {
            Required = Required ? FacetSet.Of(("gait", "jog")) : FacetSet.Empty,
            Preferred = preferred,
            Numeric = new() { Speed = 2.4f },
            RepeatPenalty = 0.25f
        };
    }

    /// <summary>The whole pass: filter, score, pick, retime.</summary>
    [Benchmark]
    public MoveSelection Choose() => QueryMoveSelector.Shared.Choose(set, query, DefaultMoveScorer.Shared);
}
