// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Ui;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A toolbar's label sits against the field it names (#1465).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The bars labelled their fields with a bare <c>fact-name</c></b>, which is the label
///         column of a name/value pair: <c>width: 40%</c> of whatever holds it. In a 1200 px bar
///         that is 480 px per one-word label, so the move set's "Query" ended 480 px from its box
///         and the clip bar's three labels wrapped it onto two lines with "Length" ending one line
///         and its box starting the next. The census of every <c>fact-name</c> outside a
///         <c>fact-row</c> found exactly the six labels here, in four bars.
///     </para>
///     <para>
///         Read off the laid-out boxes, so a label that moved into some other element still sized
///         like a column would fail too: each label must end left of its field, on the same line,
///         and no further from it than the bar's gap and a little.
///     </para>
/// </remarks>
public class BarLabelTests {
    /// <summary>More than the bars' 6 px gap and less than any column-sized label could be.</summary>
    const float Adjacent = 16f;

    /// <summary>Wider than "Length" in the editor's face, and far narrower than 40 % of any bar.</summary>
    const float Word = 80f;

    /// <summary>A harness with the editor's own face installed, so a label measures as its word.</summary>
    /// <returns>The harness.</returns>
    internal static ViewHarness Opened() {
        var harness = new ViewHarness();
        var face = FontFace.Load(File.ReadAllBytes(TypefacePath()), name: "OpenSans");

        harness.Ui.Document.Fonts.Register(face.Name, face);
        harness.Ui.Document.Fonts.Default = face;

        return harness;
    }

    [Fact]
    public void The_animation_clip_bar_labels_sit_against_their_fields() {
        using var harness = Opened();
        var path = harness.Project.WriteAsset("Assets/walk.vxanim", string.Empty);
        var view = harness.Ui.Document.Root.Add<AnimationClipView>();

        view.Show(new AnimationClipDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        Labels(view.Duration, "Length");
        Labels(view.FrameRate, "FPS");
        Labels(view.Wrap, "Wrap");
    }

    [Fact]
    public void The_move_set_bar_label_sits_against_its_query_box() {
        using var harness = Opened();
        var path = harness.Project.WriteAsset("Assets/locomotion.vxmoveset", string.Empty);
        var view = harness.Ui.Document.Root.Add<MoveSetView>();

        view.Show(new MoveSetDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        Labels(view.Filter, "Query");
    }

    [Fact]
    public void The_animation_graph_bar_label_sits_against_its_layer_select() {
        using var harness = Opened();
        var path = harness.Project.WriteAsset("Assets/hero.vxanimgraph", string.Empty);
        var view = harness.Ui.Document.Root.Add<AnimationGraphView>();

        view.Show(new AnimationGraphDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        Labels(view.Layers, "Layer");
    }

    [Fact]
    public void The_sequence_bar_label_sits_against_its_length_box() {
        using var harness = Opened();
        var path = harness.Project.WriteAsset("Assets/intro.vxseq", string.Empty);
        var view = harness.Ui.Document.Root.Add<SequenceView>();

        view.Show(new SequenceDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        Labels(view.Duration, "Length");
    }

    /// <summary>Asserts the element before a field is its label, ending just left of it on its line.</summary>
    static void Labels(UiElement field, string text) {
        var bar = field.Parent ?? throw new InvalidOperationException("the field is not in a bar");
        var at = IndexOf(bar, field);

        Assert.True(at > 0, $"nothing precedes the {text} field in its bar");

        var label = bar.Children[at - 1];

        Assert.Equal(text, label.Text);

        // The premises: the bar is wide enough that 40 % of it is a visibly different box, and the
        // label has a face to measure with — with none, every word is zero wide.
        Assert.True(bar.Width > 400f, $"the bar is {bar.Width} px wide");
        Assert.True(label.Width > 0f, $"\"{text}\" measured zero wide, so no face was installed");
        Assert.True(
            label.Width <= Word,
            $"\"{text}\" is {label.Width} px wide in a {bar.Width} px bar, which is a column and not a word"
        );

        var (l, f) = (label.Bounds, field.Bounds);
        var lineOf = (f.Y + (f.Height * 0.5f)) - (l.Y + (l.Height * 0.5f));

        Assert.True(
            MathF.Abs(lineOf) < f.Height * 0.5f,
            $"\"{text}\" is on a different line from its field: label at y {l.Y}, field at y {f.Y}"
        );

        var gap = f.X - (l.X + l.Width);

        Assert.True(
            gap is >= 0f and <= Adjacent,
            $"\"{text}\" is {l.Width} px wide and ends {gap} px from its field in a {bar.Width} px bar"
        );
    }

    static string TypefacePath() {
        const string Relative = "Editor/Vixen.Editor.App/Fonts/OpenSans-Regular.ttf";

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, Relative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(candidate)) {
                return candidate;
            }
        }

        throw new FileNotFoundException($"'{Relative}' was not found above '{AppContext.BaseDirectory}'.");
    }

    static int IndexOf(UiElement parent, UiElement child) {
        for (var index = 0; index < parent.Children.Count; index++) {
            if (ReferenceEquals(parent.Children[index], child)) {
                return index;
            }
        }

        return -1;
    }
}
