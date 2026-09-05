// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Ecs.Tests;

using Spy = AritySpy<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>;

/// <summary>A component whose value a body can bump without knowing which component it is.</summary>
/// <remarks>
///     ⚠ Mechanical, and meant to be. Sixteen arities across four families is two hundred and
///     fifty-six generated methods whose only variable is a number, and
///     <see cref="QueryAritySurfaceTests" /> is what says this file still covers all of them: if
///     <c>QueryArityGenerator.MaxArity</c> moves, that test goes red and this file is extended by
///     hand to match.
/// </remarks>
public interface ISlot {
    int Value { get; set; }
}

/// <summary>Component 0 of the sixteen the arity drive uses.</summary>
public struct Slot0 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 1 of the sixteen the arity drive uses.</summary>
public struct Slot1 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 2 of the sixteen the arity drive uses.</summary>
public struct Slot2 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 3 of the sixteen the arity drive uses.</summary>
public struct Slot3 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 4 of the sixteen the arity drive uses.</summary>
public struct Slot4 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 5 of the sixteen the arity drive uses.</summary>
public struct Slot5 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 6 of the sixteen the arity drive uses.</summary>
public struct Slot6 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 7 of the sixteen the arity drive uses.</summary>
public struct Slot7 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 8 of the sixteen the arity drive uses.</summary>
public struct Slot8 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 9 of the sixteen the arity drive uses.</summary>
public struct Slot9 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 10 of the sixteen the arity drive uses.</summary>
public struct Slot10 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 11 of the sixteen the arity drive uses.</summary>
public struct Slot11 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 12 of the sixteen the arity drive uses.</summary>
public struct Slot12 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 13 of the sixteen the arity drive uses.</summary>
public struct Slot13 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 14 of the sixteen the arity drive uses.</summary>
public struct Slot14 : ISlot {
    public int Value { get; set; }
}

/// <summary>Component 15 of the sixteen the arity drive uses.</summary>
public struct Slot15 : ISlot {
    public int Value { get; set; }
}

/// <summary>
///     A visitor that implements every generated <c>IForEach</c> and <c>IForEachWithEntity</c>,
///     so one type can be handed to all four iteration families at all sixteen arities.
/// </summary>
/// <remarks>
///     <para>
///         Each body bumps every component it was handed by one, so what a run leaves behind is
///         arithmetic rather than a flag: a column delivered twice, or not at all, or in the wrong
///         position, shows up as a slot whose value is off by the number of times it was wrong.
///     </para>
///     <para>
///         ⚠ <b>The struct carries a reference, and that is the point.</b> Handing
///         <c>spy.Execute</c> to the delegate families boxes a copy, so a counter in a field would
///         be written to a copy nobody reads. <see cref="Seen" /> is shared by every copy.
///     </para>
/// </remarks>
public struct AritySpy<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> :
    IForEach<T0>,
    IForEach<T0, T1>,
    IForEach<T0, T1, T2>,
    IForEach<T0, T1, T2, T3>,
    IForEach<T0, T1, T2, T3, T4>,
    IForEach<T0, T1, T2, T3, T4, T5>,
    IForEach<T0, T1, T2, T3, T4, T5, T6>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>,
    IForEach<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>,
    IForEachWithEntity<T0>,
    IForEachWithEntity<T0, T1>,
    IForEachWithEntity<T0, T1, T2>,
    IForEachWithEntity<T0, T1, T2, T3>,
    IForEachWithEntity<T0, T1, T2, T3, T4>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14>,
    IForEachWithEntity<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15>
    where T0 : struct, ISlot
    where T1 : struct, ISlot
    where T2 : struct, ISlot
    where T3 : struct, ISlot
    where T4 : struct, ISlot
    where T5 : struct, ISlot
    where T6 : struct, ISlot
    where T7 : struct, ISlot
    where T8 : struct, ISlot
    where T9 : struct, ISlot
    where T10 : struct, ISlot
    where T11 : struct, ISlot
    where T12 : struct, ISlot
    where T13 : struct, ISlot
    where T14 : struct, ISlot
    where T15 : struct, ISlot
{
    /// <summary>Every entity the two entity-carrying families handed over.</summary>
    public List<Entity>? Seen { get; init; }

    public void Execute(ref T0 component0) {
        Bump(ref component0);
    }

    public void Execute(ref T0 component0, ref T1 component1) {
        Bump(ref component0);
        Bump(ref component1);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
        Bump(ref component10);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
        Bump(ref component10);
        Bump(ref component11);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
        Bump(ref component10);
        Bump(ref component11);
        Bump(ref component12);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12, ref T13 component13) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
        Bump(ref component10);
        Bump(ref component11);
        Bump(ref component12);
        Bump(ref component13);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12, ref T13 component13, ref T14 component14) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
        Bump(ref component10);
        Bump(ref component11);
        Bump(ref component12);
        Bump(ref component13);
        Bump(ref component14);
    }

    public void Execute(ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12, ref T13 component13, ref T14 component14, ref T15 component15) {
        Bump(ref component0);
        Bump(ref component1);
        Bump(ref component2);
        Bump(ref component3);
        Bump(ref component4);
        Bump(ref component5);
        Bump(ref component6);
        Bump(ref component7);
        Bump(ref component8);
        Bump(ref component9);
        Bump(ref component10);
        Bump(ref component11);
        Bump(ref component12);
        Bump(ref component13);
        Bump(ref component14);
        Bump(ref component15);
    }

    public void Execute(Entity entity, ref T0 component0) {
        Seen?.Add(entity);
        Execute(ref component0);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9, ref component10);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9, ref component10, ref component11);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9, ref component10, ref component11, ref component12);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12, ref T13 component13) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9, ref component10, ref component11, ref component12, ref component13);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12, ref T13 component13, ref T14 component14) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9, ref component10, ref component11, ref component12, ref component13, ref component14);
    }

    public void Execute(Entity entity, ref T0 component0, ref T1 component1, ref T2 component2, ref T3 component3, ref T4 component4, ref T5 component5, ref T6 component6, ref T7 component7, ref T8 component8, ref T9 component9, ref T10 component10, ref T11 component11, ref T12 component12, ref T13 component13, ref T14 component14, ref T15 component15) {
        Seen?.Add(entity);
        Execute(ref component0, ref component1, ref component2, ref component3, ref component4, ref component5, ref component6, ref component7, ref component8, ref component9, ref component10, ref component11, ref component12, ref component13, ref component14, ref component15);
    }

    static void Bump<T>(ref T slot) where T : struct, ISlot => slot.Value += 1;
}

/// <summary>Calls all four generated iteration families once at every arity.</summary>
public static class ArityDrive {
    /// <summary>The number of arities this file drives. Raised in step with the generator.</summary>
    public const int MaxArity = 16;

    /// <summary>The component types, in the order the drive uses them.</summary>
    public static IReadOnlyList<Type> Slots { get; } = [
        typeof(Slot0),
        typeof(Slot1),
        typeof(Slot2),
        typeof(Slot3),
        typeof(Slot4),
        typeof(Slot5),
        typeof(Slot6),
        typeof(Slot7),
        typeof(Slot8),
        typeof(Slot9),
        typeof(Slot10),
        typeof(Slot11),
        typeof(Slot12),
        typeof(Slot13),
        typeof(Slot14),
        typeof(Slot15)
    ];

    /// <summary>Runs every arity of every family over the world once.</summary>
    /// <param name="world">A world holding one entity with all sixteen slots.</param>
    /// <param name="spy">The visitor. Passed by reference, because two of the families take it that way.</param>
    public static void RunEveryArity(World world, ref Spy spy) {
        ArgumentNullException.ThrowIfNull(world);

        var all1 = new QueryDescription().WithAll<Slot0>();
        world.Query<Slot0>(all1, spy.Execute);
        world.QueryWithEntity<Slot0>(all1, spy.Execute);
        world.ForEach<Spy, Slot0>(all1, ref spy);
        world.ForEachWithEntity<Spy, Slot0>(all1, ref spy);

        var all2 = new QueryDescription().WithAll<Slot0, Slot1>();
        world.Query<Slot0, Slot1>(all2, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1>(all2, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1>(all2, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1>(all2, ref spy);

        var all3 = new QueryDescription().WithAll<Slot0, Slot1, Slot2>();
        world.Query<Slot0, Slot1, Slot2>(all3, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2>(all3, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2>(all3, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2>(all3, ref spy);

        var all4 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3>();
        world.Query<Slot0, Slot1, Slot2, Slot3>(all4, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3>(all4, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3>(all4, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3>(all4, ref spy);

        var all5 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4>(all5, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4>(all5, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4>(all5, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4>(all5, ref spy);

        var all6 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>(all6, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>(all6, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>(all6, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>(all6, ref spy);

        var all7 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>(all7, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>(all7, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>(all7, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>(all7, ref spy);

        var all8 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>(all8, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>(all8, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>(all8, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>(all8, ref spy);

        var all9 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>(all9, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>(all9, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>(all9, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>(all9, ref spy);

        var all10 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>(all10, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>(all10, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>(all10, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>(all10, ref spy);

        var all11 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>(all11, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>(all11, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>(all11, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>(all11, ref spy);

        var all12 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>(all12, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>(all12, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>(all12, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>(all12, ref spy);

        var all13 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>(all13, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>(all13, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>(all13, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>(all13, ref spy);

        var all14 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>(all14, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>(all14, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>(all14, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>(all14, ref spy);

        var all15 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>(all15, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>(all15, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>(all15, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>(all15, ref spy);

        var all16 = new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>();
        world.Query<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>(all16, spy.Execute);
        world.QueryWithEntity<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>(all16, spy.Execute);
        world.ForEach<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>(all16, ref spy);
        world.ForEachWithEntity<Spy, Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>(all16, ref spy);
    }

    /// <summary>One description from every arity of every generated builder family.</summary>
    /// <returns>The family's name, the arity, and the description it built.</returns>
    public static IReadOnlyList<(string Family, int Arity, QueryDescription Description)> EveryDescription() => [
        ("WithAll", 1, new QueryDescription().WithAll<Slot0>()),
        ("WithAny", 1, new QueryDescription().WithAny<Slot0>()),
        ("WithNone", 1, new QueryDescription().WithNone<Slot0>()),
        ("WithChanged", 1, new QueryDescription().WithChanged<Slot0>()),
        ("WithAll", 2, new QueryDescription().WithAll<Slot0, Slot1>()),
        ("WithAny", 2, new QueryDescription().WithAny<Slot0, Slot1>()),
        ("WithNone", 2, new QueryDescription().WithNone<Slot0, Slot1>()),
        ("WithChanged", 2, new QueryDescription().WithChanged<Slot0, Slot1>()),
        ("WithAll", 3, new QueryDescription().WithAll<Slot0, Slot1, Slot2>()),
        ("WithAny", 3, new QueryDescription().WithAny<Slot0, Slot1, Slot2>()),
        ("WithNone", 3, new QueryDescription().WithNone<Slot0, Slot1, Slot2>()),
        ("WithChanged", 3, new QueryDescription().WithChanged<Slot0, Slot1, Slot2>()),
        ("WithAll", 4, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3>()),
        ("WithAny", 4, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3>()),
        ("WithNone", 4, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3>()),
        ("WithChanged", 4, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3>()),
        ("WithAll", 5, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4>()),
        ("WithAny", 5, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4>()),
        ("WithNone", 5, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4>()),
        ("WithChanged", 5, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4>()),
        ("WithAll", 6, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>()),
        ("WithAny", 6, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>()),
        ("WithNone", 6, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>()),
        ("WithChanged", 6, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5>()),
        ("WithAll", 7, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>()),
        ("WithAny", 7, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>()),
        ("WithNone", 7, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>()),
        ("WithChanged", 7, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6>()),
        ("WithAll", 8, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>()),
        ("WithAny", 8, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>()),
        ("WithNone", 8, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>()),
        ("WithChanged", 8, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7>()),
        ("WithAll", 9, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>()),
        ("WithAny", 9, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>()),
        ("WithNone", 9, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>()),
        ("WithChanged", 9, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8>()),
        ("WithAll", 10, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>()),
        ("WithAny", 10, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>()),
        ("WithNone", 10, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>()),
        ("WithChanged", 10, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9>()),
        ("WithAll", 11, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>()),
        ("WithAny", 11, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>()),
        ("WithNone", 11, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>()),
        ("WithChanged", 11, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10>()),
        ("WithAll", 12, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>()),
        ("WithAny", 12, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>()),
        ("WithNone", 12, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>()),
        ("WithChanged", 12, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11>()),
        ("WithAll", 13, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>()),
        ("WithAny", 13, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>()),
        ("WithNone", 13, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>()),
        ("WithChanged", 13, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12>()),
        ("WithAll", 14, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>()),
        ("WithAny", 14, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>()),
        ("WithNone", 14, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>()),
        ("WithChanged", 14, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13>()),
        ("WithAll", 15, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>()),
        ("WithAny", 15, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>()),
        ("WithNone", 15, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>()),
        ("WithChanged", 15, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14>()),
        ("WithAll", 16, new QueryDescription().WithAll<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>()),
        ("WithAny", 16, new QueryDescription().WithAny<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>()),
        ("WithNone", 16, new QueryDescription().WithNone<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>()),
        ("WithChanged", 16, new QueryDescription().WithChanged<Slot0, Slot1, Slot2, Slot3, Slot4, Slot5, Slot6, Slot7, Slot8, Slot9, Slot10, Slot11, Slot12, Slot13, Slot14, Slot15>())
    ];
}
