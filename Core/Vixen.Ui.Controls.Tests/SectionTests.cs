// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Composition;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The section: a region a screen reader can jump to, named by a heading it can also jump to.</summary>
/// <remarks>
///     ⚠ <b>The subject is two nodes and the one string between them.</b> A section is reachable by
///     landmark and by heading, so the tree has a <c>region</c> and a <c>heading</c>; the region's
///     name is read off the heading through <c>LabelledBy</c>, so the words on screen and the words
///     announced for both are one property. Every test here is about the tree or about the one theme
///     rule, because a section draws nothing but its title.
/// </remarks>
public class SectionTests {
    static (ControlFixture Fixture, Section Section) Titled(string? title = "Audio") {
        var fixture = new ControlFixture();
        var section = fixture.Add<Section>();

        section.Title = title;
        fixture.Update();

        return (fixture, section);
    }

    /// <summary>
    ///     ⚠ <b>A region named by its heading, and a heading a reader can navigate to, as the tree
    ///     hands them to a screen reader.</b>
    /// </summary>
    /// <remarks>
    ///     Asserted on the rendered tree rather than on the properties alone: a region whose name was
    ///     right and whose heading was not a node would satisfy <c>AccessibleName</c> and leave heading
    ///     navigation with nothing to find.
    /// </remarks>
    [Fact]
    public void A_section_is_a_region_named_by_a_heading_that_is_in_the_tree() {
        var (fixture, section) = Titled();

        using (fixture) {
            var volume = section.Content.Add<Slider>();
            volume.AccessibleName = "Volume";
            fixture.Update();

            Assert.Equal(AccessibleRole.Region, section.Role);
            Assert.Equal(AccessibleRole.Heading, section.Heading.Role);
            Assert.Same(section.Heading, section.AccessibleRelationTarget(AccessibleRelation.LabelledBy));

            var lines = AccessibilitySnapshot.Render(section).Split('\n');

            Assert.Equal("region \"Audio\"", lines[0]);
            Assert.Equal("  heading \"Audio\"", lines[1]);
            Assert.StartsWith("  slider \"Volume\"", lines[2], StringComparison.Ordinal);
            Assert.Equal(3, lines.Length);

            // One string: retitling moves the heading's words and the region's name together.
            section.Title = "Sound";
            fixture.Update();

            Assert.Equal("Sound", section.Heading.Text);
            Assert.Equal("Sound", section.AccessibleName);
        }
    }

    /// <summary>
    ///     ⚠ <b>With no title it is still a region, it has no heading at all, and the gate says it is
    ///     unnamed.</b>
    /// </summary>
    /// <remarks>
    ///     The region stays so that <c>AccessibilitySnapshot.Unnamed</c> can report it — a role that
    ///     vanished with the title would take the omission with it. The heading does go: the tree
    ///     walks hidden parts, so an empty one left as a heading would be announced as one with no
    ///     name. An explicit <c>AccessibleName</c> is the landmark-without-a-visible-heading case and
    ///     satisfies the gate.
    /// </remarks>
    [Fact]
    public void An_untitled_section_is_an_unnamed_region_with_no_heading() {
        var (fixture, section) = Titled(null);

        using (fixture) {
            Assert.Equal(AccessibleRole.Region, section.Role);
            Assert.Equal(AccessibleRole.None, section.Heading.Role);
            Assert.Equal("none", section.Heading.GetStyle("display"));
            Assert.Equal("region", AccessibilitySnapshot.Render(section));

            Assert.Equal(["<section> is a region and has no accessible name"], AccessibilitySnapshot.Unnamed(section));

            section.AccessibleName = "Audio";
            Assert.Empty(AccessibilitySnapshot.Unnamed(section));

            section.AccessibleName = null;
            Assert.NotEmpty(AccessibilitySnapshot.Unnamed(section));

            section.Title = "Audio";
            fixture.Update();

            Assert.Empty(AccessibilitySnapshot.Unnamed(section));
            Assert.Equal("flex", section.Heading.GetStyle("display"));

            // ⚠ And taking the title away takes the heading with it. The start of this test never
            // ran the title's change hook — null to null is no change — so this is the leg that
            // proves the hook, rather than the constructor, decides the heading's role.
            section.Title = null;
            fixture.Update();

            Assert.Equal(AccessibleRole.None, section.Heading.Role);
            Assert.Equal("none", section.Heading.GetStyle("display"));
            Assert.Equal("region", AccessibilitySnapshot.Render(section));
        }
    }

    /// <summary>A tag nested in markup lands in the content, under the heading, and is not named by the section.</summary>
    /// <remarks>
    ///     <para>
    ///         Through markup, because that is the route <c>ContentHost</c> decides: a nested tag goes
    ///         to the content host. Since #1425 so does C#'s <c>section.Add&lt;T&gt;()</c>; the C#
    ///         lines in this file say <c>section.Content</c> because they were written before it did.
    ///     </para>
    ///     <para>
    ///         And not named by it: the title is context for everything under it, and a field that
    ///         answered the section's name as its own would be a column of fields all called "Audio".
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_nested_tag_lands_in_the_content_and_keeps_its_own_anonymity() {
        using var ui = ControlHarness.Open(400f, 300f);
        var sheet = new SectionSheet { Heading = "Audio" };

        BuildContext.BuildInto(sheet, ui.Document, ui.Document.Root);
        ui.Frame();

        Assert.Same(sheet.Audio.Content, sheet.Slider.Parent);
        Assert.Null(sheet.Slider.AccessibleName);
        Assert.Equal("Audio", sheet.Audio.AccessibleName);
        Assert.True(sheet.Slider.AbsoluteTop >= sheet.Audio.Heading.AbsoluteTop + sheet.Audio.Heading.Height);
    }

    /// <summary>The heading is above the content, and the content is a column: the one theme rule it needs.</summary>
    /// <remarks>
    ///     An element nothing styles lays its children out in a row, which would put the title beside
    ///     the first control and every control beside the one before it.
    /// </remarks>
    [Fact]
    public void A_section_stacks_its_heading_over_a_column_of_controls() {
        var (fixture, section) = Titled();

        using (fixture) {
            var first = section.Content.Add<TextBox>();
            var second = section.Content.Add<TextBox>();
            fixture.Update();

            Assert.True(
                section.Content.AbsoluteTop >= section.Heading.AbsoluteTop + section.Heading.Height,
                $"the content starts at {section.Content.AbsoluteTop}, inside the heading ({section.Heading.AbsoluteTop} + {section.Heading.Height})"
            );

            Assert.Equal(first.AbsoluteLeft, second.AbsoluteLeft);
            Assert.True(second.AbsoluteTop >= first.AbsoluteTop + first.Height);
        }
    }
}
