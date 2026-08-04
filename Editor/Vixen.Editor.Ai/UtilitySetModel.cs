// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core.Curves;

namespace Vixen.Editor.Ai;

/// <summary>A utility set, and every gesture an editor makes to one.</summary>
/// <remarks>
///     <para>
///         The counterpart to <see cref="BehaviorTreeModel" />, and it is much smaller for the reason
///         doc 37 § P5 gives: <b>a utility set has no edges.</b> There is no reparent, no reorder that
///         changes meaning and no topology to keep valid — a set is a list of actions, each with a
///         list of considerations, and every operation here is on one of those two lists.
///     </para>
///     <para>
///         ⚠ <b>Order does not matter, and that is worth stating because it is the opposite of a
///         tree.</b> A composite's child order <i>is</i> its priority; a set's action order is a
///         display convenience and a tie-break, and nothing else. Moving a row up cannot change what
///         an agent does unless two actions score exactly the same, which is why this model has a
///         <see cref="Move" /> and no equivalent of the tree's careful reparent arithmetic.
///     </para>
/// </remarks>
public sealed class UtilitySetModel {
    /// <summary>Creates a model over a set.</summary>
    /// <param name="content">The set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public UtilitySetModel(UtilitySetContent content) {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
    }

    /// <summary>The set.</summary>
    public UtilitySetContent Content { get; private set; }

    /// <summary>How many actions it holds.</summary>
    public int Count => Content.Actions.Count;

    /// <summary>Raised after anything changes.</summary>
    public event Action<UtilitySetModel>? Changed;

    /// <summary>Adds an action.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="task">Which task it runs.</param>
    /// <returns>The action.</returns>
    public UtilityActionContent AddAction(string name, string task = "Wait") {
        var action = new UtilityActionContent { Name = name, Task = task };

        Content.Actions.Add(action);
        Raise();

        return action;
    }

    /// <summary>Removes an action.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveAction(UtilityActionContent action) {
        if (!Content.Actions.Remove(action)) {
            return false;
        }

        Raise();

        return true;
    }

    /// <summary>Moves an action up or down the list.</summary>
    /// <param name="action">The action.</param>
    /// <param name="offset">How far, and which way.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>Display only. ⚠ It is also the tie-break, so two actions that score identically swap.</remarks>
    public bool Move(UtilityActionContent action, int offset) {
        var index = Content.Actions.IndexOf(action);
        var target = index + offset;

        if (index < 0 || target < 0 || target >= Content.Actions.Count) {
            return false;
        }

        Content.Actions.RemoveAt(index);
        Content.Actions.Insert(target, action);
        Raise();

        return true;
    }

    /// <summary>Adds a consideration to an action.</summary>
    /// <param name="action">The action.</param>
    /// <param name="name">What to call it.</param>
    /// <returns>The consideration, or null if the action is not in this set.</returns>
    /// <remarks>
    ///     ⚠ It arrives reading nothing, which under the zero rule vetoes its action until somebody
    ///     picks a key. That is the right way round: a half-added axis makes an agent do nothing rather
    ///     than do the wrong thing enthusiastically, and the compiler says which one it was.
    /// </remarks>
    public UtilityConsiderationContent? AddConsideration(UtilityActionContent action, string name = "axis") {
        ArgumentNullException.ThrowIfNull(action);

        if (!Content.Actions.Contains(action)) {
            return null;
        }

        var consideration = new UtilityConsiderationContent { Name = name };

        action.Considerations.Add(consideration);
        Raise();

        return consideration;
    }

    /// <summary>Removes a consideration.</summary>
    /// <param name="action">The action it belongs to.</param>
    /// <param name="consideration">The consideration.</param>
    /// <returns>Whether it was there.</returns>
    public bool RemoveConsideration(UtilityActionContent action, UtilityConsiderationContent consideration) {
        ArgumentNullException.ThrowIfNull(action);

        if (!action.Considerations.Remove(consideration)) {
            return false;
        }

        Raise();

        return true;
    }

    /// <summary>Changes the curve on a consideration.</summary>
    /// <param name="consideration">The consideration.</param>
    /// <param name="kind">Which shape.</param>
    /// <remarks>
    ///     ⚠ Switching to <see cref="ResponseCurveKind.Sampled" /> seeds the keys from the curve that
    ///     was there, so "this is nearly right, let me bend it" is one click rather than starting from
    ///     a straight line.
    /// </remarks>
    public void SetCurve(UtilityConsiderationContent consideration, ResponseCurveKind kind) {
        ArgumentNullException.ThrowIfNull(consideration);

        if (kind == ResponseCurveKind.Sampled && consideration.Keys.Count == 0) {
            var previous = consideration.BuildCurve();

            for (var step = 0; step <= 8; step++) {
                var time = step / 8f;

                consideration.Keys.Add(
                    new() { Time = time, Value = previous.Evaluate(time), Mode = TangentMode.Auto }
                );
            }
        }

        consideration.Curve = kind;
        Raise();
    }

    /// <summary>Changes a curve's four parameters.</summary>
    /// <param name="consideration">The consideration.</param>
    /// <param name="slope">Slope, or the bell's height.</param>
    /// <param name="exponent">Exponent, steepness or width.</param>
    /// <param name="shift">Vertical shift.</param>
    /// <param name="centre">Horizontal shift.</param>
    public void SetShape(
        UtilityConsiderationContent consideration,
        float? slope = null,
        float? exponent = null,
        float? shift = null,
        float? centre = null
    ) {
        ArgumentNullException.ThrowIfNull(consideration);

        consideration.Slope = slope ?? consideration.Slope;
        consideration.Exponent = exponent ?? consideration.Exponent;
        consideration.Shift = shift ?? consideration.Shift;
        consideration.Centre = centre ?? consideration.Centre;
        Raise();
    }

    /// <summary>Points a consideration at a key.</summary>
    /// <param name="consideration">The consideration.</param>
    /// <param name="kind">Where its number comes from.</param>
    /// <param name="key">The key it reads, for the two kinds that read one.</param>
    /// <param name="source">The registered input's name, for the third.</param>
    public void SetInput(
        UtilityConsiderationContent consideration,
        UtilityInputKind kind,
        string? key = null,
        string? source = null
    ) {
        ArgumentNullException.ThrowIfNull(consideration);

        consideration.Input = kind;
        consideration.Key = key ?? consideration.Key;
        consideration.Source = source ?? consideration.Source;
        Raise();
    }

    /// <summary>Adds a blackboard key to the set's own list.</summary>
    /// <param name="name">What to call it.</param>
    /// <param name="type">What it holds.</param>
    /// <returns>The key, or null if the name is taken or empty.</returns>
    public BehaviorKeyContent? AddKey(string name, BlackboardValueType type) {
        if (string.IsNullOrWhiteSpace(name)
            || Content.Keys.Any(key => string.Equals(key.Name, name, StringComparison.Ordinal))) {
            return null;
        }

        var added = new BehaviorKeyContent { Name = name, Type = type };

        Content.Keys.Add(added);
        Raise();

        return added;
    }

    /// <summary>Renames a key, and every consideration that reads it.</summary>
    /// <param name="key">The key.</param>
    /// <param name="name">Its new name.</param>
    /// <returns>How many references were rewritten, or <c>-1</c> if the name is taken.</returns>
    /// <remarks>
    ///     ⚠ The rewrite is the whole point, exactly as it is for a tree: a file references a key by
    ///     name, so a rename that only changed the declaration would leave every consideration reading
    ///     a key that is gone — which under the zero rule is an action that silently never runs.
    /// </remarks>
    public int RenameKey(BehaviorKeyContent key, string name) {
        ArgumentNullException.ThrowIfNull(key);

        if (string.IsNullOrWhiteSpace(name)
            || Content.Keys.Any(other => other != key && string.Equals(other.Name, name, StringComparison.Ordinal))) {
            return -1;
        }

        var old = key.Name;
        var rewritten = 0;

        key.Name = name;

        foreach (var consideration in Content.Actions.SelectMany(action => action.Considerations)) {
            if (string.Equals(consideration.Key, old, StringComparison.Ordinal)) {
                consideration.Key = name;
                rewritten++;
            }
        }

        Raise();

        return rewritten;
    }

    /// <summary>Removes a key, leaving what read it dangling.</summary>
    /// <param name="key">The key.</param>
    /// <returns>How many considerations still name it.</returns>
    public int RemoveKey(BehaviorKeyContent key) {
        ArgumentNullException.ThrowIfNull(key);

        if (!Content.Keys.Remove(key)) {
            return 0;
        }

        var dangling = Content.Actions
            .SelectMany(action => action.Considerations)
            .Count(consideration => string.Equals(consideration.Key, key.Name, StringComparison.Ordinal));

        Raise();

        return dangling;
    }

    /// <summary>What one action would score, and what each of its considerations contributed.</summary>
    /// <param name="action">The action.</param>
    /// <param name="reading">What each consideration's input reads, by name. Missing reads as zero.</param>
    /// <returns>The action's score and its per-consideration scores.</returns>
    /// <remarks>
    ///     ⚠ <b>The editor scores from a table of readings rather than from a running agent.</b>
    ///     "Why is this scoring 0.2" is the question the tool exists to answer, and it has to be
    ///     answerable while the game is not running — so an author types the inputs and the panel does
    ///     the arithmetic the runtime would.
    /// </remarks>
    public static (float Score, float[] Detail) Preview(
        UtilityActionContent action,
        IReadOnlyDictionary<string, float> reading
    ) {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(reading);

        var detail = new float[action.Considerations.Count];

        for (var index = 0; index < detail.Length; index++) {
            var consideration = action.Considerations[index];
            var input = reading.TryGetValue(Reads(consideration), out var value) ? value : 0f;

            detail[index] = consideration.BuildCurve().Evaluate(input);
        }

        return (UtilityScoring.Combine(detail, action.Weight), detail);
    }

    /// <summary>What a consideration reads, as a name a preview table can be keyed on.</summary>
    /// <param name="consideration">The consideration.</param>
    /// <returns>Its key, or its registered input's name.</returns>
    public static string Reads(UtilityConsiderationContent consideration) {
        ArgumentNullException.ThrowIfNull(consideration);

        return consideration.Input == UtilityInputKind.Registered ? consideration.Source : consideration.Key;
    }

    /// <summary>A deep copy, for an undo entry.</summary>
    /// <returns>The copy.</returns>
    public UtilitySetContent Snapshot() => Copy(Content);

    /// <summary>Puts a whole set back, which is what an undo does.</summary>
    /// <param name="content">The set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public void Replace(UtilitySetContent content) {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        Raise();
    }

    /// <summary>A deep copy of a set.</summary>
    /// <param name="content">The set.</param>
    /// <returns>The copy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> is null.</exception>
    public static UtilitySetContent Copy(UtilitySetContent content) {
        ArgumentNullException.ThrowIfNull(content);

        return new() {
            Version = content.Version,
            Name = content.Name,
            Selector = content.Selector,
            SelectorCount = content.SelectorCount,
            CommitmentBonus = content.CommitmentBonus,
            DecisionInterval = content.DecisionInterval,
            Keys = [.. content.Keys.Select(key => new BehaviorKeyContent { Name = key.Name, Type = key.Type })],
            Actions = [.. content.Actions.Select(Copy)]
        };
    }

    static UtilityActionContent Copy(UtilityActionContent action) => new() {
        Name = action.Name,
        Task = action.Task,
        Weight = action.Weight,
        Cooldown = action.Cooldown,
        Bucket = action.Bucket,
        Fields = new(action.Fields, StringComparer.Ordinal),
        Considerations = [.. action.Considerations.Select(Copy)]
    };

    static UtilityConsiderationContent Copy(UtilityConsiderationContent consideration) => new() {
        Name = consideration.Name,
        Input = consideration.Input,
        Key = consideration.Key,
        Source = consideration.Source,
        Minimum = consideration.Minimum,
        Maximum = consideration.Maximum,
        Curve = consideration.Curve,
        Slope = consideration.Slope,
        Exponent = consideration.Exponent,
        Shift = consideration.Shift,
        Centre = consideration.Centre,
        Keys = [
            .. consideration.Keys.Select(
                key => new UtilityCurveKeyContent {
                    Time = key.Time,
                    Value = key.Value,
                    InTangent = key.InTangent,
                    OutTangent = key.OutTangent,
                    Mode = key.Mode
                }
            )
        ]
    };

    void Raise() => Changed?.Invoke(this);
}
