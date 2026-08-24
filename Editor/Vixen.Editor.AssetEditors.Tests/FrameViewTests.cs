// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Frame;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The frame panel's quality table, and the sentence that stands in for an empty one.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The branch none of wave 7's six dumped states crossed, and the bug that hid in
///         it.</b> The port first wrote the empty-table arm as <c>@if (QualityRows == 0)</c>.
///         <c>QualityRows</c> is a plain <c>int</c> — a public counter the panel promises — so the
///         arm registered <b>no signal dependency at all</b>: <c>BuildContext.Switch</c> wraps its
///         condition in a <c>Bind</c>, and a condition that reads no signal is evaluated once and
///         never again. It would also have been evaluated wrongly, because <c>Show</c> runs before
///         the first flush, so the table was already full when the arm was first picked and the
///         sentence could never have appeared however empty the table later got.
///     </para>
///     <para>
///         ⚠ <b>The dumps could not see it and that is the point of this file.</b> Every one of the
///         six states had knobs in the table, so the arm was never crossed and the ported panel
///         matched the hand-written one to the byte while carrying a binding that could not work.
///         "All six states matched" is a claim about six states. Reading it as a claim about the
///         panel is how a dead branch ships.
///     </para>
///     <para>
///         The fix is one word — <c>QualityKnobs.Length == 0</c>, which reads the signal — and the
///         two are always equal, because a group heading is only ever added immediately before a row.
///     </para>
/// </remarks>
public class FrameViewTests {
    /// <summary>A frame whose knobs are all engine defaults, so "only what is overridden" is empty.</summary>
    const string Plain = """
        version: 2
        game: !StandardFrame
          name: TheFrame
        """;

    const string Knobs = """
        version: 2
        game: !StandardFrame
          name: TheFrame
          tier: High
          shadows: Cascades
        """;

    static StandardFrameView Open(ViewHarness harness, string text, string name) {
        var path = harness.Project.Write("Assets/" + name, text);
        var document = new StandardFrameDocument(harness.Project.Project, AssetId.New(), path);
        var view = harness.Ui.Document.Root.Add<StandardFrameView>();

        view.Show(document);
        harness.Ui.Frames(3);

        return view;
    }

    /// <summary>
    ///     ⚠ By label, not by type. The two <c>InspectorView</c>s above this control draw the frame's
    ///     own boolean settings as check boxes, so "the first <c>CheckBox</c> in the tree" is one of
    ///     theirs and ticking it edits the document instead of filtering the table.
    /// </summary>
    static CheckBox Filter(UiElement root) {
        if (root is CheckBox box && box.Label == "Only what is overridden") {
            return box;
        }

        foreach (var child in root.Children) {
            if (Filter(child) is { } found) {
                return found;
            }
        }

        return null!;
    }

    /// <summary>A table with rows in it shows the rows and not the sentence.</summary>
    [Fact]
    public void AQualityTableWithRowsShowsNoEmptyStateSentence() {
        using var harness = new ViewHarness();
        var view = Open(harness, Knobs, "Frame.vxcompositor");

        Assert.True(view.QualityRows > 0);
        Assert.DoesNotContain(view.Quality.Children, child => child.Tag == "text");
    }

    /// <summary>
    ///     ⚠ Filtering an all-default frame to "only what is overridden" empties the table, and the
    ///     sentence has to arrive — which the original binding could not have done.
    /// </summary>
    [Fact]
    public void FilteringToNothingShowsTheEmptyStateSentence() {
        using var harness = new ViewHarness();
        var view = Open(harness, Plain, "Plain.vxcompositor");
        var filter = Filter(view);

        Assert.NotNull(filter);

        filter.IsChecked = true;
        harness.Ui.Frames(3);

        Assert.Equal(0, view.QualityRows);

        var sentence = Assert.Single(view.Quality.Children, child => child.Tag == "text");
        Assert.Equal("Nothing above the engine table states a value for this tier.", sentence.Text);
    }

    /// <summary>And unfiltering takes the sentence away again, which is the other direction.</summary>
    [Fact]
    public void UnfilteringTakesTheSentenceAwayAgain() {
        using var harness = new ViewHarness();
        var view = Open(harness, Plain, "Plain.vxcompositor");
        var filter = Filter(view);

        filter.IsChecked = true;
        harness.Ui.Frames(3);

        Assert.Contains(view.Quality.Children, child => child.Tag == "text");

        filter.IsChecked = false;
        harness.Ui.Frames(3);

        Assert.True(view.QualityRows > 0);
        Assert.DoesNotContain(view.Quality.Children, child => child.Tag == "text");
    }
}
