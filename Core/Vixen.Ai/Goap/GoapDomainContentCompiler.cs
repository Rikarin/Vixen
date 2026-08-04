// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai;

/// <summary>Turns the tables in a <c>.vxgoap</c> into a domain, and builds its graph.</summary>
/// <remarks>
///     <para>
///         The third of the three content compilers, and it shares the other two's back half: an
///         action's task is looked up in the same <see cref="BehaviorNodeSchema" />, built by the same
///         factories and registered in the same <see cref="AgentActionRegistry" />.
///     </para>
///     <para>
///         ⚠ <b>A condition on a key nobody declared is the failure a designer cannot see</b>, because
///         it never holds — so the action it gates never runs and the goal it belongs to is never met,
///         with nothing in the game to look at. It is a diagnostic here, and the importer fails the
///         build on it.
///     </para>
/// </remarks>
public static class GoapDomainContentCompiler {
    /// <summary>Builds a domain from a file.</summary>
    /// <param name="content">The file.</param>
    /// <param name="resolver">Where names are looked up, and where actions are registered.</param>
    /// <param name="diagnostics">Everything that could not be resolved.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="layout">The blackboard to resolve keys against, or null for the file's own.</param>
    /// <returns>Whether it compiled with nothing wrong.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content" /> or <paramref name="resolver" /> is null.</exception>
    public static bool TryCompile(
        GoapDomainContent content,
        BehaviorTreeResolver resolver,
        out IReadOnlyList<BehaviorTreeDiagnostic> diagnostics,
        out GoapDomain? domain,
        BlackboardLayout? layout = null
    ) {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolver);

        var problems = new List<BehaviorTreeDiagnostic>();
        var board = layout ?? content.BuildLayout(problems);
        var names = new Dictionary<string, GoapWorldKey>(StringComparer.Ordinal);
        var definitions = new GoapKeyDefinition[content.Keys.Count];

        for (var index = 0; index < content.Keys.Count; index++) {
            var key = content.Keys[index];

            names[key.Name] = new((ushort)index);
            definitions[index] = new(Symbol.Intern(key.Name), Source(key, resolver, board, problems));
        }

        var keys = new GoapWorldKeys(definitions);
        var actions = new GoapAction[content.Actions.Count];

        for (var index = 0; index < actions.Length; index++) {
            actions[index] = Action(content.Actions[index], resolver, board, names, problems);
        }

        var goals = new GoapGoal[content.Goals.Count];

        for (var index = 0; index < goals.Length; index++) {
            var goal = content.Goals[index];

            goals[index] = new(
                Symbol.Intern(goal.Name),
                [.. goal.Conditions.Select(condition => Condition(condition, names, problems))],
                goal.Priority
            );
        }

        domain = new(Symbol.Intern(content.Name), keys, actions, goals);
        diagnostics = problems;

        return problems.Count == 0;
    }

    static GoapAction Action(
        GoapActionContent content,
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        Dictionary<string, GoapWorldKey> names,
        List<BehaviorTreeDiagnostic> problems
    ) {
        BehaviorTreeContentCompiler.TryResolveTask(resolver, layout, content.Task, content.Fields, problems, out var task);

        return new(
            Symbol.Intern(content.Name),
            task,
            [.. content.Conditions.Select(condition => Condition(condition, names, problems))],
            [.. content.Effects.Select(effect => Effect(effect, names, problems))]
        ) {
            BaseCost = content.Cost,
            Target = content.Target.Length > 0 ? Symbol.Intern(content.Target) : Symbol.None,
            StoppingDistance = content.StoppingDistance,
            Move = content.Move
        };
    }

    static GoapCondition Condition(
        GoapConditionContent content,
        Dictionary<string, GoapWorldKey> names,
        List<BehaviorTreeDiagnostic> problems
    ) =>
        new(Key(content.Key, names, problems), content.Comparison, content.Value);

    static GoapEffect Effect(
        GoapEffectContent content,
        Dictionary<string, GoapWorldKey> names,
        List<BehaviorTreeDiagnostic> problems
    ) =>
        new(Key(content.Key, names, problems), content.Increases);

    static GoapWorldKey Key(string name, Dictionary<string, GoapWorldKey> names, List<BehaviorTreeDiagnostic> problems) {
        if (name.Length == 0) {
            problems.Add(new(Symbol.None, "A condition or effect needs a world key."));

            return GoapWorldKey.Invalid;
        }

        if (names.TryGetValue(name, out var key)) {
            return key;
        }

        problems.Add(new(Symbol.Intern(name), $"'{name}' is not a world key on this domain."));

        return GoapWorldKey.Invalid;
    }

    static IGoapWorldSource Source(
        GoapKeyContent content,
        BehaviorTreeResolver resolver,
        BlackboardLayout layout,
        List<BehaviorTreeDiagnostic> problems
    ) {
        var where = Symbol.Intern(content.Name);

        switch (content.Source) {
            case GoapSourceKind.Constant:
                return GoapWorldSources.Constant(content.Value);

            case GoapSourceKind.Registered:
                if (resolver.TryGetWorldSource(content.From, out var registered) && registered is not null) {
                    return registered;
                }

                problems.Add(new(where, $"No world source called '{content.From}' is registered."));

                // ⚠ Zero rather than a guess. A key nobody wired up reads as its lowest value, which
                // makes the conditions that want it *higher* unsatisfiable — so an unfinished domain
                // is an agent that plans nothing rather than one that plans confidently from a lie.
                return GoapWorldSources.Constant(0);

            default:
                if (layout.TryGetKey(Symbol.Intern(content.From), out var key)) {
                    return GoapWorldSources.Blackboard(key);
                }

                problems.Add(new(where, $"'{content.From}' is not a key on this agent's blackboard."));

                return GoapWorldSources.Constant(0);
        }
    }
}
