// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Net.Messaging;

namespace Vixen.Net.Replication;

/// <summary>Knows how to put one component type on the wire and take it off again.</summary>
/// <remarks>
///     <para>
///         One of these is emitted per <c>[Replicated]</c> component by
///         <c>Vixen.Net.Generators</c>. The interface is what the generated code implements, and it
///         is small on purpose: everything the replication loop needs to know about a component type
///         is here, and everything specific to a type is in the generated implementation rather than
///         in a reflection call at run time. That is what makes replication work under NativeAOT.
///     </para>
///     <para>
///         Hand-written implementations are legitimate — the tests use them, and a type with a layout
///         the generator cannot see is expected to. The generator emits what a careful person would
///         have written.
///     </para>
/// </remarks>
public interface IComponentReplicator {
    /// <summary>Which component this replicates.</summary>
    ComponentTypeId ComponentType { get; }

    /// <summary>
    ///     The id this type has on the wire: a hash of its full name, computed at build time.
    /// </summary>
    /// <remarks>
    ///     Hashed rather than counted, so that adding a component does not renumber the others and a
    ///     mismatch between two builds is detected rather than silently misrouted into the wrong
    ///     type.
    /// </remarks>
    uint TypeId { get; }

    /// <summary>The name, for diagnostics and for the bandwidth attribution panel.</summary>
    string TypeName { get; }

    /// <summary>How to send it.</summary>
    Channel Channel { get; }

    /// <summary>What to shed last. Higher goes first.</summary>
    int Priority { get; }

    /// <summary>A query matching entities with this component, filtered on it having changed.</summary>
    /// <remarks>
    ///     The cheap half of the work. The per-chunk change versions mean a component nobody wrote
    ///     this tick costs nothing to consider, which is the main structural reason replication is
    ///     built on this ECS rather than on one without them.
    /// </remarks>
    QueryDescription ChangedQuery { get; }

    /// <summary>Whether an entity has this component at all.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Whether it is there.</returns>
    bool Has(World world, Entity entity);

    /// <summary>Encodes an entity's component.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="writer">Where the bits go.</param>
    void Write(World world, Entity entity, ref BitWriter writer);

    /// <summary>Decodes a component and puts it on an entity, adding it if it is not there.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="reader">Where the bits come from.</param>
    /// <returns>Whether the bits were well-formed. A false leaves the entity as it was.</returns>
    bool Apply(World world, Entity entity, ref BitReader reader);

    /// <summary>
    ///     The fixed-width fields <see cref="Write" /> produces, in the order it produces them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Declaring this is how a component opts in to delta encoding, and it is all that is
    ///         needed: <see cref="DeltaCodec" /> works on bits, so the layout is the only thing it has
    ///         to be told. Empty — the default — means every record of this component is sent whole,
    ///         which is correct and is what a replicator with a variable-length encoding must do.
    ///     </para>
    ///     <para>
    ///         <b>Lanes that disagree with <see cref="Write" /> would be a desync nobody could see</b>,
    ///         so they are not taken on trust: the server compares their total against the length of
    ///         what <see cref="Write" /> actually produced and silently sends whole records if the two
    ///         differ. A generated replicator derives both from one field list and cannot disagree; a
    ///         hand-written one gets an assertion in the tests, and a wrong answer costs bandwidth
    ///         rather than correctness.
    ///     </para>
    /// </remarks>
    ReadOnlySpan<WireLane> Lanes => default;
}
