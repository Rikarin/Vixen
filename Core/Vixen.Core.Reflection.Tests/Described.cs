// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Core.Reflection.Tests;

[DataContract]
[Category("Gameplay")]
public sealed class Health {
    [Category("Vitals")]
    [Tooltip("How much damage is left before death.")]
    [Range(0, 1000, Step = 5, Logarithmic = false)]
    public float Current { get; set; }

    public float Maximum { get; set; }

    [EditorVisible(false)]
    public float LastDamageTime { get; set; }

    [EditorVisible(DisplayName = "Regeneration", ReadOnly = true)]
    public float Regeneration { get; set; }

    /// <summary>Readable and not writable, so it is described but cannot be set.</summary>
    public float Fraction => Maximum == 0 ? 0 : Current / Maximum;
}

/// <summary>A component that is not a contract, to keep the two traits distinguishable.</summary>
[Component]
public sealed class Tag {
    public string? Name { get; set; }
}

/// <summary>Both, which is the ordinary case.</summary>
[DataContract]
[Component]
[EditorVisible]
public sealed class Transform2D {
    public float X { get; set; }
    public float Y { get; set; }
    public float Rotation { get; set; }
}

/// <summary>A struct component: setting a member has to reach the boxed instance, not a copy.</summary>
[DataContract]
[Component]
public struct Velocity {
    public float DeltaX { get; set; }
    public float DeltaY { get; set; }
}

/// <summary>Renamed once, so the registry has to answer to both names.</summary>
[DataContract("Sprite")]
[DataAlias("Billboard")]
public sealed class SpriteRenderer {
    public string? Texture { get; set; }
}

[DataContract]
public abstract class Behaviour {
    public bool Enabled { get; set; }
}

[DataContract]
public sealed class Spinner : Behaviour {
    public float Speed { get; set; }
}

/// <summary>No parameterless constructor, so it is described but not constructible.</summary>
[DataContract]
public sealed class Anchored {
    public Anchored(int target) => Target = target;

    public int Target { get; set; }
}
