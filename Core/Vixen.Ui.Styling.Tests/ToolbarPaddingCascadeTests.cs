// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     The rules behind a toolbar button's box, resolved at both of the sites #967 reported as
///     disagreeing: a <c>button.size-sm</c> on the strip and the same button inside a
///     <c>toolbar-group</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The cascade was not the defect, and neither of the two rules the report named is the
///         one that wins.</b> The report read <c>ControlTheme.vcss</c>'s
///         <c>button.size-sm { padding: 2px 8px }</c> against <c>EditorTheme.vcss</c>'s
///         <c>toolbar button { padding: 4px 10px }</c> and concluded that the first won inside the
///         group and the second won on the strip, which no ordering produces. There is a third rule:
///         <c>EditorTheme.vcss:693</c> restates <c>button.size-sm</c> at <c>3px 9px</c>. Same
///         specificity as the control theme's, same <c>components</c> layer, later sheet — so it wins
///         at <i>both</i> sites, and the two buttons differ only in the border that
///         <c>toolbar-group button { border-width: 0px }</c> takes off the one in the group.
///     </para>
///     <para>
///         That reproduces the measurement exactly, which is how the arithmetic in the report went
///         wrong: it fitted a border of 0 to the strip and 1 to the group, and the truth is the other
///         way round. Strip <c>132 + 2·9 + 2·1 = 152</c> wide and <c>20 + 2·3 + 2·1 = 28</c> high;
///         group <c>63 + 2·9 + 0 = 81</c> and <c>20 + 2·3 + 0 = 26</c>.
///     </para>
///     <para>
///         The sheets are transcribed rather than loaded, layer declarations and install order
///         included, because the report's remaining innocent explanation was that
///         <c>CascadeLayers.Declare</c> decides something at install time that differs between two
///         sheets naming the same layers. It does not: a layer of the same name in a later sheet is
///         the same layer, so the tie falls to source order.
///     </para>
/// </remarks>
public class ToolbarPaddingCascadeTests {
    /// <summary>What <c>ControlTheme.vcss</c> says about a button, cut to the properties in dispute.</summary>
    const string ControlTheme = """
        @layer base, components, utilities;
        @layer components {
            button, icon-button, toggle-button { padding: 5px 12px; border-width: 1px; }
            button.size-sm, icon-button.size-sm, toggle-button.size-sm { padding: 2px 8px; }
        }
        """;

    /// <summary>What <c>EditorTheme.vcss</c> says, installed after it as the editor installs it.</summary>
    const string EditorTheme = """
        @layer base, components, utilities;
        @layer components {
            toolbar button, toolbar icon-button, toolbar toggle-button { padding: 4px 10px; }
            toolbar-group button, toolbar-group icon-button, toolbar-group toggle-button,
            toolbar-group button.variant-subtle, toolbar-group icon-button.variant-subtle {
                border-width: 0px;
            }
            button, icon-button, toggle-button { padding: 5px 12px; }
            button.size-sm, icon-button.size-sm, toggle-button.size-sm { padding: 3px 9px; }
        }
        """;

    [Fact]
    public void One_padding_wins_at_both_sites_and_it_is_the_editors_own_size_rule() {
        var fixture = Themed();
        var toolbar = fixture.Tree.CreateElement("toolbar", classNames: ["size-md", "variant-default"]);
        var onTheStrip = fixture.Tree.CreateElement("button", toolbar, classNames: ["size-sm", "variant-subtle"]);
        var group = fixture.Tree.CreateElement("toolbar-group", toolbar);
        var inTheGroup = fixture.Tree.CreateElement("button", group, classNames: ["size-sm", "variant-subtle"]);

        // (0,1,1) beats (0,0,2) whatever the sheet or the depth, so `toolbar button` loses on the
        // strip too — and between the two class rules the later sheet takes it.
        Assert.Equal("3px", fixture.Value(onTheStrip, "padding-top"));
        Assert.Equal("9px", fixture.Value(onTheStrip, "padding-left"));
        Assert.Equal("3px", fixture.Value(inTheGroup, "padding-top"));
        Assert.Equal("9px", fixture.Value(inTheGroup, "padding-left"));
    }

    [Fact]
    public void The_border_is_the_only_thing_the_group_changes_and_it_takes_it_away() {
        // The report's second measurement, and the half it had backwards: the strip keeps the base
        // button's 1px and the group's member is the one drawn without it.
        var fixture = Themed();
        var toolbar = fixture.Tree.CreateElement("toolbar");
        var onTheStrip = fixture.Tree.CreateElement("button", toolbar, classNames: ["size-sm", "variant-subtle"]);
        var group = fixture.Tree.CreateElement("toolbar-group", toolbar);
        var inTheGroup = fixture.Tree.CreateElement("button", group, classNames: ["size-sm", "variant-subtle"]);

        Assert.Equal("1px", fixture.Value(onTheStrip, "border-top-width"));
        Assert.Equal("0", fixture.Value(inTheGroup, "border-top-width"));
    }

    [Fact]
    public void A_descendant_rule_still_reaches_a_grandchild_two_levels_down() {
        // The one reading of the matcher that would have produced two different paddings is a
        // descendant combinator that walks a single level, leaving `toolbar button` matching the
        // strip and missing the group's member. It does not stop there.
        var fixture = new CascadeFixture();
        fixture.Load("toolbar button { color: reached }");

        var toolbar = fixture.Tree.CreateElement("toolbar");
        var group = fixture.Tree.CreateElement("toolbar-group", toolbar);
        var inTheGroup = fixture.Tree.CreateElement("button", group);

        Assert.Equal("reached", fixture.Value(inTheGroup));
    }

    [Fact]
    public void A_later_sheet_reusing_a_layer_name_writes_into_the_same_layer() {
        // The install-time explanation, asserted on its own so that a regression in
        // `CascadeLayers.Declare` names itself rather than showing up as a padding.
        var fixture = new CascadeFixture();
        fixture.Load("@layer base, components, utilities; @layer components { .a { color: first } }");
        fixture.Load("@layer base, components, utilities; @layer components { .a { color: second } }");

        Assert.Equal("second", fixture.Value(fixture.Tree.CreateElement("button", classNames: ["a"])));
    }

    static CascadeFixture Themed() {
        var fixture = new CascadeFixture();
        fixture.Load(ControlTheme);
        fixture.Load(EditorTheme);

        return fixture;
    }
}
