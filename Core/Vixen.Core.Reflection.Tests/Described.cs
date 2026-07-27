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

/// <summary>
///     The shape [08](../../docs/plan/08-asset-pipeline-and-addressables.md) uses for every
///     importer's settings: an immutable record with defaults, which a deserializer has to be able to
///     fill in without an object initializer to write it in.
/// </summary>
[DataContract("TextureImportSettings")]
public sealed record ImportSettings {
    public int MaxSize { get; init; } = 2048;

    public string Compression { get; init; } = "Bc7";

    /// <summary>Mixed with the init-only ones, because a real settings record is.</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>Genuinely unwritable, so <c>init</c> support must not make everything settable.</summary>
    public bool IsHighResolution => MaxSize > 1024;
}

/// <summary>An init-only member on a struct, where the write has to land in the box.</summary>
[DataContract]
public struct Extent {
    public int Width { get; init; }
    public int Height { get; init; }
}
