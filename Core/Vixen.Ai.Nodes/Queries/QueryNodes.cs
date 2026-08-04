// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ai.Nodes;

/// <summary>The two nodes that run an environment query, and how a file builds them.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>RunQuery</c>, which that table lists twice — once as a task and once
///         as a service — because both forms are wanted and they answer different questions. The task
///         asks now and the branch waits for the answer; the service keeps a key pointed at the best
///         spot for as long as the branch it hangs on is running.
///     </para>
///     <para>
///         ⚠ <b>Two type names, because a schema entry has one slot.</b> <c>RunQuery</c> is the task
///         and <c>KeepQueryResult</c> is the service; calling them both <c>RunQuery</c> would mean the
///         search popup could not tell an author which one they were about to drop, and a file could
///         not say which one it meant.
///     </para>
///     <para>
///         ⚠ <b>A query is named rather than described, and the library is passed in.</b> A compiled
///         query holds live generator and test objects — a <c>PhysicsWorld</c>, a <c>NavMeshQuery</c> —
///         and none of those is a string in a file, which is the same bargain <c>PlaySound</c> and
///         <c>DoesPathExist</c> already make.
///     </para>
/// </remarks>
public static class QueryNodes {
    /// <summary>The declarations, for a caller adding them to a schema of its own.</summary>
    /// <returns>The node types.</returns>
    public static IEnumerable<BehaviorNodeType> Describe() {
        var query = new BehaviorField(
            "Query",
            "Query",
            BehaviorFieldKind.Text,
            "Which environment query to run, by name."
        );

        var context = new BehaviorField(
            "Context",
            "Context key",
            BehaviorFieldKind.Key,
            "The key naming what the query is about — the target it scores distance and sight against. Leave empty for a query that only cares where the agent is."
        );

        var result = new BehaviorField(
            "Result",
            "Result key",
            BehaviorFieldKind.Key,
            "The Vector3 key the best point is written to."
        );

        var resultEntity = new BehaviorField(
            "ResultEntity",
            "Result entity key",
            BehaviorFieldKind.Key,
            "The Entity key the best point's entity is written to, for a query over entities. Leave empty otherwise."
        );

        yield return new(
            "RunQuery",
            "Run query",
            "Queries",
            BehaviorSlot.Task,
            "Runs an environment query once and writes the best point to a key. Fails when nothing survived.",
            [query, context, result, resultEntity]
        );

        yield return new(
            "KeepQueryResult",
            "Keep query result",
            "Queries",
            BehaviorSlot.Service,
            "Re-runs an environment query on this branch's schedule and keeps a key on the best point. Clears the key when nothing survived.",
            [query, context, result, resultEntity]
        );
    }

    /// <summary>Adds them to a schema.</summary>
    /// <param name="schema">The schema, or null for the shared one.</param>
    /// <returns>The schema.</returns>
    /// <remarks>Safe to call twice: a type already in the schema is left alone.</remarks>
    public static BehaviorNodeSchema Register(BehaviorNodeSchema? schema = null) {
        var target = schema ?? BehaviorNodeSchema.Default;

        foreach (var type in Describe()) {
            if (!target.TryGet(type.Type, out _)) {
                target.Add(type);
            }
        }

        return target;
    }

    /// <summary>Teaches a resolver to build them.</summary>
    /// <param name="resolver">The resolver a <c>.vxbt</c> is compiled against.</param>
    /// <param name="queries">The compiled queries a file may name.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static BehaviorTreeResolver Register(BehaviorTreeResolver resolver, EnvironmentQueryLibrary queries) {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(queries);

        Register(resolver.Schema);

        resolver.AddTask(
            "RunQuery",
            (in BehaviorBuildContext context) => new BehaviorTaskBuild(
                new RunQueryTask(Bind(in context, queries)),
                RunQueryTask.StateSize
            )
        );

        resolver.AddService(
            "KeepQueryResult",
            (in BehaviorBuildContext context) => new RunQueryService(Bind(in context, queries))
        );

        return resolver;
    }

    /// <summary>Resolves the four fields both nodes take.</summary>
    /// <remarks>
    ///     ⚠ <b>A missing query is a diagnostic and a binding with none in it</b>, not a refusal. The
    ///     node then fails every time it runs, which is exactly what a tree that names a query nobody
    ///     has written yet should do — and the branch beside it still compiles and still works.
    /// </remarks>
    static QueryBinding Bind(ref readonly BehaviorBuildContext context, EnvironmentQueryLibrary queries) {
        var name = context.Text("Query");
        var index = queries.IndexOf(Symbol.Intern(name));

        if (index < 0) {
            context.Report($"No environment query called '{name}' is registered.");
        }

        return new(
            index >= 0 ? queries[index] : null!,
            context.Text("Context").Length > 0 ? context.Key("Context") : null,
            context.Text("Result").Length > 0 ? context.Key("Result") : null,
            context.Text("ResultEntity").Length > 0 ? context.Key("ResultEntity") : null
        );
    }
}
