// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Moves;

/// <summary>A facet as a file holds it: two words.</summary>
[DataContract("FacetRecord")]
public sealed class FacetRecord {
    /// <summary>The axis. <c>role</c>, <c>style</c>, <c>stance</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The value on that axis.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>What the runtime reads.</summary>
    /// <returns>The facet.</returns>
    public Facet Bake() => Facet.Of(Key, Value);
}

/// <summary>One row of an authored move set.</summary>
/// <remarks>
///     ⚠ <b>The clip is named by address, not embedded.</b> Two moves in two sets routinely play the
///     same clip — a walk and an injured walk share their turn-in-place — and a set that carried
///     clips would carry it twice.
/// </remarks>
[DataContract("MoveEntryRecord")]
public sealed class MoveEntryRecord {
    /// <summary>What the move is called. Its key is hashed from this, so it is the identity.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The clip it plays, by asset address.</summary>
    public string Clip { get; set; } = string.Empty;

    /// <summary>What it is for.</summary>
    public List<FacetRecord> Facets { get; set; } = [];

    /// <summary>What it plays at as authored, in metres a second.</summary>
    public float Speed { get; set; }

    /// <summary>How fast it turns as authored, in radians a second. Positive is left.</summary>
    public float TurnRate { get; set; }

    /// <summary>The slowest playback rate it still reads correctly at.</summary>
    public float MinRate { get; set; } = 1f;

    /// <summary>The fastest.</summary>
    public float MaxRate { get; set; } = 1f;

    /// <summary>Where in normalised time the first foot plants.</summary>
    public float FootPhase { get; set; }

    /// <summary>What the runtime reads, once somebody has resolved the clip.</summary>
    /// <param name="motion">What plays.</param>
    /// <returns>The entry.</returns>
    public MoveEntry Bake(Motion motion) {
        ArgumentNullException.ThrowIfNull(motion);

        return new(
            Name,
            motion,
            FacetSet.Of([.. Facets.Select(static facet => facet.Bake())]),
            new() {
                Speed = Speed,
                TurnRate = TurnRate,
                MinRate = MinRate,
                MaxRate = MaxRate,
                FootPhase = FootPhase
            }
        );
    }
}

/// <summary>A transition rule as a file holds it.</summary>
[DataContract("TransitionRuleRecord")]
public sealed class TransitionRuleRecord {
    /// <summary>Which moves it applies leaving. Empty matches every move.</summary>
    public List<FacetRecord> From { get; set; } = [];

    /// <summary>Which moves it applies entering. Empty matches every move.</summary>
    public List<FacetRecord> To { get; set; } = [];

    /// <summary>How long the crossfade takes, in seconds. Zero is a cut.</summary>
    public float Duration { get; set; } = 0.25f;

    /// <summary>Its shape.</summary>
    public BlendEasing Easing { get; set; }

    /// <summary>How the incoming move's phase is chosen.</summary>
    public SyncMode Sync { get; set; }

    /// <summary>Whether the transition may happen at all.</summary>
    public bool Allowed { get; set; } = true;

    /// <summary>Which joints cross over, by name, or empty for all of them.</summary>
    /// <remarks>
    ///     <para>
    ///         What makes an upper body change instantly while the legs take their time. Named joints
    ///         and their descendants are the ones that blend; everything else cuts.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Named and not indexed, and resolved against whichever rig plays the set.</b> A
    ///         mask is an array of weights per joint, which is a fact about one skeleton — so a file
    ///         that stored one could only ever be used by the rig it was authored against. Naming the
    ///         joints is what lets one move set be played by two characters.
    ///     </para>
    /// </remarks>
    public List<string> Mask { get; set; } = [];

    /// <summary>What the runtime reads.</summary>
    /// <param name="skeleton">The rig the set is played on, for the mask, or <see langword="null" />.</param>
    /// <returns>The rule.</returns>
    /// <remarks>
    ///     ⚠ <b>A rule with a mask and no rig bakes without one rather than refusing.</b> A move set
    ///     is legitimately inspected with no skeleton in sight — that is what the editor's table does
    ///     — and a transition that blended the whole body is a far better answer there than a throw.
    ///     A joint the rig does not have is ignored for <see cref="BoneMask.Builder.Set(string, float, bool)" />'s
    ///     reason: a mask outlives the rig it was written for.
    /// </remarks>
    public TransitionRule Bake(Skeleton? skeleton = null) {
        BoneMask? mask = null;

        if (skeleton is not null && Mask.Count > 0) {
            var builder = BoneMask.Excluding(skeleton);

            foreach (var joint in Mask) {
                builder = builder.Set(joint, 1f);
            }

            mask = builder.Build();
        }

        return new(
            new(FacetSet.Of([.. From.Select(static facet => facet.Bake())])),
            new(FacetSet.Of([.. To.Select(static facet => facet.Bake())])),
            new(Duration, Easing, Sync, mask, Allowed)
        );
    }
}

/// <summary>A move set as a project authors it: a table, plus the sets it overlays.</summary>
/// <remarks>
///     <para>
///         <b>A table, because a move set is a table.</b> Rows are entries and columns are facets,
///         with no containment between rows and no container per style — which is the whole of D1's
///         argument, restated as a file format.
///     </para>
///     <para>
///         ⚠ <b>The overlay is a list of set addresses and it composes at bake.</b> An injured set is
///         three clips over a hundred, and the entries it does not name come from the set underneath;
///         resolving that at load rather than at bake would put a lookup chain in the selector's
///         inner loop, which is the one place in this whole subsystem that has a microsecond budget.
///     </para>
/// </remarks>
[DataContract("MoveSetContent")]
public sealed class MoveSetContent {
    /// <summary>The extension a project writes these under.</summary>
    public const string Extension = ".vxmoveset";

    /// <summary>The version this reader and writer speak.</summary>
    public const int Current = 1;

    /// <summary>Which version of the format this file is.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the set is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The sets this one overlays, by asset address, base first.</summary>
    public List<string> Bases { get; set; } = [];

    /// <summary>The rows.</summary>
    public List<MoveEntryRecord> Entries { get; set; } = [];

    /// <summary>The transition rules, in the order they are tried. First match wins.</summary>
    public List<TransitionRuleRecord> Rules { get; set; } = [];

    /// <summary>What the runtime reads.</summary>
    /// <param name="motions">
    ///     What each row's clip resolves to. A row whose clip cannot be resolved is left out, and its
    ///     name is reported through <paramref name="unresolved" />.
    /// </param>
    /// <param name="bases">The sets named in <see cref="Bases" />, already baked.</param>
    /// <param name="unresolved">Where the names of the rows that were dropped go.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     ⚠ <b>A row whose clip is missing is dropped rather than baked against nothing.</b> An entry
    ///     with no motion would be selected like any other and then play silence, which reads in game
    ///     as a character freezing — much harder to trace than a set with one fewer move in it.
    /// </remarks>
    public MoveSet Bake(
        Func<string, Motion?> motions,
        IEnumerable<MoveSet>? bases = null,
        ICollection<string>? unresolved = null
    ) {
        ArgumentNullException.ThrowIfNull(motions);

        List<MoveEntry> entries = [];

        foreach (var record in Entries) {
            if (motions(record.Clip) is { } motion) {
                entries.Add(record.Bake(motion));
            } else {
                unresolved?.Add(record.Name);
            }
        }

        return MoveSet.Compose(Name, bases, [.. entries]);
    }

    /// <summary>Every row, baked against nothing, for asking questions about the table.</summary>
    /// <param name="bases">The sets named in <see cref="Bases" />, already baked.</param>
    /// <returns>The set.</returns>
    /// <remarks>
    ///     ⚠ <b>What makes the editor's live query possible with no content build.</b> Selection reads
    ///     facets and traits and never touches a motion, so "what would this query pick, and why" is
    ///     answerable from the table alone — and an author who had to build content to ask it would
    ///     not ask it. Nothing here may be played: every row poses nothing at all.
    /// </remarks>
    public MoveSet Preview(IEnumerable<MoveSet>? bases = null) =>
        MoveSet.Compose(
            Name,
            bases,
            [.. Entries.Where(static entry => entry.Name.Length > 0).Select(static entry => entry.Bake(UnresolvedMotion.Shared))]
        );

    /// <summary>The transition policy this set declares.</summary>
    /// <param name="skeleton">
    ///     The rig it is played on, so a rule that names joints can build its mask, or
    ///     <see langword="null" /> for a policy whose transitions all move the whole body.
    /// </param>
    /// <returns>The policy.</returns>
    public RuleTransitionPolicy Policy(Skeleton? skeleton = null) =>
        new([.. Rules.Select(rule => rule.Bake(skeleton))]);
}

/// <summary>A move whose clip is not loaded. Poses nothing, and says so.</summary>
/// <remarks>
///     ⚠ <b>For tools, and never for a game.</b> <see cref="MoveSetContent.Bake" /> drops a row whose
///     clip it cannot resolve, because an entry that is selected and then plays silence reads in game
///     as a character freezing. An editor wants the opposite — the row is what is being edited — so
///     the two entry points differ in exactly this, and the type is public so the difference is
///     visible rather than hidden behind a flag.
/// </remarks>
public sealed class UnresolvedMotion : Motion {
    /// <summary>The one every unresolved row shares.</summary>
    public static UnresolvedMotion Shared { get; } = new() { Name = "unresolved" };

    /// <inheritdoc />
    public override float Length(AnimationParameters parameters) => 1f;

    /// <inheritdoc />
    public override RootMotionDelta Evaluate(in MotionContext context, Span<BoneTransform> destination) =>
        RootMotionDelta.None;
}
