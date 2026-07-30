// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Engine.Tests;

/// <summary>A component of the shape a game declares: attached to an entity, and saved with a scene.</summary>
/// <remarks>
///     ⚠ <b>Nothing anywhere registers this.</b> That is the whole assertion <c>ComponentRegistrationTests</c>
///     makes — the two attributes are the declaration, and this type exists to prove a game needs no
///     entry point, no module initializer of its own and no editor change.
/// </remarks>
[Component]
[DataContract("RegistrationTestShield")]
public struct Shield {
    /// <summary>How much it absorbs.</summary>
    public float Absorption;
}

/// <summary>A tag of the same shape, which has a bit in the archetype and no bytes anywhere.</summary>
[Component]
[DataContract("RegistrationTestWarded")]
public struct Warded : ITagComponent;

/// <summary>A handle a bridge writes, of the shape <c>PhysicsBody</c> has.</summary>
/// <remarks>
///     <c>[Component]</c> without <c>[DataContract]</c>, which is what a runtime handle looks like and
///     what has to stay out of a scene: the value means nothing outside the process that made it.
/// </remarks>
[Component]
public struct RegistrationTestHandle {
    /// <summary>The handle.</summary>
    public int Slot;
}

/// <summary>Data that is described but is not a component, of the shape an asset's settings have.</summary>
[DataContract("RegistrationTestSettings")]
public struct RegistrationTestSettings {
    /// <summary>Anything.</summary>
    public Vector3 Extent;
}
