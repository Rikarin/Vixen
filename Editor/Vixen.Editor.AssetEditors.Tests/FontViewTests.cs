// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.AssetEditors.Fonts;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The font panel's write-back, driven through the controls rather than through the document.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The direction the port's whole-tree dumps could not reach.</b> Those drove the panel
///         with <c>document.Edit(…)</c> and compared the tree it produced, which exercises the
///         <i>binding</i> leg — a signal write landing on a control — and says nothing about the
///         <c>change:</c> leg, where a person moves a control and the document is supposed to move
///         once. Wave 7 ported these five fields out of imperative <c>CheckedChanged</c>/
///         <c>NumberChanged</c> handlers, so that leg is new code and had no test at all: this panel
///         had none before.
///     </para>
///     <para>
///         ⚠ <b>The instrument is the undo depth, and it has to be, because <c>FontDocument.Edit</c>
///         no-ops when the YAML does not change.</b> A write-back that looped — handler edits the
///         document, document raises <c>Changed</c>, <c>Reload</c> writes the settings signal, the
///         binding writes the control, the control's <c>change:</c> handler fires again — would
///         second-guess itself invisibly if the second write happened to land on the same value. The
///         stack counts what actually happened. This is wave 3's assertion for the mixer, in the same
///         words: opening leaves the stack at nought, and the very next move takes it to one.
///     </para>
/// </remarks>
public class FontViewTests {
    static FontAsset Asset =>
        new() { Name = "Inter", PixelSize = 32f, Padding = 2, AtlasWidth = 512, AtlasHeight = 512 };

    static T Find<T>(UiElement root) where T : UiElement {
        if (root is T found) {
            return found;
        }

        foreach (var child in root.Children) {
            if (Find<T>(child) is { } inside) {
                return inside;
            }
        }

        return null!;
    }

    /// <summary>⚠ Showing a font is not an edit, which is the half a loop would break first.</summary>
    [Fact]
    public void ShowingAFontLeavesTheUndoStackEmpty() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/inter.vxfont", Asset.ToYaml());
        var document = new FontDocument(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<FontView>();

        view.Show(document);
        harness.Ui.Frames(3);

        Assert.Equal(0, document.Stack.Depth.Value);
    }

    /// <summary>
    ///     ⚠ One tick of the box is one undo entry, and the value the document ends on is the one the
    ///     box now shows.
    /// </summary>
    /// <remarks>
    ///     The write-back cannot loop because a change made while effects are draining is not
    ///     reported — the rule <c>TextureImportView</c>'s channel bar rests on. This is what says so
    ///     for the fields wave 7 moved.
    /// </remarks>
    [Fact]
    public void TickingTheDistanceFieldBoxIsOneUndoableEdit() {
        using var harness = new ViewHarness();
        var asset = Asset;
        var path = harness.Project.WriteAsset("Assets/inter.vxfont", asset.ToYaml());
        var document = new FontDocument(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<FontView>();

        view.Show(document);
        harness.Ui.Frames(3);

        var was = document.Font.DistanceField;
        var box = Find<CheckBox>(view);

        Assert.NotNull(box);
        Assert.Equal(was, box.IsChecked);

        box.IsChecked = !was;
        harness.Ui.Frames(3);

        Assert.Equal(!was, document.Font.DistanceField);
        Assert.Equal(1, document.Stack.Depth.Value);

        // ⚠ And it stays put. A loop would land the second write on the original value and leave the
        // box disagreeing with the document, which is exactly what a depth of one cannot rule out on
        // its own.
        Assert.Equal(!was, box.IsChecked);
    }

    /// <summary>Typing a pixel size moves the document once and the panel follows it.</summary>
    [Fact]
    public void TypingAPixelSizeIsOneUndoableEdit() {
        using var harness = new ViewHarness();
        var path = harness.Project.WriteAsset("Assets/inter.vxfont", Asset.ToYaml());
        var document = new FontDocument(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<FontView>();

        view.Show(document);
        harness.Ui.Frames(3);

        var size = Find<NumericInput>(view);

        Assert.NotNull(size);
        Assert.Equal(32d, size.Number);

        size.Number = 64d;
        harness.Ui.Frames(3);

        Assert.Equal(64f, document.Font.PixelSize);
        Assert.Equal(1, document.Stack.Depth.Value);
        Assert.Equal(64d, size.Number);

        // Undo puts both back, because the panel is a view over the document and not a second copy.
        document.Stack.Undo();
        harness.Ui.Frames(3);

        Assert.Equal(32f, document.Font.PixelSize);
        Assert.Equal(32d, size.Number);
    }
}
