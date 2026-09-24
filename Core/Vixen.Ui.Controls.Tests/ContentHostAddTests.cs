// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Composition;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>In C#, <c>container.Add&lt;T&gt;()</c> puts the child where a nested markup tag goes.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Until #1425 it did not, on any control whose content is a part.</b>
///         <c>UiElement.Add</c> parented on the element it was called on, and only markup asked
///         <c>ContentHost</c> — so <c>groupBox.Add&lt;TextBox&gt;()</c> put the field beside the legend,
///         <c>comboBox.Add&lt;Option&gt;()</c> put a suggestion under the field for ever, and the only
///         container that corrected it was <c>SelectBase</c>, by hand, after the sprite editor shipped
///         a toolbar of unchoosable options (#1394).
///     </para>
///     <para>
///         ⚠ <b>A census rather than a list</b>, because the defect is a property of every control
///         that overrides <c>ContentHost</c>, and the next one written would not be on a list. The
///         instrument is the count: a census that found no such controls would pass every assertion
///         in it.
///     </para>
/// </remarks>
public class ContentHostAddTests {
    /// <summary>Every concrete element type in the control set that can be made with <c>new()</c>.</summary>
    static IEnumerable<Type> Controls() =>
        typeof(Control).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(UiElement).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is { IsPublic: true })
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

    // ⚠ Through a helper of our own rather than `UiElement.Add<T>` itself: its `params ReadOnlySpan`
    // cannot be passed through `MethodInfo.Invoke`.
    static readonly MethodInfo AddOfT = typeof(ContentHostAddTests)
        .GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();

    /// <summary>Both overloads, on every control whose content host is not itself.</summary>
    [Fact]
    public void Add_puts_a_child_where_a_nested_tag_goes_on_every_control_that_routes_its_content() {
        using var fixture = new ControlFixture();
        var routed = new List<string>();
        var wrong = new List<string>();

        foreach (var type in Controls()) {
            var control = (UiElement) AddOfT.MakeGenericMethod(type)
                .Invoke(null, [fixture.Document.Root])!;

            // What the markup emitter writes for a nested tag, so the comparison is against the
            // route markup takes rather than a restatement of it.
            var host = BuildContext.Inner(control);

            if (ReferenceEquals(host, control)) {
                continue;
            }

            routed.Add(type.Name);

            var plain = control.Add("probe");
            var typed = control.Add<UiElement>("typed-probe");

            if (!ReferenceEquals(plain.Parent, host) || !ReferenceEquals(typed.Parent, host)) {
                wrong.Add($"{type.Name}: Add parented on {plain.Parent?.GetType().Name} '{plain.Parent?.Tag}', "
                    + $"a nested tag goes to {host.GetType().Name} '{host.Tag}'");
            }
        }

        Assert.Empty(wrong);

        // ⚠ The instrument. The issue names sixteen overriding classes (seventeen with `MultiSelect`,
        // which inherits one); a census that reached none of them would have passed the line above.
        Assert.True(routed.Count >= 16, $"only {routed.Count} controls route their content: {string.Join(", ", routed)}");
        Assert.Contains(nameof(GroupBox), routed);
        Assert.Contains(nameof(ComboBox), routed);
        Assert.Contains(nameof(ScrollView), routed);
    }

    /// <summary>The issue's own example: a field added to a group box is inside the group, under the legend.</summary>
    [Fact]
    public void A_field_added_to_a_group_box_is_in_its_content_under_the_legend() {
        using var fixture = new ControlFixture();
        var group = fixture.Add<GroupBox>();

        group.Label = "Shadows";
        var field = group.Add<TextBox>();
        fixture.Update();

        Assert.Same(group.Content, field.Parent);
        Assert.Equal([group.Legend, group.Content], group.Children);
        Assert.True(
            field.AbsoluteTop >= group.Legend.AbsoluteTop + group.Legend.Height,
            $"the field's top {field.AbsoluteTop} is not below the legend's bottom {group.Legend.AbsoluteTop + group.Legend.Height}"
        );
    }

    /// <summary>
    ///     ⚠ <b>A combo box's suggestion added from C# is a suggestion</b> — the asymmetry the issue
    ///     named: <c>ComboBox</c> is not a <c>SelectBase</c>, so the fix #1394 made there never reached it.
    /// </summary>
    [Fact]
    public void An_option_added_to_a_combo_box_is_one_of_its_suggestions() {
        using var fixture = new ControlFixture();
        var combo = fixture.Add<ComboBox>();

        var option = combo.Add<Option>();
        option.Value = "linear";
        option.Label = "Linear";
        fixture.Update();

        Assert.Same(combo.List.Content, option.Parent);
        Assert.Equal([option], combo.Options);
        Assert.DoesNotContain(option, combo.Children);
    }

    /// <summary>A control's own parts are still its children, whatever order they were made in.</summary>
    /// <remarks>
    ///     ⚠ <b>The other half of the change</b>: <c>Control.Part</c> made its parts with <c>Add</c>,
    ///     so once <c>Add</c> asked <c>ContentHost</c> every part made after the content part would
    ///     have been made inside it. A <c>Card</c>'s footer is the case that proves it, because it is
    ///     made lazily — long after the body exists.
    /// </remarks>
    [Fact]
    public void A_part_made_after_the_content_part_is_still_beside_it() {
        using var fixture = new ControlFixture();
        var card = fixture.Add<Card>();

        var body = card.Add("div");
        var footer = card.Footer;

        Assert.Same(card.Body, body.Parent);
        Assert.Same(card, footer.Parent);
        Assert.Equal([card.Body, footer], card.Children);

        var scroll = fixture.Add<ScrollView>();

        Assert.Equal([scroll.Content, scroll.VerticalBar, scroll.HorizontalBar], scroll.Children.Take(3));
    }
}
