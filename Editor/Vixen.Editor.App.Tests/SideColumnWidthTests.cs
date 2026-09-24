// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Every asset editor's fields column scrolls down and never across — issue #1411.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A <c>scroll-content</c> is laid out at its content's max-content width</b>;
///         <c>min-width: 100%</c> is only its floor. #1275 turned the <c>*-side</c> columns into
///         <c>ScrollView</c>s, so one unwrapped line wider than the column widened the whole content,
///         put it behind a sideways bar and cut every line in the column at its right edge. The mixer
///         was capped by #1397; this holds the other eleven to the same thing, and the mixer as the
///         control that was already right.
///     </para>
///     <para>
///         The line is put in by the test rather than waited for, because what the column does with
///         one is a fact about the sheet and not about whichever message an editor happens to
///         show: a sentence far wider than 300 px in a flex row, appended to the column's own
///         content. With the cap it wraps and <see cref="ScrollView.MaximumLeft" /> is zero; without
///         it the content is as wide as the sentence, 1019 px in every column. Each column is drawn,
///         on both renderers when a device opens, when <c>VIXEN_PANEL_CAPTURE</c> names somewhere to
///         put the pictures.
///     </para>
///     <para>
///         ⚠ <b>The issue's "any unwrapped line" is narrower than that, and was measured to be.</b> A
///         bare line of text appended to the column wrapped in all twelve with no cap at all; it is a
///         line inside a <i>row</i> that widens the content, because a flex row measured "at most"
///         a width reports its content's size rather than shrinking its items. And three columns did
///         not need the test's line: the shader graph, VFX and animation graph columns were 306,
///         284 and 178 px over on a freshly created asset, so the first assertion is theirs.
///     </para>
/// </remarks>
public sealed class SideColumnWidthTests {
    const string Sentence =
        "A line far wider than any fields column is ever docked at, which has to wrap inside the column "
        + "rather than widen what the column scrolls.";

    [Theory]
    [InlineData("assets.create-animation", "animation-side")]
    [InlineData("assets.create-animation-graph", "animgraph-side")]
    [InlineData("assets.create-input", "input-side")]
    [InlineData("assets.create-font", "font-side")]
    [InlineData("assets.create-sequence", "sequence-side")]
    [InlineData("assets.create-shader-graph", "shadergraph-side")]
    [InlineData("assets.create-vfx", "vfx-side")]
    [InlineData("assets.create-proxy-shapes", "shape-side")]
    [InlineData("assets.create-shape-vocabulary", "vocab-side")]
    [InlineData("assets.create-harness", "harness-side")]
    [InlineData("assets.create-move-set", "moveset-side")]
    [InlineData("assets.create-mixer", "mixer-side")]
    public void A_line_wider_than_the_fields_column_wraps_inside_it(string command, string tag) {
        using var fixture = ScrollingPanelPictureTests.Start();

        fixture.Run(command).Settle();
        fixture.Frames(2);

        var side = Descendants(fixture.Document.Root).OfType<ScrollView>().SingleOrDefault(view => view.Tag == tag)
            ?? throw fixture.Fail($"{command} opened no ScrollView under <{tag}>");

        var natural = side.MaximumLeft;
        var widest = Widest(side);

        // The column as the editor leaves it, before the test's line, so that a picture of the sheet
        // with and without the cap compares real content rather than a column the line has widened.
        Draw(fixture, tag + "-natural");

        // ⚠ In a row, the way every field row in these columns holds its words — a caption and a
        // value, a stage and a message. A bare line of text in the column WRAPS without any cap, and
        // was measured to: a flex row measured "at most" a width reports its content's size instead
        // of shrinking its items, so it is the row and not the text that widens the column.
        var row = side.Content.Add("div");
        row.SetStyle("flex-direction", "row");

        var line = row.Add("div");
        line.SetStyle("flex-grow", "1");
        line.Text = Sentence;
        fixture.Frames(2);

        Draw(fixture, tag);

        Assert.True(side.Width > 0f, $"<{tag}> is not on screen, so its width says nothing.");

        Assert.True(
            natural == 0f,
            $"<{tag}>'s own content is {natural:0} px wider than the {side.Width:0} px column before anything is added: "
            + $"{widest} (#1411)."
        );

        Assert.True(
            side.MaximumLeft == 0f,
            $"<{tag}>'s content is {side.Content.Width:0} px in a {side.Width:0} px column, so its lines run under a "
            + $"sideways bar: the line is {line.Width:0} px wide (#1411)."
        );

        Assert.True(
            line.Block()!.Lines.Length > 1,
            $"the line is one line {line.Width:0} px wide in a {side.Width:0} px column, so it was cut rather than wrapped."
        );
    }

    static void Draw(EditorSession fixture, string tag) {
        if (Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE") is not { Length: > 0 }) {
            return;
        }

        var (width, height) = (ScrollingPanelPictureTests.WidthOf(fixture), ScrollingPanelPictureTests.HeightOf(fixture));

        using var device = ScrollingPanelPictureTests.OpenDevice();
        using var gpu = device is null ? null : new ScrollingPanelPictureTests.GpuPicture(device, width, height);

        ScrollingPanelPictureTests.Draw(fixture, gpu, tag);
    }

    /// <summary>What reaches furthest right in the column's content, and the chain above it, for the message.</summary>
    /// <remarks>Read eagerly, because the line this test adds would otherwise be what it reports.</remarks>
    static string Widest(ScrollView side) {
        var widest = Descendants(side.Content)
            .Where(element => !ReferenceEquals(element, side.Content))
            .MaxBy(element => element.AbsoluteLeft + element.Width);

        if (widest is null) {
            return "the content is empty";
        }

        var chain = new List<string>();

        for (var element = widest; element is not null && !ReferenceEquals(element, side.Content); element = element.Parent) {
            chain.Add($"{element.Tag}({element.Width:0})");
        }

        chain.Reverse();

        return $"<{widest.Tag}> reaches {widest.AbsoluteLeft + widest.Width - side.AbsoluteLeft:0} px, '{widest.Text}', "
            + $"under {string.Join(" > ", chain)}";
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        yield return element;

        foreach (var child in element.Children) {
            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }
}
