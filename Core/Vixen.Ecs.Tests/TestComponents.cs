// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ecs.Tests;

/// <summary>
///     The component types the suite uses. One file so that a test reading a chunk layout can see
///     every size that goes into it.
/// </summary>
/// <remarks>
///     Deliberately several shapes: a small struct, a large one, a one-byte one, a tag, a class and
///     a struct that merely contains a reference. Those last two take different paths through the
///     storage layer and the third one — a struct with a reference in it — is the one that gets
///     forgotten.
/// </remarks>
public struct Position {
    public float X;
    public float Y;
    public float Z;

    public Position(float x, float y, float z) {
        X = x;
        Y = y;
        Z = z;
    }
}

public struct Velocity {
    public float X;
    public float Y;
    public float Z;

    public Velocity(float x, float y, float z) {
        X = x;
        Y = y;
        Z = z;
    }
}

public struct Health {
    public int Value;

    public Health(int value) => Value = value;
}

public struct Flags {
    public byte Value;

    public Flags(byte value) => Value = value;
}

/// <summary>Big enough that a chunk holds few of them, which is what exercises multi-chunk paths.</summary>
public struct Bulky {
    public long A;
    public long B;
    public long C;
    public long D;
    public long E;
    public long F;
    public long G;
    public long H;
}

public struct Frozen : ITagComponent;

public struct Player : ITagComponent;

public struct Npc : ITagComponent;

/// <summary>A managed component that is a class.</summary>
public sealed class Label {
    public string Text { get; init; } = "";
}

/// <summary>A managed component that is a struct containing a reference — the easy one to miss.</summary>
public struct Named {
    public string Name;

    public Named(string name) => Name = name;
}
