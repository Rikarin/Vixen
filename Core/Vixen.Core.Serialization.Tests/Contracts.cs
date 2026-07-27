// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Core.Serialization.Tests;

/// <summary>The shapes the generator has to handle, one per thing that could go wrong.</summary>
[DataContract]
public struct MutableStruct {
    public int Number;
    public string? Text;
    public bool Flag;
}

/// <summary>Positional record: every member is get-only, so it can only be built by its constructor.</summary>
[DataContract]
public readonly record struct PositionalStruct(int X, float Y, string Name);

/// <summary>A class with settable properties, the shape most engine data takes.</summary>
[DataContract]
public sealed class SettableClass {
    public int Id { get; set; }
    public string? Name { get; set; }
    public double Weight { get; set; }
}

/// <summary>Ignored and reordered members.</summary>
[DataContract]
public sealed class AnnotatedClass {
    [DataMember(2)] public int Third { get; set; }

    [DataMember(0)] public int First { get; set; }

    [DataMember(1)] public int Second { get; set; }

    [DataMemberIgnore] public int Cache { get; set; }
}

public enum Facing : byte {
    North,
    East,
    South,
    West
}

[DataContract]
public sealed class CollectionsClass {
    public int[]? Numbers { get; set; }
    public string?[]? Names { get; set; }
    public List<int>? Scores { get; set; }
    public Dictionary<string, int>? Counts { get; set; }
    public int? Optional { get; set; }
    public Facing Direction { get; set; }
}

[DataContract]
public sealed class NestedClass {
    public MutableStruct Inner { get; set; }
    public SettableClass? Child { get; set; }
    public PositionalStruct Positional { get; set; }
}

/// <summary>A base and a derived contract, to pin the inheritance and the polymorphism guard.</summary>
[DataContract]
public class BaseContract {
    public int BaseNumber { get; set; }
}

[DataContract]
public sealed class DerivedContract : BaseContract {
    public string? DerivedText { get; set; }
}

/// <summary>A base with two subtypes, plus a member declared as the base. The polymorphism case.</summary>
[DataContract]
public abstract class Shape {
    public string? Label { get; set; }
}

[DataContract]
public sealed class Circle : Shape {
    public float Radius { get; set; }
}

/// <summary>Renamed once. The alias is what existing data carries, so it has to keep working.</summary>
[DataContract("Rect")]
[DataAlias("Rectangle")]
public sealed class Box : Shape {
    public float Width { get; set; }
    public float Height { get; set; }
}

[DataContract]
public sealed class Drawing {
    public Shape? Root { get; set; }
    public Shape?[]? Children { get; set; }
}

/// <summary>Version 2, with no way to read version 1.</summary>
[DataContract(SerializedVersion = 2)]
public sealed class VersionedClass {
    public int Value { get; set; }
}

/// <summary>Version 2, with a migration from version 1.</summary>
[DataContract(SerializedVersion = 2)]
public sealed class MigratedClass {
    public int Value { get; set; }

    /// <summary>Reads the version-1 layout, which held the value as a string.</summary>
    /// <param name="fromVersion">The version the data was written with.</param>
    /// <param name="reader">Where to read from, positioned just after the header.</param>
    /// <param name="value">What to fill.</param>
    /// <returns><see langword="false" /> if the version is not one this knows.</returns>
    public static bool TryMigrate(int fromVersion, ref SerializationReader reader, ref MigratedClass value) {
        if (fromVersion != 1) {
            return false;
        }

        value ??= new();
        value.Value = int.Parse(reader.ReadString()!, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }
}

/// <summary>
///     A computed property alongside real state, and a constructor that is not parameterless. Both
///     were generator bugs: the computed property was treated as data that had to round-trip, and the
///     missing default constructor was emitted as <c>new()</c> anyway.
/// </summary>
[DataContract]
public sealed class Reading {
    public Reading(int raw) => Raw = raw;

    public int Raw { get; set; }

    public double Scaled => Raw / 100d;
}

[DataContract]
public sealed class Computed {
    public int Width { get; set; }
    public int Height { get; set; }
    public int Area => Width * Height;
}

/// <summary>
///     The shape [08](../../docs/plan/08-asset-pipeline-and-addressables.md) gives every importer's
///     settings, and the one <c>Vixen.Audio.AudioClip</c> was first written in: an immutable record
///     with defaults and no constructor to match. Until the generator bound to the <c>init</c>
///     setters, this had no settable member, so it was emitted with <c>MemberCount = 0</c> — it wrote
///     nothing, read every field back as its default, and said so nowhere.
/// </summary>
[DataContract]
public sealed record InitOnlySettings {
    public int MaxSize { get; init; } = 2048;
    public string Compression { get; init; } = "Bc7";
    public Facing Direction { get; init; }
    public int[] Sizes { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public SettableClass? Child { get; init; }

    /// <summary>Mixed in with the init-only ones, because a real settings record has both.</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>Genuinely unwritable, so <c>init</c> support must not make everything settable.</summary>
    public bool IsHighResolution => MaxSize > 1024;
}

/// <summary>An init-only member on a struct, where the write has to land in the caller's value.</summary>
[DataContract]
public struct InitOnlyExtent {
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
///     An inherited init-only member. The accessor has to name the type that declares the setter,
///     because it looks its target up on exactly that type rather than walking the base chain.
/// </summary>
[DataContract]
public class InitOnlyBase {
    public int BaseNumber { get; init; }
}

[DataContract]
public sealed class InitOnlyDerived : InitOnlyBase {
    public string? DerivedText { get; init; }
}

/// <summary>
///     An immutable value type: <c>readonly</c> fields, a constructor that takes them, and a derived
///     property alongside. Every type in <c>Vixen.Core.Mathematics</c> has this shape.
/// </summary>
/// <remarks>
///     It generated a serializer with <b>no members at all</b> — two varints out, every field back as
///     its default, and no diagnostic. Nothing had ever written one, so nothing had noticed. The
///     fallback dropped everything unassignable in a single step, which took the <c>readonly</c>
///     fields along with the derived property and left no members for any constructor to match. That
///     is a different failure from the <c>init</c> one above and outlived the fix for it.
/// </remarks>
[DataContract]
public readonly struct Extent {
    public readonly int Width;
    public readonly int Height;

    public Extent(int width, int height) {
        Width = width;
        Height = height;
    }

    public int Area => Width * Height;
}
