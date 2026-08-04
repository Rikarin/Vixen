// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Engine.Transforms;

namespace Vixen.Ai.Nodes;

/// <summary>Where a query run gets its origin, and where its answer goes.</summary>
/// <param name="Query">The query to run.</param>
/// <param name="Context">A key naming what the query is about, or invalid for none.</param>
/// <param name="Result">A <c>Vector3</c> key the best point is written to.</param>
/// <param name="ResultEntity">An <c>Entity</c> key the best point's entity is written to, or invalid.</param>
/// <remarks>
///     <para>
///         ⚠ <b>Two result keys, not one.</b> A query over entities answers with an entity and a query
///         over a grid answers with a position, and a node that wrote only the position would have
///         thrown away the whole point of the first — while one that wrote only the entity would have
///         nothing to say about the second. Both are written when both are known; the entity key is
///         cleared when the winning point came from no entity, because a stale entity is worse than
///         none.
///     </para>
///     <para>
///         ⚠ <b>The optional keys are <c>BlackboardKey?</c> and not <c>BlackboardKey</c>, because
///         <c>default(BlackboardKey)</c> is key <i>zero</i> and not an invalid one.</b> A binding that
///         left the entity key unset would silently name the first key on the board — which in this
///         node's case meant clearing the very key it had just written. Perception's bindings are
///         nullable for the same reason and it cost a failing test to find there too.
///     </para>
/// </remarks>
public readonly record struct QueryBinding(
    EnvironmentQuery Query,
    BlackboardKey? Context = null,
    BlackboardKey? Result = null,
    BlackboardKey? ResultEntity = null
);

/// <summary>Runs an environment query now, and writes the best point to a key.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>RunQuery</c> task. It is here rather than in <c>Vixen.Ai</c> because
///         it needs to know where the agent <i>is</i>, which is a <see cref="LocalTransform" /> and
///         therefore <c>Vixen.Engine</c>'s.
///     </para>
///     <para>
///         ⚠ <b>It finishes in one tick, and fails when nothing survived.</b> A query is a question
///         rather than a procedure, so a task that stayed <c>Running</c> while it thought would be a
///         task the tree has to be told when to stop waiting for. Failing on an empty result is what
///         lets a selector fall through to the branch that does not need an answer — which is the
///         shape every cover query wants: take cover, or if there is none, run.
///     </para>
/// </remarks>
/// <param name="binding">What to run, and where the answer goes.</param>
public sealed class RunQueryTask(QueryBinding binding) : IAgentAction {
    readonly QueryResults results = new();

    /// <summary>How many bytes it needs: the run it has already done, so a retry is not a re-query.</summary>
    public static int StateSize => Unsafe.SizeOf<int>();

    /// <summary>What the last run produced. What the editor's preview and a test read.</summary>
    public QueryResults Results => results;

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) => MemoryMarshal.AsRef<int>(state) = 0;

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) =>
        QueryRunner.Run(in context, in binding, results) ? ActionStatus.Succeeded : ActionStatus.Failed;

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>Runs an environment query on a schedule and keeps a key pointed at the best answer.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>RunQuery</c> service, and the form most trees actually want:
///         "wherever the best cover is, keep this key on it" running under the branch that uses it,
///         at an interval, with the deviation that stops every agent querying on the same frame.
///     </para>
///     <para>
///         ⚠ <b>A service that finds nothing clears the key rather than leaving the last answer
///         there.</b> A stale destination is the bug that makes an agent walk confidently to a spot
///         that stopped being cover two seconds ago, and it is invisible because the key still looks
///         perfectly reasonable. <c>DefaultFocusService</c> made the same decision in P4.
///     </para>
/// </remarks>
/// <param name="binding">What to run, and where the answer goes.</param>
public sealed class RunQueryService(QueryBinding binding) : BehaviorService {
    readonly QueryResults results = new();

    /// <summary>What the last run produced.</summary>
    public QueryResults Results => results;

    /// <inheritdoc />
    public override void Tick(in BehaviorContext context, Span<byte> state, float delta) {
        var agent = context.Agent;

        if (QueryRunner.Run(in agent, in binding, results)) {
            return;
        }

        if (binding.Result is { IsValid: true } result) {
            agent.Blackboard.Clear(result);
        }

        if (binding.ResultEntity is { IsValid: true } named) {
            agent.Blackboard.Clear(named);
        }
    }
}

/// <summary>The half a task and a service share: build the origin, run, write the keys.</summary>
static class QueryRunner {
    public static bool Run(in AgentContext context, in QueryBinding binding, QueryResults results) {
        if (binding.Query is null || !context.World.Has<LocalTransform>(context.Entity)) {
            return false;
        }

        var here = context.World.Read<LocalTransform>(context.Entity).Position;
        var origin = new QueryOrigin(here, Vector3.Zero);

        if (binding.Context is { IsValid: true } about
            && AgentTarget.TryResolve(in context, about, out var at, out var entity)) {
            origin = new(here, at, true, entity);
        }

        if (!binding.Query.Run(in context, in origin, results) || !results.TryBest(out var best)) {
            return false;
        }

        if (binding.Result is { IsValid: true } result) {
            context.Blackboard.SetVector3(result, best.Position);
        }

        if (binding.ResultEntity is not { IsValid: true } named) {
            return true;
        }

        if (best.Entity.IsNull) {
            context.Blackboard.Clear(named);
        } else {
            context.Blackboard.SetEntity(named, best.Entity);
        }

        return true;
    }
}
