// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Turns the data in a <c>.vxutility</c> into a set the runtime scores.</summary>
/// <remarks>
///     <para>
///         The same shape <see cref="BehaviorTreeContentCompiler" /> has, and it shares that
///         compiler's back half outright: an action's task is looked up in the same
///         <see cref="BehaviorNodeSchema" />, built by the same factories and registered in the same
///         <see cref="AgentActionRegistry" />. Two files that both say <c>Wait(2)</c> get one action
///         between them whether one of them is a tree and the other a set.
///     </para>
///     <para>
///         ⚠ <b>Everything it cannot resolve is a diagnostic and a placeholder, never a refusal.</b>
///         Tuning a set before its inputs exist is the ordinary order of work, and a compiler that
///         refused would make the file unopenable until every name resolved. A consideration whose
///         input is missing scores <b>zero</b>, which under the zero rule vetoes its action — so an
///         unfinished set is an agent that does nothing rather than an agent that does the wrong
///         thing.
///     </para>
/// </remarks>
public static class UtilitySetContentCompiler {
    /// <summary>Builds a set from a file.</summary>
    /// <param name="content">The file.</param>
    /// <param name="resolver">Where names are looked up, and where actions are registered.</param>
    /// <param name="diagnostics">Everything that could not be resolved.</param>
    /// <param name="set">The set.</param>
    /// <param name="layout">The blackboard to resolve keys against, or null for the file's own.</param>
    /// <returns>Whether it compiled with nothing wrong.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> or <paramref name="resolver" /> is null.</exception>
    public static bool TryCompile(
        UtilitySetContent content,
        BehaviorTreeResolver resolver,
        out IReadOnlyList<BehaviorTreeDiagnostic> diagnostics,
        out UtilitySet? set,
        BlackboardLayout? layout = null
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolver);

        var problems = new List<BehaviorTreeDiagnostic>();
        var board = layout ?? content.BuildLayout(problems);
        var actions = new List<UtilityAction>(content.Actions.Count);

        foreach (var authored in content.Actions) {
            actions.Add(Action(authored, resolver, board, problems));
        }

        set = new UtilitySet(Symbol.Intern(content.Name), [.. actions]) {
            Selector = content.BuildSelector(),
            CommitmentBonus = content.CommitmentBonus,
            DecisionInterval = content.DecisionInterval
        };

        diagnostics = problems;

        return problems.Count == 0;
    }

    static UtilityAction Action(
        UtilityActionContent content,
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        List<BehaviorTreeDiagnostic> problems
    ) {
        BehaviorTreeContentCompiler.TryResolveTask(resolver, layout, content.Task, content.Fields, problems, out var task);

        var considerations = new UtilityConsideration[content.Considerations.Count];

        for (var index = 0; index < considerations.Length; index++) {
            considerations[index] = Consideration(content.Considerations[index], resolver, layout, problems);
        }

        return new UtilityAction(Symbol.Intern(content.Name), task, considerations) {
            Weight = content.Weight,
            Cooldown = content.Cooldown,
            Bucket = content.Bucket
        };
    }

    static UtilityConsideration Consideration(
        UtilityConsiderationContent content,
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        List<BehaviorTreeDiagnostic> problems
    ) =>
        new(Symbol.Intern(content.Name), Input(content, resolver, layout, problems), content.BuildCurve());

    static IUtilityInput Input(
        UtilityConsiderationContent content,
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        List<BehaviorTreeDiagnostic> problems
    ) {
        var where = Symbol.Intern(content.Name);

        switch (content.Input) {
            case UtilityInputKind.Registered:
                if (resolver.TryGetInput(content.Source, out var registered) && registered is not null) {
                    return registered;
                }

                problems.Add(new(where, $"No utility input called '{content.Source}' is registered."));

                // ⚠ Zero rather than one. A consideration nobody has wired up vetoes its action, so an
                // unfinished set is an agent that does nothing rather than one that does the wrong
                // thing enthusiastically.
                return UtilityInputs.Constant(0f);

            case UtilityInputKind.Distance:
                return new DistanceUtilityInput(Key(content, layout, problems), content.Maximum);

            default:
                return new BlackboardUtilityInput(Key(content, layout, problems), content.Minimum, content.Maximum);
        }
    }

    static BlackboardKey Key(
        UtilityConsiderationContent content,
        BlackboardLayout layout,
        List<BehaviorTreeDiagnostic> problems
    ) {
        var where = Symbol.Intern(content.Name);

        if (content.Key.Length == 0) {
            problems.Add(new(where, $"'{content.Name}' needs a key to read."));

            return BlackboardKey.Invalid;
        }

        if (layout.TryGetKey(Symbol.Intern(content.Key), out var key)) {
            return key;
        }

        problems.Add(new(where, $"'{content.Key}' is not a key on this agent's blackboard."));

        return BlackboardKey.Invalid;
    }
}
