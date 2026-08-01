// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Behaviors;

namespace Vixen.Engine.Tests;

/// <summary>A behaviour a scene may name, which is what carrying <c>[DataContract]</c> declares.</summary>
/// <remarks>
///     Its fields are what an inspector would draw and what a file would carry — which is the whole
///     of what the registry asks of a behaviour, and is why there is no attribute of its own.
/// </remarks>
[DataContract("RegistrationTestPatrol")]
public sealed class Patrol : Behavior {
    /// <summary>How fast it walks.</summary>
    public float Speed { get; set; } = 3f;

    /// <summary>How far it goes before turning round.</summary>
    public float Distance { get; set; } = 10f;
}

/// <summary>A behaviour with no contract, which is code rather than content.</summary>
/// <remarks>
///     The exclusion that makes <c>[DataContract]</c> the declaration rather than deriving from
///     <see cref="Behavior" />: a behaviour attached in code and never authored is the common case
///     and must not appear in an Add Component menu.
/// </remarks>
public sealed class RegistrationTestCodeOnly : Behavior;

/// <summary>A described base whose concrete subclasses are the authorable ones.</summary>
/// <remarks>
///     Abstract, so the generator passes over it in silence — a scene was never going to name it,
///     and describing it is how its members reach the subclasses below.
/// </remarks>
[DataContract("RegistrationTestWeapon")]
public abstract class RegistrationTestWeapon : Behavior {
    /// <summary>How much it hurts.</summary>
    public int Damage { get; set; }
}

/// <summary>The concrete one, which is what a scene names.</summary>
[DataContract("RegistrationTestSword")]
public sealed class RegistrationTestSword : RegistrationTestWeapon {
    /// <summary>How long the blade is.</summary>
    public float Reach { get; set; } = 1.5f;
}
