// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A side column's one-sentence summary spans the column (#1449).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The summary was a <c>fact-name</c> in a <c>fact-row</c> with no <c>fact-value</c></b>,
///         and <c>fact-name</c> is the label half of a pair: <c>width: 40%</c>, muted. So the sentence
///         wrapped at about 120 px of a 300 px column, with the other 60 % empty, in the label's grey
///         although it is the content. The animation clip and move set editors were the two named;
///         the census found the proxy shape, shape vocabulary and sequence editors built theirs the
///         same way.
///     </para>
///     <para>
///         Read off the laid-out boxes rather than the tree's tags, so a summary that moved into some
///         other element still sized like a label would fail here too.
///     </para>
/// </remarks>
public class SummaryLineTests {
    [Fact]
    public void The_animation_clip_summary_spans_the_column() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/walk.vxanim", string.Empty);
        var view = harness.Ui.Document.Root.Add<AnimationClipView>();

        view.Show(new AnimationClipDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        SpansTheColumn(view.Fields, "0 object(s), 0 key(s), 0 event(s)");
    }

    [Fact]
    public void The_move_set_summary_spans_the_column() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/locomotion.vxmoveset", string.Empty);
        var view = harness.Ui.Document.Root.Add<MoveSetView>();

        view.Show(new MoveSetDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        SpansTheColumn(view.Fields, "0 move(s), 0 rule(s), 0 overlay(s)");
    }

    [Fact]
    public void The_proxy_shape_summary_spans_the_column() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/hero.vxproxyshapes", string.Empty);
        var view = harness.Ui.Document.Root.Add<ProxyShapeView>();

        view.Show(new ProxyShapeDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        SpansTheColumn(view.Fields, "0 shape(s), 0 coarse");
    }

    [Fact]
    public void The_shape_vocabulary_summary_spans_the_column() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/humanoid.vxshapevocab", string.Empty);
        var view = harness.Ui.Document.Root.Add<ShapeVocabularyView>();

        view.Show(new ShapeVocabularyDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        SpansTheColumn(view.Fields, "0 name(s), 0 tag(s), 0 class(es)");
    }

    [Fact]
    public void The_sequence_summary_spans_the_column() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/intro.vxseq", string.Empty);
        var view = harness.Ui.Document.Root.Add<SequenceView>();

        view.Show(new SequenceDocument(harness.Project.Project, AssetId.New(), path));
        harness.Ui.Frame();

        SpansTheColumn(view.Fields, "0 track(s) · ");

        // And the no-scene line is a warning that reads as one: the whole row, not a muted label.
        var warning = Find(view.Fields, "No scene attached");

        Assert.True(warning.Width >= view.Fields.Width - 1f, $"the no-scene line is {warning.Width} px of a {view.Fields.Width} px column");
    }

    static void SpansTheColumn(UiElement fields, string text) {
        var summary = Find(fields, text);

        // The premise: the column is wide enough that 40 % of it is a visibly different box.
        Assert.True(fields.Width > 200f, $"the fields column is {fields.Width} px wide");

        Assert.True(
            summary.Width >= fields.Width - 1f,
            $"the summary \"{summary.Text}\" is {summary.Width} px of a {fields.Width} px column"
        );
    }

    static UiElement Find(UiElement root, string prefix) =>
        Descendants(root).FirstOrDefault(element => element.Text is { } text && text.StartsWith(prefix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"nothing in the column says '{prefix}…'");

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
