// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Behaviors;

/// <summary>How many instances of one behaviour type a <see cref="BehaviorStore" /> is holding.</summary>
/// <param name="BehaviorType">
///     The concrete type the bucket is keyed by — the static type at the <c>Add&lt;T&gt;</c> call
///     site, which is not always the runtime type. See <c>Behavior.BucketKey</c>.
/// </param>
/// <param name="Total">How many there are, enabled or not.</param>
/// <param name="Enabled">
///     How many are in the bucket's enabled prefix, which is what the update loop walks. ⚠ That is a
///     property of the *loop* rather than of the authoring: a behaviour attached since the last
///     lifecycle drain has not been activated yet and counts as disabled here while
///     <see cref="Behavior.Enabled" /> on it says otherwise.
/// </param>
/// <remarks>
///     Deliberately not carrying the <c>[DataContract]</c> alias, though a report wants one: a
///     behaviour with no alias is not registered at all and there would be nothing to put in the
///     field, and asking <c>SceneBehaviorRegistry</c> per row would make a count depend on a registry
///     that has nothing to do with counting. Whoever prints this resolves the name it wants to show.
/// </remarks>
public readonly record struct BehaviorPopulation(Type BehaviorType, int Total, int Enabled);
