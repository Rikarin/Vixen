// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.Inspector.Drawers;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Inspector.Tests;

/// <summary>The two drawers that draw rows of their own: a nested object, and a list.</summary>
/// <remarks>
///     ⚠ <b>Both are about writing back, which is where every implementation of them goes wrong.</b>
///     A nested struct is read as a copy and a list is a shared reference, so the naive version of
///     each — edit what you were handed — silently does nothing in the first case and destroys undo
///     in the second.
/// </remarks>
public class CompositeTests {
    static InspectorDescriptor Describe(Type type) =>
        InspectorRegistry.Find(type)
        ?? throw new InvalidOperationException($"The generator registered no descriptor for {type.Name}.");

    static InspectorMember Member(Type type, string name) =>
        Describe(type).Members.Single(member => member.Name == name);

    static InspectorField Field(object target, string name) =>
        new(Describe(target.GetType()), Member(target.GetType(), name), [target]);

    static UiElement Host() => new UiDocument(400f, 800f).Root;

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }

    static IEnumerable<InspectorRow> Rows(UiElement editor) =>
        editor is CompositeEditor composite ? composite.Rows : [];

    [Theory]
    [InlineData("Extent", typeof(NestedDrawer))]
    [InlineData("Credit", typeof(NestedDrawer))]
    [InlineData("Weights", typeof(ListDrawer))]
    [InlineData("Falloff", typeof(ListDrawer))]
    public void The_registry_resolves_a_composite_member_to_the_drawer_for_its_shape(string name, Type expected) =>
        Assert.IsType(expected, DrawerRegistry.Default.Resolve(Member(typeof(Volume), name)));

    /// <summary>
    ///     ⚠ The one that used to read <c>Vixen.Core.Mathematics.Bounds</c> and nothing else: a member
    ///     whose own type has members fell through to the last resort, which draws the value as text.
    /// </summary>
    [Fact]
    public void A_nested_object_draws_a_row_per_member_of_its_own_type() {
        var volume = new Volume();
        var field = Field(volume, "Extent");

        var editor = new NestedDrawer().Build(field, Host());

        Assert.Equal(["Low", "High", "Padding"], Rows(editor).Select(row => row.Field.Member.Name));
    }

    [Fact]
    public void Editing_a_nested_row_reaches_the_object_that_holds_it() {
        var volume = new Volume();
        var field = Field(volume, "Extent");

        var editor = new NestedDrawer().Build(field, Host());
        var padding = Rows(editor).Single(row => row.Field.Member.Name == "Padding");

        Assert.True(padding.Field.Write(4f));
        Assert.Equal(4f, volume.Extent.Padding);
    }

    /// <summary>
    ///     ⚠ Two objects, one leaf edited: the write reaches both, and each keeps its own values for
    ///     the leaves nobody touched.
    /// </summary>
    [Fact]
    public void Editing_one_leaf_across_a_selection_leaves_the_other_leaves_alone() {
        var first = new Volume { Extent = new Bounds { Low = new Vector3(1f), High = new Vector3(2f) } };
        var second = new Volume { Extent = new Bounds { Low = new Vector3(9f), High = new Vector3(9f) } };

        var descriptor = Describe(typeof(Volume));
        var field = new InspectorField(descriptor, Member(typeof(Volume), "Extent"), [first, second]);

        var editor = new NestedDrawer().Build(field, Host());
        var padding = Rows(editor).Single(row => row.Field.Member.Name == "Padding");

        Assert.True(padding.Field.Write(3f));

        Assert.Equal(3f, first.Extent.Padding);
        Assert.Equal(3f, second.Extent.Padding);

        Assert.Equal(new Vector3(1f), first.Extent.Low);
        Assert.Equal(new Vector3(9f), second.Extent.Low);
    }

    /// <summary>
    ///     ⚠ The nested rows follow the member, not the instance they were built over. Assigning a
    ///     different object to the member — which an undo does — has to bring the rows with it.
    /// </summary>
    [Fact]
    public void A_nested_row_follows_the_member_when_the_object_behind_it_is_replaced() {
        var volume = new Volume();
        var field = Field(volume, "Credit");
        var drawer = new NestedDrawer();

        var editor = drawer.Build(field, Host());

        volume.Credit = new Attribution { Author = "Someone Else", Year = 1999 };
        drawer.Show(field, editor);

        var author = Rows(editor).Single(row => row.Field.Member.Name == "Author");

        Assert.True(author.Field.Write("Jiu"));
        Assert.Equal("Jiu", volume.Credit.Author);
    }

    [Fact]
    public void A_nested_object_that_is_null_says_so_rather_than_drawing_rows() {
        var volume = new Volume { Credit = null! };
        var field = Field(volume, "Credit");

        var editor = new NestedDrawer().Build(field, Host());

        Assert.Empty(Rows(editor));
        Assert.Contains(Descendants(editor).OfType<TextBlock>(), text => text.Text == "None");
    }

    /// <summary>⚠ A type that contains itself, which is a tree node and not an exotic case.</summary>
    [Fact]
    public void A_type_that_contains_itself_stops_rather_than_running_out_of_stack() {
        var root = new Recursive();
        var current = root;

        for (var depth = 0; depth < 20; depth++) {
            current = current.Inner = new Recursive { Depth = depth };
        }

        var field = Field(root, "Inner");
        var editor = new NestedDrawer { Drawers = DrawerRegistry.Default }.Build(field, Host());

        // It built something rather than throwing, and it stopped: the deepest foldout draws its
        // own `Inner` as text instead of opening another one.
        Assert.NotEmpty(Rows(editor));
        Assert.True(Depth(editor) <= NestedDrawer.MaxDepth, "the nested rows went past the bound");

        static int Depth(UiElement element) {
            var deepest = 0;

            foreach (var row in Rows(element)) {
                foreach (var child in row.Children) {
                    deepest = Math.Max(deepest, Depth(child));
                }
            }

            return deepest + 1;
        }
    }

    [Fact]
    public void A_list_draws_a_row_per_element() {
        var volume = new Volume();
        var field = Field(volume, "Weights");

        var editor = new ListDrawer().Build(field, Host());

        Assert.Equal(["Element 0", "Element 1", "Element 2"], Rows(editor).Select(row => row.Field.Member.DisplayName));
    }

    /// <summary>
    ///     ⚠ <b>Copy-on-write, and it is what makes undo possible at all.</b> A drawer that mutated
    ///     the object's own list would leave the undo command holding the same reference as its
    ///     "before" and its "after".
    /// </summary>
    [Fact]
    public void Editing_an_element_replaces_the_list_rather_than_mutating_it() {
        var volume = new Volume();
        var original = volume.Weights;

        var field = Field(volume, "Weights");
        var editor = new ListDrawer().Build(field, Host());

        Assert.True(Rows(editor).First().Field.Write(0.9f));

        Assert.Equal(0.9f, volume.Weights[0]);
        Assert.NotSame(original, volume.Weights);
        Assert.Equal(0.25f, original[0]);
    }

    [Fact]
    public void Adding_an_element_grows_the_list_with_the_element_types_own_default() {
        var volume = new Volume();
        var field = Field(volume, "Weights");
        var editor = new ListDrawer().Build(field, Host());

        Press(editor, "Add Element");

        Assert.Equal(4, volume.Weights.Count);
        Assert.Equal(0f, volume.Weights[3]);
    }

    [Fact]
    public void Removing_an_element_drops_that_one_and_keeps_the_order() {
        var volume = new Volume();
        var field = Field(volume, "Weights");
        var editor = new ListDrawer().Build(field, Host());

        Remove(editor, index: 1);

        Assert.Equal([0.25f, 0.75f], volume.Weights);
    }

    [Fact]
    public void An_element_can_be_moved_and_the_ends_cannot_go_further() {
        var volume = new Volume();
        var field = Field(volume, "Weights");
        var editor = new ListDrawer().Build(field, Host());

        Move(editor, index: 2, "list-up");
        Assert.Equal([0.25f, 0.75f, 0.5f], volume.Weights);

        // The first row's Move Up and the last row's Move Down are disabled rather than absent, so
        // the buttons stay in the same place as the list is reordered.
        Assert.True(Icon(editor, 0, "list-up").Disabled);
        Assert.True(Icon(editor, volume.Weights.Count - 1, "list-down").Disabled);
    }

    [Fact]
    public void An_array_is_resized_as_an_array_rather_than_becoming_a_list() {
        var volume = new Volume();
        var field = Field(volume, "Falloff");
        var editor = new ListDrawer().Build(field, Host());

        Press(editor, "Add Element");

        Assert.Equal(3, volume.Falloff.Length);
        Assert.IsType<float[]>(volume.Falloff);
    }

    /// <summary>
    ///     ⚠ A <c>[Range(0, 1)] float[]</c> means every element is a slider. The attribute is a
    ///     statement about the values, and dropping it would make an annotated array the one place
    ///     the annotation did not apply.
    /// </summary>
    [Fact]
    public void An_element_inherits_the_declared_members_presentation() {
        var volume = new Volume();

        var falloff = new ListDrawer().Build(Field(volume, "Falloff"), Host());
        Assert.All(Rows(falloff), row => Assert.NotNull(row.Field.Member.Range));
        Assert.All(Rows(falloff), row => Assert.IsType<Slider>(row.Editor));

        volume.Layers.Add(AssetId.Empty);

        var layers = new ListDrawer().Build(Field(volume, "Layers"), Host());
        Assert.All(Rows(layers), row => Assert.IsType<AssetDrawer>(row.Drawer));
    }

    [Fact]
    public void A_selection_holding_different_lists_says_so_rather_than_showing_one_of_them() {
        var descriptor = Describe(typeof(Volume));

        var field = new InspectorField(
            descriptor,
            Member(typeof(Volume), "Weights"),
            [new Volume(), new Volume()]
        );

        var editor = new ListDrawer().Build(field, Host());

        Assert.Empty(Rows(editor));
        Assert.Contains(Descendants(editor).OfType<TextBlock>(), text => text.Text?.Contains("different", StringComparison.Ordinal) == true);
    }

    static void Press(UiElement editor, string label) =>
        Descendants(editor).OfType<Button>().Single(button => button.Label == label).Activate();

    static void Remove(UiElement editor, int index) => Icon(editor, index, "list-remove").Activate();

    static void Move(UiElement editor, int index, string className) => Icon(editor, index, className).Activate();

    static IconButton Icon(UiElement editor, int index, string className) =>
        Rows(editor).ElementAt(index).Children.OfType<IconButton>().Single(button => button.HasClass(className));
}
