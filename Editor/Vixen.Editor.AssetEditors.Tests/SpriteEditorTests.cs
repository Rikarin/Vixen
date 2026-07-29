// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.Assets.Textures;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>What the sprite editor does to a texture's sidecar.</summary>
/// <remarks>
///     ⚠ <b>Everything here goes through the texture's own document.</b> A slice is rects written
///     into the texture's import settings, so the sprite editor shares that document's undo stack and
///     dirty flag rather than opening a second document over the same <c>.meta</c> — which would be
///     two undo histories over one set of bytes.
/// </remarks>
public class SpriteDocumentTests {
    static SpriteRect Rect(string name, int x, int y, int width = 32, int height = 32) =>
        new() { Name = name, X = x, Y = y, Width = width, Height = height };

    static TextureImportDocument Open(EditorFixture project, string name = "Assets/sheet.png") =>
        new(project.Project, AssetId.New(), project.WriteAsset(name, "bytes"));

    [Fact]
    public void SlicingIsOneUndoStepHoweverManySpritesItProduced() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.SetSprites([Rect("a", 0, 0), Rect("b", 32, 0), Rect("c", 64, 0)]);

        Assert.Equal(3, document.Sprites.Count);

        // The author's model is "I sliced it, that was wrong, undo" — sixty rects as sixty commands
        // would be sixty undos.
        Assert.True(document.Stack.Undo());
        Assert.Empty(document.Sprites);

        Assert.True(document.Stack.Redo());
        Assert.Equal(3, document.Sprites.Count);
    }

    [Fact]
    public void EditingOneSpriteRepeatedlyMergesIntoOneStep() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.SetSprites([Rect("a", 0, 0)]);

        // A drag across a canvas is three hundred edits of one rect, and each of them arriving as its
        // own undo entry is what a merging command exists to prevent.
        for (var x = 1; x <= 20; x++) {
            document.UpdateSprite(0, Rect("a", x, 0));
        }

        Assert.Equal(20, document.Sprites[0].X);

        document.Stack.Undo();

        // Back to where the rect was before the drag started, not to one step along it.
        Assert.Equal(0, document.Sprites[0].X);
        Assert.Single(document.Sprites);
    }

    [Fact]
    public void EditsToDifferentSpritesDoNotMerge() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.SetSprites([Rect("a", 0, 0), Rect("b", 32, 0)]);

        document.UpdateSprite(0, Rect("a", 4, 0));
        document.UpdateSprite(1, Rect("b", 40, 0));

        // Two rects moved is two edits. Merging on the document alone would make dragging one rect
        // and then another undo both at once.
        document.Stack.Undo();

        Assert.Equal(4, document.Sprites[0].X);
        Assert.Equal(32, document.Sprites[1].X);
    }

    [Fact]
    public void SealingEndsAMergeRunTheWayADragEnds() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.SetSprites([Rect("a", 0, 0)]);

        document.UpdateSprite(0, Rect("a", 4, 0));
        document.Stack.Seal();
        document.UpdateSprite(0, Rect("a", 8, 0));

        document.Stack.Undo();

        Assert.Equal(4, document.Sprites[0].X);
    }

    [Fact]
    public void AddingAndRemovingAreUndoable() {
        using var project = new EditorFixture();
        var document = Open(project);

        Assert.Equal(0, document.AddSprite(Rect("a", 0, 0)));
        Assert.Equal(1, document.AddSprite(Rect("b", 32, 0)));

        Assert.True(document.RemoveSprite(0));
        Assert.Equal("b", Assert.Single(document.Sprites).Name);

        document.Stack.Undo();

        Assert.Equal(2, document.Sprites.Count);
        Assert.False(document.RemoveSprite(7));
    }

    [Fact]
    public void EveryChangeRaisesOneSignal() {
        using var project = new EditorFixture();
        var document = Open(project);

        var raised = 0;
        document.SpritesChanged += _ => raised++;

        document.SetSprites([Rect("a", 0, 0)]);
        document.UpdateSprite(0, Rect("a", 4, 0));
        document.Stack.Undo();

        // The panel redraws the overlay either way — a rect that moved and a rect that appeared are
        // the same amount of work — so one signal covers all of it, including the undo.
        Assert.Equal(3, raised);
    }

    [Fact]
    public void TheSpritesReachTheSidecarAndComeBack() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Texture.SpriteMode = SpriteMode.Multiple;
        document.Texture.PixelsPerUnit = 32f;

        document.SetSprites([
            new() {
                Name = "hero_0",
                X = 0, Y = 0, Width = 32, Height = 48,
                PivotX = 0.5f, PivotY = 0f,
                BorderLeft = 4, BorderTop = 4, BorderRight = 4, BorderBottom = 4
            }
        ]);

        document.Save();

        var reopened = new TextureImportDocument(project.Project, document.Asset, document.AssetPath);
        var sprite = Assert.Single(reopened.Sprites);

        Assert.Equal(SpriteMode.Multiple, reopened.Texture.SpriteMode);
        Assert.Equal(32f, reopened.Texture.PixelsPerUnit);
        Assert.Equal("hero_0", sprite.Name);
        Assert.Equal(new Rectangle(0f, 0f, 32f, 48f), sprite.Region);
        Assert.Equal(new Vector2(0.5f, 0f), sprite.Pivot);
        Assert.Equal(NineSlice.Uniform(4f), sprite.Border);
    }

    [Fact]
    public void ATextureWithNoSpritesStillWritesTheKeysAsEveryOtherSettingDoes() {
        using var project = new EditorFixture();
        var yaml = Open(project).ToYaml();

        // ⚠ Worth pinning rather than assuming, because the obvious wish is the other way round:
        // most textures in a project are not sprite sheets, so it is tempting to leave the keys out
        // of theirs. The emitter writes every member of the settings type — `maxSize: 0` is in every
        // sidecar too — and a sprite list that was special-cased out would be one member of one
        // importer behaving differently from the other four, for three lines of file.
        Assert.Contains("spriteMode: None", yaml, StringComparison.Ordinal);
        Assert.Contains("sprites:", yaml, StringComparison.Ordinal);
    }
}

/// <summary>What the sprite editor panel builds and what pressing its buttons does.</summary>
public class SpriteViewTests {
    /// <summary>A solid sheet, as a real PNG the view can decode.</summary>
    /// <remarks>
    ///     A real image rather than a stub, because the panel's whole first act is decoding the file
    ///     it was pointed at — a fake would test the fake.
    /// </remarks>
    static string Sheet(EditorFixture project, string name = "Assets/sheet.png") {
        var path = project.Paths.Absolute(name);
        var pixels = new byte[128 * 64 * 4];

        Array.Fill(pixels, (byte) 255);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Assets.Tests.MinimalPng.Write(128, 64, pixels));

        return path;
    }

    static (SpriteSheetView View, TextureImportDocument Document) Build(ViewHarness harness) {
        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), Sheet(harness.Project));
        var view = harness.Ui.Document.Root.Add<SpriteSheetView>();

        view.Show(document);
        harness.Ui.Frame();

        return (view, document);
    }

    [Fact]
    public void SlicingPutsARectOnTheOverlayForEverySprite() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        view.CellWidth.Number = 32;
        view.CellHeight.Number = 32;

        Assert.Equal(8, view.Slice());
        harness.Ui.Frame();

        Assert.Equal(8, document.Sprites.Count);
        Assert.Equal(8, view.Rects.Count);

        // ⚠ The mode follows the slice. A texture somebody has just cut into eight frames is a sprite
        // sheet whatever its settings said a moment ago, and leaving it at None would produce no
        // sub-assets from rects the panel is showing.
        Assert.Equal(SpriteMode.Multiple, document.Texture.SpriteMode);
    }

    [Fact]
    public void EveryRectIsPositionedInTexelsTimesTheZoom() {
        using var harness = new ViewHarness();
        var (view, _) = Build(harness);

        view.CellWidth.Number = 32;
        view.CellHeight.Number = 32;
        view.Slice();

        view.Zoom = 2f;
        harness.Ui.Frame();

        // Cell five is the second row's second column: texel (32, 32), so 64 pixels in at 2×.
        Assert.Equal(64f, view.Rects[5].Bounds.Left - view.Canvas.Bounds.Left, 1);
        Assert.Equal(64f, view.Rects[5].Bounds.Top - view.Canvas.Bounds.Top, 1);
        Assert.Equal(64f, view.Rects[5].Bounds.Width, 1);
    }

    [Fact]
    public void TheCanvasIsTheTextureTimesTheZoom() {
        using var harness = new ViewHarness();
        var (view, _) = Build(harness);

        harness.Ui.Frame();
        Assert.Equal(128f, view.Canvas.Bounds.Width, 1);

        view.Zoom = 0.5f;
        harness.Ui.Frame();

        Assert.Equal(64f, view.Canvas.Bounds.Width, 1);
    }

    [Fact]
    public void FittingNeverMagnifies() {
        using var harness = new ViewHarness();
        var (view, _) = Build(harness);

        view.FitTo(64f, 64f);
        Assert.Equal(0.5f, view.Zoom, 3);

        // A small sheet in a large panel stays at 1:1 rather than being blown up by the act of
        // opening the panel.
        view.FitTo(2000f, 2000f);
        Assert.Equal(1f, view.Zoom, 3);
    }

    [Fact]
    public void SelectingASpriteFillsTheFields() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        document.SetSprites([
            new() { Name = "hero", X = 8, Y = 12, Width = 20, Height = 24, PivotX = 0.25f, BorderLeft = 3 }
        ]);

        view.Select(0);
        harness.Ui.Frame();

        Assert.Equal("hero", view.Name.Value);
        Assert.Equal(8, view.RectX.Number);
        Assert.Equal(24, view.RectHeight.Number);
        Assert.Equal(0.25, view.PivotX.Number, 3);
        Assert.Equal(3, view.BorderLeft.Number);
    }

    [Fact]
    public void EditingAFieldWritesBackThroughTheDocument() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        document.SetSprites([new() { Name = "hero", X = 0, Y = 0, Width = 32, Height = 32 }]);
        view.Select(0);

        view.RectX.Number = 12;
        view.BorderTop.Number = 5;

        Assert.Equal(12, document.Sprites[0].X);
        Assert.Equal(5, document.Sprites[0].BorderTop);

        // Which means it is undoable, because it went through the document rather than around it.
        document.Stack.Undo();
        Assert.Equal(0, document.Sprites[0].X);
    }

    [Fact]
    public void RestatingTheFieldsDoesNotWriteBack() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        document.SetSprites([new() { Name = "a", X = 4, Y = 0, Width = 32, Height = 32 }]);
        view.Select(0);

        var depth = 0;

        // ⚠ Every field raises its change event when it is assigned, so restating ten of them from
        // the model without a guard would post ten edits back — the last built from fields that had
        // not been written yet.
        while (document.Stack.Undo()) {
            depth++;
        }

        Assert.Equal(1, depth);
    }

    [Fact]
    public void TheNineSliceGuidesAppearOnlyOnTheSelectedRect() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        document.SetSprites([
            new() { Name = "panel", X = 0, Y = 0, Width = 32, Height = 32, BorderLeft = 8, BorderRight = 8 },
            new() { Name = "plain", X = 32, Y = 0, Width = 32, Height = 32 }
        ]);

        view.Select(0);
        harness.Ui.Frame();

        // Two borders, two guides — and the unselected rect shows none, because guides are what the
        // selection is for rather than decoration on every box.
        Assert.Equal(2, view.Rects[0].Children.Count(child => string.Equals(child.Tag, "sprite-guide", StringComparison.Ordinal)));
        Assert.DoesNotContain(view.Rects[1].Children, child => string.Equals(child.Tag, "sprite-guide", StringComparison.Ordinal));
    }

    [Fact]
    public void ABorderReachingTheFarEdgeDrawsNoGuide() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        // A border as wide as the sprite is the rect's own outline, and one wider is a number
        // somebody is still typing.
        document.SetSprites([
            new() { Name = "panel", X = 0, Y = 0, Width = 32, Height = 32, BorderLeft = 32, BorderTop = 40 }
        ]);

        view.Select(0);
        harness.Ui.Frame();

        Assert.DoesNotContain(view.Rects[0].Children, child => string.Equals(child.Tag, "sprite-guide", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingKeepsTheSelectionInsideTheList() {
        using var harness = new ViewHarness();
        var (view, document) = Build(harness);

        document.SetSprites([Rect("a"), Rect("b"), Rect("c")]);
        view.Select(2);

        document.RemoveSprite(2);
        harness.Ui.Frame();

        // The overlay is rebuilt from a list one shorter, so a selection left where it was would
        // point past the end of it.
        Assert.Equal(1, view.Selected);
        Assert.Equal(2, view.Rects.Count);

        static SpriteRect Rect(string name) => new() { Name = name, X = 0, Y = 0, Width = 32, Height = 32 };
    }

    [Fact]
    public void ATextureNothingCanDecodeSaysSoAndRefusesToSlice() {
        using var harness = new ViewHarness();

        // Not a PNG. The settings are still worth editing, so the panel opens and says it has no
        // pixels rather than refusing to open at all.
        var path = harness.Project.WriteAsset("Assets/broken.png", "not an image");
        var document = new TextureImportDocument(harness.Project.Project, AssetId.New(), path);

        var view = harness.Ui.Document.Root.Add<SpriteSheetView>();
        view.Show(document);
        harness.Ui.Frame();

        Assert.False(view.Unavailable.HasClass("hidden"));
        Assert.True(view.SliceButton.Disabled);
        Assert.Equal(0, view.Slice());
    }

    [Fact]
    public void TheGridFieldsAreOffWhenTheMethodDoesNotReadThem() {
        using var harness = new ViewHarness();
        var (view, _) = Build(harness);

        Assert.False(view.CellWidth.Disabled);

        view.Method.Value = "automatic";
        harness.Ui.Frame();

        // Automatic reads the alpha, not the grid — so the fields that do nothing are disabled
        // rather than left looking as though they apply.
        Assert.True(view.CellWidth.Disabled);
        Assert.True(view.PaddingX.Disabled);
        Assert.True(view.KeepEmptyToggle.Disabled);
    }

    [Fact]
    public void TheTextureEditorCarriesTheSpritePanelOverTheSameDocument() {
        using var harness = new ViewHarness();
        var document = new TextureImportDocument(
            harness.Project.Project,
            AssetId.New(),
            Sheet(harness.Project, "Assets/tiles.png")
        );

        var view = harness.Ui.Document.Root.Add<TextureImportView>();

        view.Show(document);
        harness.Ui.Frame();

        Assert.Equal(2, view.Tabs.Items.Count);

        view.Sprites.CellWidth.Number = 64;
        view.Sprites.CellHeight.Number = 64;

        Assert.Equal(2, view.Sprites.Slice());

        // One document, one undo stack: the slice the sprite tab made is undone by the texture tab's
        // Ctrl+Z, because there is only one of them.
        Assert.True(document.Stack.Undo());
        Assert.Empty(document.Sprites);
    }
}
