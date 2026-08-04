// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Where a world key's value comes from, as a file names it.</summary>
public enum GoapSourceKind : byte {
    /// <summary>A numeric blackboard key.</summary>
    Blackboard,

    /// <summary>Something the game registered by name on the resolver.</summary>
    Registered,

    /// <summary>A fixed number, for a key nobody has wired up yet.</summary>
    Constant
}

/// <summary>One world key, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>Every member is settable.</b> The YAML binder takes part only in members it can write on
///     both sides, so a get-only collection is written out and then silently skipped on load.
/// </remarks>
[DataContract("GoapKey")]
public sealed class GoapKeyContent {
    /// <summary>What the key is called. Conditions and effects name it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where its value comes from.</summary>
    public GoapSourceKind Source { get; set; }

    /// <summary>The blackboard key or the registered source's name.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The number, for <see cref="GoapSourceKind.Constant" />.</summary>
    public int Value { get; set; }
}

/// <summary>One condition, as a file holds it.</summary>
[DataContract("GoapCondition")]
public sealed class GoapConditionContent {
    /// <summary>Which world key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>How it is compared.</summary>
    public GoapComparison Comparison { get; set; }

    /// <summary>To what.</summary>
    public int Value { get; set; }
}

/// <summary>One effect, as a file holds it.</summary>
/// <remarks>
///     ⚠ A direction and not an amount — doc 37 § D10. "Eating reduces hunger" stays true while a
///     designer tunes the numbers, and it is all the resolver needs.
/// </remarks>
[DataContract("GoapEffect")]
public sealed class GoapEffectContent {
    /// <summary>Which world key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Whether it goes up. Down otherwise.</summary>
    public bool Increases { get; set; } = true;
}

/// <summary>One action, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>The task is named the same way a behaviour tree and a utility set name one</b>, out of the
///     same <see cref="BehaviorNodeSchema" /> and into the same registry. Three planners, one action
///     library — which is doc 37 § D2, and the third of three files to prove it.
/// </remarks>
[DataContract("GoapAction")]
public sealed class GoapActionContent {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which task it runs, as <see cref="BehaviorNodeSchema" /> names it.</summary>
    public string Task { get; set; } = string.Empty;

    /// <summary>The task's fields, by name.</summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>What has to be true before it can run.</summary>
    public List<GoapConditionContent> Conditions { get; set; } = [];

    /// <summary>What it changes.</summary>
    public List<GoapEffectContent> Effects { get; set; } = [];

    /// <summary>What it costs before the world has its say.</summary>
    public float Cost { get; set; } = 1f;

    /// <summary>Which target sensor says where it happens, or empty for nowhere.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>How close is close enough, in metres.</summary>
    public float StoppingDistance { get; set; } = 1.5f;

    /// <summary>Whether the agent has to be there first.</summary>
    public GoapMoveMode Move { get; set; }
}

/// <summary>One goal, as a file holds it.</summary>
[DataContract("GoapGoal")]
public sealed class GoapGoalContent {
    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which goal wins when more than one is unmet. Higher first.</summary>
    public int Priority { get; set; }

    /// <summary>What has to hold for it to be met.</summary>
    public List<GoapConditionContent> Conditions { get; set; } = [];
}

/// <summary>A GOAP domain as a file: tables of keys, actions and goals.</summary>
/// <remarks>
///     ⚠ <b>Tables and not a graph, and doc 37 § Part 5 is explicit about why.</b> The edges of a GOAP
///     graph are not authored — they are <i>computed</i> from which effects satisfy which conditions.
///     Drawing them by hand would be authoring the same fact twice, and the two copies would disagree
///     the first time somebody edited a condition. So the file is three tables and the graph is
///     derived, read-only, in the viewer.
/// </remarks>
[DataContract("GoapDomain")]
public sealed class GoapDomainContent {
    /// <summary>What a GOAP domain is called on disk.</summary>
    public const string Extension = ".vxgoap";

    /// <summary>The version this build writes and reads.</summary>
    public const int Current = 1;

    /// <summary>Which version wrote this file.</summary>
    public int Version { get; set; } = Current;

    /// <summary>What the domain is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The blackboard keys it declares, when it declares its own.</summary>
    public List<BehaviorKeyContent> Blackboard { get; set; } = [];

    /// <summary>The world keys it reasons about.</summary>
    public List<GoapKeyContent> Keys { get; set; } = [];

    /// <summary>What its agents can do.</summary>
    public List<GoapActionContent> Actions { get; set; } = [];

    /// <summary>What they might want.</summary>
    public List<GoapGoalContent> Goals { get; set; } = [];

    /// <summary>How many nodes one search may expand.</summary>
    public int NodeBudget { get; set; } = 512;

    /// <summary>How long a chain may get.</summary>
    public int DepthLimit { get; set; } = 8;

    /// <summary>Builds the blackboard this domain's own keys describe.</summary>
    /// <param name="diagnostics">Where to put anything wrong with them.</param>
    /// <returns>The layout.</returns>
    public BlackboardLayout BuildLayout(ICollection<BehaviorTreeDiagnostic>? diagnostics = null) {
        var builder = new BlackboardLayoutBuilder();

        foreach (var key in Blackboard) {
            try {
                builder.Add(key.Name, key.Type);
            } catch (Exception error) when (error is InvalidOperationException or ArgumentException) {
                diagnostics?.Add(new(Symbol.Intern(key.Name), error.Message));
            }
        }

        return builder.Build();
    }

    /// <summary>What bounds a search of this domain.</summary>
    /// <returns>The settings.</returns>
    public GoapSettings BuildSettings() => new() { NodeBudget = NodeBudget, DepthLimit = DepthLimit };
}
