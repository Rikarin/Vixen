// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Reflection;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The tests that share <c>TypeRegistry</c>, which is process-wide.</summary>
/// <remarks>
///     ⚠ <b>xunit runs test classes in parallel and the registry is a global.</b> Each of these
///     classes empties it and registers its own descriptors, so two of them running at once leave
///     each other inspecting types that are no longer there — which surfaces as an index out of range
///     in whichever one lost the race, several assertions away from the cause. It cost a green class
///     going red the moment a second one was added.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedTypeRegistry {
    public const string Name = "TypeRegistry";
}

/// <summary>Editing a struct inside a struct, which used to be shown read-only.</summary>
/// <remarks>
///     <para>
///         <b>An accessor takes its instance as <c>object</c>, so reading a struct member gives a
///         box.</b> Writing into that box changes a copy nothing holds — so the grid showed nested
///         structs read-only rather than accepting edits that would vanish, and the type's remarks
///         said the fix needed <c>ref</c> accessors on the descriptor.
///     </para>
///     <para>
///         ⚠ <b>It did not.</b> Read-modify-write does it: set the leaf, then write each owner back
///         into <i>its</i> owner, innermost first. The grid keeps the path from the target to the
///         member rather than the member alone, which is the one thing that was missing.
///     </para>
///     <para>
///         ⚠ <b>Two of the sabotages needed the fixture changed before they could land</b>, and both
///         changes are the realistic case rather than a contrivance for the test: a setter that
///         maintains an invariant, so that re-reading a sibling row is not a no-op, and a registered
///         descriptor for a type the grid already draws an editor for, which is what a
///         <c>Vector3</c> is.
///     </para>
///     <para>
///         Verified by sabotage, five of five landing: writing only the leaf fails 3, writing the
///         owners outermost first fails 3, reading a default off the member rather than through the
///         path fails 5, not re-reading the sibling rows of an edited struct fails 1, and expanding a
///         member that already has an editor fails 1.
///     </para>
/// </remarks>
[Collection(SharedTypeRegistry.Name)]
public sealed class NestedMemberTests : IDisposable {
    public NestedMemberTests() {
        TypeRegistry.Clear();
        TypeRegistry.Register(Describe<Point>("Point", () => new Point(), Members<Point>()));
        TypeRegistry.Register(Describe<Placement>("Placement", () => new Placement(), Members<Placement>()));
        TypeRegistry.Register(Describe<Thing>("Thing", () => new Thing(), Members<Thing>()));

        // ⚠ A descriptor for a type the grid *already* has an editor for, which is the only way to
        // reach the rule that expansion happens where nothing else claimed the member. A `Vector3`
        // in a real application is exactly this: registered, and drawn by three numeric fields.
        TypeRegistry.Register(Describe<string>("String", static () => "", Members<string>()));
    }

    public void Dispose() => TypeRegistry.Clear();

    /// <summary>A point whose setter maintains an invariant, which is why sibling rows are re-read.</summary>
    /// <remarks>
    ///     ⚠ <b><c>X</c> pushes <c>Y</c> up to meet it.</b> Contrived, and the shape is not: a
    ///     normalised quaternion, a range whose end clamps its start, a size that keeps its aspect —
    ///     a setter that changes a sibling is ordinary. Without one in the fixture, re-reading the
    ///     other rows after a write is a no-op and sabotaging it fails nothing.
    /// </remarks>
    struct Point {
        float y;

        public float X { get; set; }

        public float Y {
            readonly get => MathF.Max(y, X);
            set => y = value;
        }
    }

    /// <summary>A struct holding a struct, which is the case that did not work.</summary>
    struct Placement {
        public Point Origin { get; set; }

        public float Scale { get; set; }
    }

    sealed class Thing {
        public Placement Where { get; set; }

        public string Name { get; set; } = "thing";
    }

    static TypeDescriptor Describe<T>(string alias, Func<object> create, MemberDescriptor[] members) =>
        new(typeof(T), alias, TypeTraits.DataContract | TypeTraits.EditorVisible, members, create);

    static MemberDescriptor[] Members<T>() =>
        typeof(T).GetProperties()
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Select(static (property, order) => new MemberDescriptor(
                property.Name,
                property.PropertyType,
                order,
                property.GetValue,
                property.SetValue,
                new MemberPresentation(IsEditorVisible: true)
            ))
            .ToArray();

    static (AdvancedFixture Fixture, PropertyGrid Grid, Thing Target) Inspected() {
        var fixture = new AdvancedFixture();
        var target = new Thing { Where = new Placement { Origin = new Point { X = 1f, Y = 2f }, Scale = 3f } };
        var grid = fixture.Add<PropertyGrid>();

        grid.Inspect(target);
        fixture.Update();

        return (fixture, grid, target);
    }

    static PropertyRow Row(PropertyGrid grid, params string[] path) =>
        grid.Rows.Single(row => row.Path.Select(member => member.Name).SequenceEqual(path));

    [Fact]
    public void A_struct_inside_a_struct_gets_a_row_of_its_own() {
        var (fixture, grid, _) = Inspected();
        using var owned = fixture;

        // The chain, two levels down. Before this the grid stopped at `Where` and drew a read-only
        // placeholder beside it.
        Assert.NotNull(Row(grid, "Where"));
        Assert.NotNull(Row(grid, "Where", "Origin"));
        Assert.NotNull(Row(grid, "Where", "Origin", "X"));
        Assert.NotNull(Row(grid, "Where", "Scale"));
    }

    [Fact]
    public void Editing_it_reaches_the_object_two_boxes_up() {
        var (fixture, grid, target) = Inspected();
        using var owned = fixture;

        var x = Row(grid, "Where", "Origin", "X").Editor.Children[0];
        var input = Assert.IsType<NumericInput>(x);

        input.Number = 9d;
        fixture.Update();

        // ⚠ **The whole point.** `X` was written into a box of `Point`, which was written into a box
        // of `Placement`, which was written into the target. Setting only the leaf changes a copy and
        // this assertion is the one that says so.
        Assert.Equal(9f, target.Where.Origin.X);

        // And nothing else moved: a read-modify-write that rebuilt an owner from defaults instead of
        // from what it read would zero the siblings.
        Assert.Equal(9f, target.Where.Origin.Y);
        Assert.Equal(3f, target.Where.Scale);
    }

    [Fact]
    public void A_sibling_row_stops_showing_a_box_that_no_longer_exists() {
        var (fixture, grid, target) = Inspected();
        using var owned = fixture;

        var y = Row(grid, "Where", "Origin", "Y");

        target.Where = new Placement { Origin = new Point { X = 5f, Y = 6f }, Scale = 7f };
        grid.Reload();

        var x = Assert.IsType<NumericInput>(Row(grid, "Where", "Origin", "X").Editor.Children[0]);
        x.Number = 8d;
        fixture.Update();

        // ⚠ Editing `X` replaces the whole of `Origin` and the whole of `Where`, so every row under
        // them is showing a value that came out of a box which is now garbage. `Y` has to be re-read,
        // and it is the reason `Write` refreshes the rows that share an owner rather than only its
        // own.
        Assert.Equal("8", Assert.IsType<NumericInput>(y.Editor.Children[0]).Value);
        Assert.Equal(8f, target.Where.Origin.X);
        Assert.Equal(8f, target.Where.Origin.Y);
    }

    [Fact]
    public void A_member_that_already_has_an_editor_is_not_expanded() {
        var (fixture, grid, _) = Inspected();
        using var owned = fixture;

        // ⚠ `Name` is a string with a registered descriptor nowhere in sight, but the rule is the
        // general one: expansion only happens where nothing else claimed the member. A `Vector3` that
        // the grid has an editor for is still three numbers, and expanding it would show the value
        // twice and let two controls fight over it.
        Assert.Single(Row(grid, "Name").Editor.Children);
        Assert.DoesNotContain(grid.Rows, row => row.Path.Count > 1 && row.Path[0].Name == "Name");
    }

    [Fact]
    public void Several_targets_are_all_written_through_the_path() {
        var fixture = new AdvancedFixture();
        using var owned = fixture;

        var first = new Thing { Where = new Placement { Origin = new Point { X = 1f } } };
        var second = new Thing { Where = new Placement { Origin = new Point { X = 1f } } };

        var grid = fixture.Add<PropertyGrid>();
        grid.Inspect(first, second);
        fixture.Update();

        var x = Assert.IsType<NumericInput>(Row(grid, "Where", "Origin", "X").Editor.Children[0]);
        x.Number = 4d;
        fixture.Update();

        // Multi-target editing is what an inspector is for, and a path does not change that — each
        // target is walked separately, because each of them has its own boxes.
        Assert.Equal(4f, first.Where.Origin.X);
        Assert.Equal(4f, second.Where.Origin.X);
    }
}
