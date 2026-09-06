// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The titled box: a legend, the controls it covers, and one node in the tree.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here would pass against a <see cref="Card" /> if it were about the
///     picture, and none of them is.</b> Doc 49 § 7.1 ranks <c>GroupBox</c> beside
///     <c>LabeledContent</c> and says <c>Card</c> approximates it; what a card approximates is the
///     bordered box with a heading in it, and what it cannot do is tell a screen reader that the
///     four controls inside belong to one question. So the subject here is the role, the name, and
///     the one place the caption's words are kept.
/// </remarks>
public class GroupBoxTests {
    static (ControlFixture Fixture, GroupBox Box) Group(string? label = "Shadows") {
        var fixture = new ControlFixture();
        var box = fixture.Add<GroupBox>();

        box.Label = label;
        fixture.Update();

        return (fixture, box);
    }

    /// <summary>It is one group in the tree, named by its legend.</summary>
    /// <remarks>
    ///     ⚠ <b>The name is the group's own and not a relation's</b>, which is where this differs
    ///     from <see cref="LabeledContent" /> on purpose: a row's caption names an element the row
    ///     does not own, and a legend is part of this control. The second assertion is the one that
    ///     says so — a group whose name came from somewhere else would still satisfy the first.
    /// </remarks>
    [Fact]
    public void A_group_is_one_node_named_by_its_legend() {
        var (fixture, box) = Group();

        using (fixture) {
            Assert.Equal(AccessibleRole.Group, box.Role);
            Assert.Equal("Shadows", box.AccessibleName);
            Assert.Null(box.AccessibleRelationTarget(AccessibleRelation.LabelledBy));

            // One string: the words the reader hears and the words on screen are the same property.
            Assert.Equal("Shadows", box.Legend.Text);

            box.Label = "Ambient occlusion";
            fixture.Update();

            Assert.Equal("Ambient occlusion", box.AccessibleName);
            Assert.Equal("Ambient occlusion", box.Legend.Text);
        }
    }

    /// <summary>A group with no caption is still a group, and draws no empty line.</summary>
    /// <remarks>
    ///     ⚠ <b>The role does not move under the property</b>, and the coverage sweep is why it must
    ///     not: it builds one bare instance of every public element type and reads the answer once,
    ///     so a role that only appeared after a caption was set would be invisible to it. What
    ///     <i>is</i> conditional is the legend element, for <c>LabeledContent.Message</c>'s reason —
    ///     an empty flex item still takes the column's gap.
    /// </remarks>
    [Fact]
    public void An_unnamed_group_is_still_a_group_and_shows_no_legend() {
        var (fixture, box) = Group(null);

        using (fixture) {
            Assert.Equal(AccessibleRole.Group, box.Role);
            Assert.Null(box.AccessibleName);
            Assert.Equal("none", box.Legend.GetStyle("display"));

            box.Label = "Fog";
            fixture.Update();

            Assert.Equal("flex", box.Legend.GetStyle("display"));
        }
    }

    /// <summary>Controls added to it land inside, and are not named by it.</summary>
    /// <remarks>
    ///     ⚠ <b>The second half is the one worth asserting.</b> A group's legend names the
    ///     <i>group</i>; a field inside it still has no name of its own, and reporting one from here
    ///     would announce four fields all called "Shadows" and hide exactly the defect
    ///     <see cref="LabeledContent" /> exists to fix. The group is the context, the row is the
    ///     name, and they compose.
    /// </remarks>
    [Fact]
    public void A_control_inside_lands_in_the_content_and_keeps_its_own_anonymity() {
        var (fixture, box) = Group();

        using (fixture) {
            var field = box.Content.Add<TextBox>();
            fixture.Update();

            Assert.Same(box.Content, field.Parent);
            Assert.Null(field.AccessibleName);
        }
    }
}
