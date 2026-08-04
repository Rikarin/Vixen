// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Curves;

namespace Vixen.Ai;

/// <summary>Where a consideration's number comes from, as a file names it.</summary>
/// <remarks>
///     Three, not one per shipped input: the two that only need a key are named by kind, and anything
///     else is a name the game registered. A file cannot hold a lambda, and a kind per project-defined
///     input would be an enum this repository has to grow for other people's games.
/// </remarks>
public enum UtilityInputKind : byte {
    /// <summary>A numeric blackboard key, normalised between two bounds.</summary>
    Blackboard,

    /// <summary>How far the agent is from what a key names, as a fraction of a range.</summary>
    Distance,

    /// <summary>Something the game registered by name on the resolver.</summary>
    Registered
}

/// <summary>One consideration, as a file holds it: where the number comes from and what shape it goes through.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable, and that is not laziness.</b> The YAML binder takes part only in
///     members it can write on both sides, so a get-only collection is written out and then silently
///     skipped on load — a file that loses its contents by round-tripping.
/// </remarks>
[DataContract("UtilityConsideration")]
public sealed class UtilityConsiderationContent {
    /// <summary>What it is called, in the table and in a diagnostic.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where its number comes from.</summary>
    public UtilityInputKind Input { get; set; }

    /// <summary>The blackboard key it reads, for the two kinds that read one.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The registered input's name, for <see cref="UtilityInputKind.Registered" />.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>What maps to zero, for a blackboard input.</summary>
    public float Minimum { get; set; }

    /// <summary>What maps to one, for a blackboard input; or the far distance for a distance input.</summary>
    public float Maximum { get; set; } = 1f;

    /// <summary>Which shape it goes through.</summary>
    public ResponseCurveKind Curve { get; set; }

    /// <summary>Slope, or the bell's height.</summary>
    public float Slope { get; set; } = 1f;

    /// <summary>Exponent, steepness or width — whichever the shape has.</summary>
    public float Exponent { get; set; } = 1f;

    /// <summary>Vertical shift.</summary>
    public float Shift { get; set; }

    /// <summary>Horizontal shift.</summary>
    public float Centre { get; set; }

    /// <summary>The keys, for <see cref="ResponseCurveKind.Sampled" />.</summary>
    public List<UtilityCurveKeyContent> Keys { get; set; } = [];

    /// <summary>The curve this describes.</summary>
    /// <returns>The curve.</returns>
    public ResponseCurve BuildCurve() => new() {
        Kind = Curve,
        Slope = Slope,
        Exponent = Exponent,
        Shift = Shift,
        Centre = Centre,
        Keys = Curve == ResponseCurveKind.Sampled && Keys.Count > 0
            ? [.. Keys.OrderBy(key => key.Time).Select(key => key.ToSample())]
            : null
    };
}

/// <summary>One key of a sampled curve, as a file holds it.</summary>
[DataContract("UtilityCurveKey")]
public sealed class UtilityCurveKeyContent {
    /// <summary>Where along the input it sits.</summary>
    public float Time { get; set; }

    /// <summary>What it scores there.</summary>
    public float Value { get; set; }

    /// <summary>The slope coming in.</summary>
    public float InTangent { get; set; }

    /// <summary>The slope going out.</summary>
    public float OutTangent { get; set; }

    /// <summary>How the tangents are worked out.</summary>
    public TangentMode Mode { get; set; }

    /// <summary>This key, as the sampler wants it.</summary>
    /// <returns>The sample.</returns>
    public CurveSample ToSample() => new(Time, Value, InTangent, OutTangent, Mode);
}

/// <summary>One thing the agent might do, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>The task is named the same way a behaviour tree names one</b>, out of the same
///     <see cref="BehaviorNodeSchema" /> and built by the same factories. That is doc 37 § D2's whole
///     payoff made visible: a project writes <c>MoveToTask</c> once, declares it once, and gets it in
///     a tree, in a utility set and — when P6 lands — in a GOAP plan.
/// </remarks>
[DataContract("UtilityAction")]
public sealed class UtilityActionContent {
    /// <summary>What it is called, in the table and in the debug record.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which task it runs, as <see cref="BehaviorNodeSchema" /> names it.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>The task's fields, by name.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>Its multiplier. 1 for ambient, 2–3 for important, 5 for emergency.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>How long after it ends before it may be chosen again, in seconds.</summary>
    public float Cooldown { get; set; }

    /// <summary>Which group it is in. Higher wins, under the bucketed selector.</summary>
    public int Bucket { get; set; }

    /// <summary>What decides how good it is.</summary>
    public List<UtilityConsiderationContent> Considerations { get; set; } = [];
}

/// <summary>Which of the scored actions wins, as a file names it.</summary>
public enum UtilitySelectorKind : byte {
    /// <summary>The best one.</summary>
    Highest,

    /// <summary>Score as weight.</summary>
    WeightedRandom,

    /// <summary>Weighted random among the best few.</summary>
    TopWeightedRandom,

    /// <summary>Dual utility: the highest bucket with anything in it, then the best inside it.</summary>
    Bucketed
}

/// <summary>A utility set as a file: a list of actions, each with a list of considerations.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A list and not a graph, and doc 37 § P5 is explicit about why.</b> A utility set has
///         no edges: drawing it on a canvas would be a canvas whose wires all run from a column of
///         inputs to a column of actions and carry nothing. What it wants is a two-pane table and a
///         curve, which is what the editor gives it.
///     </para>
///     <para>
///         <b>The keys are optional.</b> A set that is an agent's whole planner shares the layout with
///         whatever else that agent runs, so the compiler takes one. A set authored on its own — a
///         test, a sample, a first draft — declares its own and <see cref="BuildLayout" /> makes it,
///         exactly the way a <c>.vxbt</c> does.
///     </para>
/// </remarks>
[DataContract("UtilitySet")]
public sealed class UtilitySetContent {
    /// <summary>What a utility set is called on disk.</summary>
    public const string Extension = ".vxutility";

    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version wrote this file.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the set is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its blackboard's keys, when it declares its own.</summary>
    public List<BehaviorKeyContent> Keys { get; set; } = [];

    /// <summary>What the agent might do.</summary>
    public List<UtilityActionContent> Actions { get; set; } = [];

    /// <summary>Which of the scored actions wins.</summary>
    public UtilitySelectorKind Selector { get; set; }

    /// <summary>How many of the best to consider, for the top-weighted selector.</summary>
    public int SelectorCount { get; set; } = 3;

    /// <summary>How much is added to the running action's score.</summary>
    public float CommitmentBonus { get; set; } = 0.15f;

    /// <summary>Seconds between decisions.</summary>
    public float DecisionInterval { get; set; } = 0.2f;

    /// <summary>Builds the blackboard this set's own keys describe.</summary>
    /// <param name="diagnostics">Where to put anything wrong with them.</param>
    /// <returns>The layout.</returns>
    public BlackboardLayout BuildLayout(ICollection<BehaviorTreeDiagnostic>? diagnostics = null) {
        var builder = new BlackboardLayoutBuilder();

        foreach (var key in Keys) {
            try {
                builder.Add(key.Name, key.Type);
            } catch (Exception error) when (error is InvalidOperationException or ArgumentException) {
                diagnostics?.Add(new(Symbol.Intern(key.Name), error.Message));
            }
        }

        return builder.Build();
    }

    /// <summary>The selector this describes.</summary>
    /// <returns>The selector.</returns>
    public IUtilitySelector BuildSelector() => Selector switch {
        UtilitySelectorKind.WeightedRandom => UtilitySelectors.WeightedRandom,
        UtilitySelectorKind.TopWeightedRandom => UtilitySelectors.TopWeightedRandom(Math.Max(1, SelectorCount)),
        UtilitySelectorKind.Bucketed => UtilitySelectors.Bucketed,
        _ => UtilitySelectors.Highest
    };
}
