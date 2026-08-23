// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Every container in the set, built the way markup builds one.</summary>
/// <remarks>
///     <para>
///         <b>A nested tag is <c>parent.ContentHost.Add&lt;T&gt;()</c> followed by property
///         assignments, and that is the whole of what the VXML emitter does.</b> So these tests build
///         by hand in exactly that order and assert the control ended up in the state its
///         <c>AddX</c> method would have produced — which is the property the two routes have to
///         share, and the one that was silently false for every control here.
///     </para>
///     <para>
///         ⚠ <b>The assignment order matters and is deliberately the awkward one.</b> A tag is
///         created before its attributes are assigned, so a container hears about a child that does
///         not yet know what it is — no value, no id, no label. Every failure this file exists to
///         catch is in that gap: a <c>Select</c> whose field kept showing its placeholder because the
///         option arrived nameless, a <c>DockPanel</c> that was never filed because it had no id yet.
///     </para>
/// </remarks>
public class NestedTagTests {
    /// <summary>What the emitter writes for a nested tag.</summary>
    /// <remarks>
    ///     ⚠ Through <see cref="BuildContext.Inner(UiElement)" /> rather than
    ///     <c>UiElement.ContentHost</c>, which is <c>protected internal</c> — and that is the
    ///     emitter's own route, so this helper is the generated code rather than an approximation
    ///     of it.
    /// </remarks>
    static T Tag<T>(UiElement parent) where T : UiElement, new() => BuildContext.Inner(parent).Add<T>();

    [Fact]
    public void A_nested_radio_is_part_of_its_group() {
        using var fixture = new AdvancedFixture();

        var group = fixture.Add<RadioGroup>();
        group.Value = "medium";

        // ⚠ The value is set *before* the options exist, which is the ordinary case for a group built
        // from saved settings — and the case a snapshot alone cannot serve.
        foreach (var value in (string[]) ["low", "medium", "high"]) {
            var option = Tag<RadioButton>(group);
            option.Value = value;
            option.Label = value;
        }

        Assert.Equal(3, group.Options.Count);
        Assert.Equal("medium", group.Options.Single(option => option.IsChecked).Value);

        // The roving tab index: one stop, and it is the chosen one.
        Assert.Equal([-1, 0, -1], group.Options.Select(option => option.TabIndex));
    }

    [Fact]
    public void A_nested_radio_reports_when_it_is_clicked() {
        using var fixture = new AdvancedFixture();

        var group = fixture.Add<RadioGroup>();
        var option = Tag<RadioButton>(group);
        option.Value = "high";

        var reported = (string?) null;
        group.ValueChanged += (_, value) => reported = value;

        option.Raise(new ClickEvent());

        Assert.Equal("high", reported);
        Assert.Equal("high", group.Value);
    }

    [Fact]
    public void A_nested_option_is_part_of_its_select() {
        using var fixture = new AdvancedFixture();

        var select = fixture.Add<Select>();
        select.Placeholder = "Blend mode";
        select.Value = "cutout";

        foreach (var (value, label) in ((string, string)[]) [("opaque", "Opaque"), ("cutout", "Cutout")]) {
            var option = Tag<Option>(select);
            option.Value = value;
            option.Label = label;
        }

        Assert.Equal(2, select.Options.Count);

        // ⚠ The assertion that matters, and the one the sample had to work around by re-assigning
        // `Value` after the options existed: the closed field shows the chosen option's label rather
        // than the placeholder.
        Assert.Equal("Cutout", select.Field.Text);
        Assert.Equal("cutout", select.Selected?.Value);
    }

    [Fact]
    public void A_nested_step_gets_its_separator_and_the_last_one_is_current() {
        using var fixture = new AdvancedFixture();

        var trail = fixture.Add<Breadcrumb>();

        foreach (var label in (string[]) ["Assets", "Materials", "Standard"]) {
            var step = Tag<BreadcrumbItem>(trail);
            step.Label = label;
            step.Value = label;
        }

        Assert.Equal(3, trail.Steps.Count);

        // Three steps and two chevrons, interleaved — which is what the separator being inserted
        // *before* the step that asked for it is.
        Assert.Equal(
            ["breadcrumb-item", "icon", "breadcrumb-item", "icon", "breadcrumb-item"],
            trail.Children.Select(child => child.Tag)
        );

        Assert.Equal(ElementState.None, trail.Steps[0].State & ElementState.Checked);
        Assert.True((trail.Steps[^1].State & ElementState.Checked) != 0);
    }

    [Fact]
    public void A_nested_menu_becomes_a_name_on_the_bar_and_moves_to_the_root() {
        using var fixture = new AdvancedFixture();

        var bar = fixture.Add<MenuBar>();

        var file = Tag<Menu>(bar);
        file.Label = "File";

        var item = Tag<MenuItem>(file);
        item.Label = "New";

        Assert.Equal(["File"], bar.Items.Select(entry => entry.Label));
        Assert.Same(file, bar.Items[0].Menu);

        // ⚠ At the root, not inside the bar. A dropdown parented to the strip that drops it is a
        // dropdown clipped by it.
        Assert.Same(fixture.Document.Root, file.Parent);

        Assert.Equal(["New"], file.Items.Select(entry => entry.Label));
    }

    [Fact]
    public void A_nested_menu_inside_a_menu_becomes_a_submenu() {
        using var fixture = new AdvancedFixture();

        var bar = fixture.Add<MenuBar>();

        var file = Tag<Menu>(bar);
        file.Label = "File";

        var recent = Tag<Menu>(file);
        recent.Label = "Open Recent";

        Assert.Equal(["Open Recent"], file.Items.Select(entry => entry.Label));
        Assert.Same(recent, file.Items[0].Submenu);
        Assert.Same(file, recent.ParentMenu);
        Assert.Same(fixture.Document.Root, recent.Parent);
    }

    [Fact]
    public void A_nested_dock_panel_is_registered_and_placed() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();

        var panel = Tag<DockPanel>(host);
        panel.Id = "hierarchy";
        panel.Title = "Hierarchy";

        Assert.Equal(["hierarchy"], host.Panels.Keys);
        Assert.NotNull(host.Layout.Find("hierarchy"));

        fixture.Update();

        // The tab the arrangement built for it, carrying the title assigned after the id.
        Assert.Equal(["Hierarchy"], host.Groups[0].Tabs.Children.OfType<DockTab>().Select(tab => tab.Label));
    }

    /// <summary>The bar built from nested tags opens, hovers and closes like one built by hand.</summary>
    /// <remarks>
    ///     ⚠ <b>Driven through the event system rather than by asserting the tree, because the tree
    ///     was never the thing that broke.</b> A nested <c>&lt;MenuItem&gt;</c> drew perfectly well
    ///     before any of this; what it did not do was open the submenu it was in front of, because
    ///     the hover handler was attached by <c>AddItem</c> and nothing had called it.
    /// </remarks>
    [Fact]
    public void A_bar_built_from_nested_tags_opens_and_hovers() {
        using var fixture = new AdvancedFixture();

        var bar = fixture.Add<MenuBar>();

        var file = Tag<Menu>(bar);
        file.Label = "File";

        var recent = Tag<Menu>(file);
        recent.Label = "Open Recent";

        fixture.Update();

        // The bar's own item opens its menu, which is `MenuBar.Chosen` — and that is reached only if
        // the item is a child of the bar and the bar recognises it as one of its own.
        bar.Items[0].Raise(new ClickEvent());
        Assert.True(file.IsOpen);
        Assert.Same(file, bar.Current);

        // Hovering the row that fronts a submenu opens it. The handler is `Menu.OnChildAdded`'s, and
        // it is Direct — so it fires on the item and never routes.
        file.Items[0].Raise(new PointerEvent { Action = PointerAction.Entered });
        Assert.True(recent.IsOpen);

        // And the whole chain goes down together.
        file.Close(CloseReason.Code);
        Assert.False(recent.IsOpen);
    }

    [Fact]
    public void A_nested_expander_is_a_section_of_its_accordion() {
        using var fixture = new AdvancedFixture();

        var accordion = fixture.Add<Accordion>();
        accordion.AllowMultiple = false;

        var first = Tag<Expander>(accordion);
        first.Label = "Surface";

        var second = Tag<Expander>(accordion);
        second.Label = "Shading";

        Assert.Equal(2, accordion.Sections.Count);

        // Exclusivity, which is the thing a section being *registered* buys.
        first.IsExpanded = true;
        second.IsExpanded = true;

        Assert.False(first.IsExpanded);
        Assert.True(second.IsExpanded);
    }
}
