// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Behaviors;

/// <summary>
///     Marks a behaviour type whose batch may have <c>Update</c> and <c>LateUpdate</c> dispatched
///     across the job system instead of walked on the calling thread.
/// </summary>
/// <remarks>
///     <para>
///         <b>Item 3 of [04](../../../docs/plan/04-ecs-and-scripting.md) § Making it fast.</b>
///         Bucketing bought the cache locality — a batch is a contiguous <c>T[]</c> and the loop is
///         monomorphic — and parallelism is a separate axis: ten thousand instances of one behaviour
///         type is exactly the shape the job system exists for, and without this they run on one
///         core.
///     </para>
///     <para>
///         ⚠ <b>What it promises is that <em>this type's</em> <c>Update</c> is safe to run on
///         several threads at once, and nothing checks that at runtime.</b> What checks it is
///         <c>VXS0417</c>, an error: inside a marked type's <c>Update</c> or <c>LateUpdate</c>, the
///         calls that reach unsynchronised store-wide or world-wide state — the lifecycle queues, the
///         coroutine scheduler, structural change, and <c>Get&lt;T&gt;</c> of a managed component,
///         which lazily allocates a slot in a table the whole world shares — are refused rather than
///         left to fail on a busy machine. The attribute and the dispatch landed in one change with
///         that analyzer for the reason <c>HotPathAttribute</c> records: an attribute naming an
///         enforcement nobody wrote reads from a call site exactly like one that is checked.
///     </para>
///     <para>
///         ⚠ <b>Not inherited, and the bucket is the reason.</b> A behaviour is bucketed under the
///         static type at its <c>Add&lt;T&gt;</c> call site, and it is that closed generic which
///         reads this. A subclass is a different bucket with a different <c>Update</c>, so it marks
///         itself or it does not run in parallel — inheriting the mark would parallelise a body its
///         author never offered.
///     </para>
///     <para>
///         ⚠ <b>An exception out of a dispatched batch arrives wrapped.</b> The serial loop lets a
///         behaviour's exception propagate as itself; the job system reports a batch that threw as a
///         <c>JobExecutionException</c> around it. Nothing here unwraps it, because the wrapper is
///         what says which of several threads the failure came off.
///     </para>
///     <para>
///         <b>What it does not buy.</b> A batch is dispatched at a sync point where no system is
///         running, so this parallelises <em>within</em> a bucket and not a bucket against a system.
///         Scheduling a behaviour's work against another system's needs an access declaration for
///         behaviour types, which does not exist — <c>[InferAccess]</c> reads queries, and a
///         behaviour reaches components through <c>Get&lt;T&gt;</c> and the transform façade instead.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [BehaviorJob]
///     public sealed class Bobbing : Behavior {
///         protected override void Update() {
///             var transform = Transform;
///             transform.Position += Vector3.UnitY * Time.Delta;
///         }
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class BehaviorJobAttribute : Attribute;
