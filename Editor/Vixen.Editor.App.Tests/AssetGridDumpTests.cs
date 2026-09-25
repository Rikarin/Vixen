// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Imaging;
using Vixen.Editor.Core;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The asset grid's tile template, held to the panel the hand-written C# control built (#1406).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Recorded before the port and not after it</b>, which is the convention
///         <c>ConsoleViewDumpTests</c> and <c>MessageLogViewDumpTests</c> set: every reference in
///         <c>AssetGridDumps/</c> was produced by the C# <c>AssetGrid</c> whose tiles were filled
///         through <c>CreateTile</c>/<c>BindTile</c>, in the Project panel of a real editor, and the
///         port is required to reproduce it. States are reached through the interface — a folder is
///         entered by a double click on its tile, a tile chosen by a click, the size by the browser's
///         own picker.
///     </para>
///     <para>
///         ⚠ <b>Two dumps per state, because a tree dump is blind</b> to a tile's <c>Checked</c> bit,
///         which lives where only <c>UiTest.Flags</c> looks.
///     </para>
///     <para>
///         ⚠ <b>Found by caption rather than through <c>AssetGrid.TileOf</c></b>, so the harness says
///         the same thing about the control before the port and after it, whatever the port does to
///         the type a tile is.
///     </para>
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "UiTest.Flags reads nine properties by name; tests are not trimmed.")]
public sealed class AssetGridDumpTests {
    /// <summary>Where a run writes its dumps and pictures, when somebody asks it to.</summary>
    /// <remarks>Unset in every gate. It is how the references were taken from the pre-port build.</remarks>
    static readonly string? Record = Environment.GetEnvironmentVariable("VIXEN_ASSETGRID_RECORD");

    [Fact]
    public void The_grid_opens_on_the_project_root() {
        using var editor = Started();

        Check(editor, "root");
    }

    [Fact]
    public void A_double_click_walks_into_a_folder_and_rebinds_the_tiles_on_screen() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        Assert.Equal("Scenes", Grid(editor).Folder?.Name);
        Check(editor, "entered");
    }

    [Fact]
    public void A_click_chooses_a_tile_and_marks_it() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");
        editor.Click(TileElement(editor, "Main.vxscene"));
        editor.Settle();

        Assert.Single(editor.Project.Selection);
        Check(editor, "chosen");
    }

    [Fact]
    public void The_smallest_tiles_refit_the_columns() {
        using var editor = Started();

        Picker(editor).Value = "Small";
        editor.Settle();

        Assert.Equal("Small", Grid(editor).TileSize);
        Check(editor, "small");
    }

    /// <summary>A folder longer than the pool, scrolled to its end: the pool rebinds and parks.</summary>
    [Fact]
    public void A_long_folder_scrolled_to_its_end_shows_its_last_tiles() {
        using var editor = Started();

        var many = Path.Combine(editor.ProjectRoot, "Assets", "Many");

        Directory.CreateDirectory(many);

        for (var index = 0; index < 120; index++) {
            File.WriteAllText(Path.Combine(many, $"file{index:000}.png"), "x");
        }

        editor.Run("assets.refresh");
        DoubleClick(editor, "Many");

        var grid = Grid(editor);

        Assert.Equal(120, grid.Items.Count);

        grid.ScrollIntoView(119);
        editor.Settle();

        Assert.NotNull(TileElement(editor, "file119.png"));
        Check(editor, "many");
    }

    /// <summary>
    ///     ⚠ <b>Settle, then show another folder, and every tile on screen follows.</b> Walking into a
    ///     folder rewrites every slot with the index it already had — slot 0 shows item 0 in both
    ///     folders — so a template that read only the slot's <c>index</c> would keep showing the
    ///     previous folder's names.
    /// </summary>
    /// <remarks>
    ///     ⚠ The #758 review found that the dump harness was the only thing holding
    ///     <c>ConsoleView</c>'s reactivity, because every other test acted before the first frame; this
    ///     one and the next act after the grid has settled, and assert every realised tile rather than
    ///     the one a dump happens to show.
    /// </remarks>
    [Fact]
    public void Showing_another_folder_after_the_grid_settled_rebinds_every_tile_on_screen() {
        using var editor = Started();

        var many = Path.Combine(editor.ProjectRoot, "Assets", "Many");

        Directory.CreateDirectory(many);

        for (var index = 0; index < 30; index++) {
            File.WriteAllText(Path.Combine(many, $"file{index:00}.png"), "x");
        }

        editor.Run("assets.refresh");
        editor.Settle();

        var grid = Grid(editor);

        // Settled at the root, showing its folders: the predicate below is false here.
        Assert.Equal(grid.Items.Select(item => item.Name), grid.Tiles.Select(tile => tile.Caption.Text));
        Assert.DoesNotContain(grid.Tiles, tile => tile.Caption.Text == "file00.png");

        DoubleClick(editor, "Many");

        var tiles = Grid(editor).Tiles;

        Assert.True(tiles.Count >= 2, $"only {tiles.Count} tiles are realised");

        for (var item = 0; item < grid.Items.Count; item++) {
            if (grid.TileOf(item) is { } tile) {
                Assert.Equal(grid.Items[item].Name, tile.Caption.Text);
                Assert.Same(grid.Items[item], tile.Node);
            }
        }

        Assert.Equal(tiles.Count, tiles.Select(tile => tile.Caption.Text).Distinct().Count());
        Assert.All(tiles, tile => Assert.StartsWith("file", tile.Caption.Text, StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ <b>Settle, then a picture arrives, and <c>Refresh</c> puts it on the tile.</b> The grid asks
    ///     for a picture on every bind rather than being told, so a tile already drawn only learns of
    ///     one when it is rebound at the index it already shows — which is the case an index signal
    ///     alone cannot see.
    /// </summary>
    [Fact]
    public void A_picture_that_arrives_after_the_grid_settled_reaches_its_tile_on_refresh() {
        using var editor = Started();

        DoubleClick(editor, "Scenes");

        var grid = Grid(editor);
        var scene = TileView(grid, "Main.vxscene");

        Assert.Equal(0UL, scene.Picture.Texture);
        Assert.False(scene.Glyph.HasClass("hidden"));

        var pictured = grid.Picture;

        grid.Picture = node => node.Name == "Main.vxscene" ? 7UL : pictured(node);
        editor.Settle();

        // Not pushed: a grid that has not been told still shows the glyph.
        Assert.Equal(0UL, TileView(grid, "Main.vxscene").Picture.Texture);

        grid.Refresh();
        editor.Settle();

        scene = TileView(grid, "Main.vxscene");

        Assert.Equal(7UL, scene.Picture.Texture);
        Assert.False(scene.Picture.HasClass("hidden"));
        Assert.True(scene.Glyph.HasClass("hidden"));
    }

    /// <summary>
    ///     ⚠ <b>The frame a wheel turn runs draws the tiles it scrolled to, and a press on it picks
    ///     what it drew.</b> Found by the #1406 review: after the port every realised tile showed the
    ///     item it had before the scroll for exactly one frame — 252 of 252 checked tiles wrong — and
    ///     a press in that frame chose the previous asset, because the grid realises from
    ///     <c>LayoutFinished</c> and the tile's bindings waited for the next frame's flush. The
    ///     hand-written grid bound inside <c>Realise</c>, so it never did either.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>One frame per turn and never a settle.</b> Every other test in this file settles, and
    ///     a settled grid is right whatever the lag — which is how the dumps, the pictures and both
    ///     settle-then-change tests passed with it.
    /// </remarks>
    [Fact]
    public void A_wheel_turn_draws_each_tile_s_own_item_on_its_frame_and_a_press_picks_it() {
        using var editor = Started();

        var many = Path.Combine(editor.ProjectRoot, "Assets", "Many");

        Directory.CreateDirectory(many);

        for (var index = 0; index < 400; index++) {
            File.WriteAllText(Path.Combine(many, $"file{index:000}.png"), "x");
        }

        editor.Run("assets.refresh");
        DoubleClick(editor, "Many");

        var grid = Grid(editor);
        var body = Descendants(grid).OfType<VirtualizingGrid>().Single();
        var viewport = body.Bounds;

        Assert.Equal(400, grid.Items.Count);
        Assert.Equal(0, body.FirstItem);

        AssetTreeNode? chosen = null;

        grid.Selected += node => chosen = node;

        var checkedTiles = 0;

        for (var turn = 0; turn < 4; turn++) {
            var first = body.FirstItem;

            editor.Ui.At(viewport.X + (viewport.Width * 0.5f), viewport.Y + (viewport.Height * 0.5f)).Scroll(0, 400);

            // The wheel moved the pool, so a slot showing its old item is showing the wrong one.
            Assert.NotEqual(first, body.FirstItem);

            for (var slot = 0; slot < body.Tiles.Count; slot++) {
                if (body.Tiles[slot].HasClass("parked")) {
                    continue;
                }

                var item = body.FirstItem + slot;

                checkedTiles++;
                Assert.Equal(grid.Items[item].Name, Descendants(body.Tiles[slot]).Single(child => child.Tag == "asset-caption").Text);
            }

            // A press in the same frame, on a tile whose centre is inside the viewport, reports the
            // item that tile is drawn showing.
            var target = Enumerable.Range(body.FirstItem, body.Tiles.Count)
                .First(item => body.TileOf(item) is { } tile
                    && tile.Bounds.Y + (tile.Bounds.Height * 0.5f) > viewport.Y + 4f
                    && tile.Bounds.Y + (tile.Bounds.Height * 0.5f) < viewport.Y + viewport.Height - 4f);
            var hit = body.TileOf(target)!.Bounds;

            chosen = null;
            editor.Ui.MovePointer(hit.X + (hit.Width * 0.5f), hit.Y + (hit.Height * 0.5f));
            editor.Ui.PressPointer();
            editor.Ui.ReleasePointer();

            Assert.Same(grid.Items[target], chosen);

            // Presses a frame apart on the same spot are a double click, which would open the asset.
            editor.Document.Gestures.EndTapRun();
        }

        Assert.True(checkedTiles > 50, $"only {checkedTiles} tiles were checked");
    }

    /// <summary>
    ///     ⚠ <b>A press between <c>Show</c> and the next frame picks the new folder's item.</b> A slot
    ///     keeps its index across a folder change, so it keeps its place on screen too — and a map
    ///     from slot to node written by a tile binding still held the old folder's node until the
    ///     next flush. <c>TileAt</c> resolves the slot's item from the grid's own window instead,
    ///     which <c>Show</c> has already moved.
    /// </summary>
    [Fact]
    public void A_press_straight_after_showing_a_folder_picks_that_folder_s_item() {
        using var editor = Started();

        var grid = Grid(editor);
        var root = grid.Folder!;
        var scenes = Assert.Single(root.Children, node => node.Name == "Scenes");

        var tile = TileElement(editor, grid.Items[0].Name);
        var hit = tile.Bounds;

        // Settled at the root: a press on the first tile picks the root's first item.
        Assert.NotSame(scenes.Children[0], grid.Items[0]);

        grid.Show(scenes);

        AssetTreeNode? chosen = null;

        grid.Selected += node => chosen = node;
        editor.Ui.MovePointer(hit.X + (hit.Width * 0.5f), hit.Y + (hit.Height * 0.5f));
        editor.Ui.PressPointer();
        editor.Ui.ReleasePointer();

        Assert.Same(scenes.Children[0], chosen);
    }

    static AssetTile TileView(AssetGrid grid, string name) =>
        grid.Tiles.FirstOrDefault(tile => tile.Node?.Name == name) ?? throw new InvalidOperationException($"no tile for '{name}'");

    static void Check(EditorSession editor, string state) {
        var grid = Grid(editor);
        var tree = editor.Ui.Tree(grid);
        var flags = editor.Ui.Flags(grid);

        if (Record is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{state}.tree.txt"), tree);
            File.WriteAllText(Path.Combine(directory, $"{state}.flags.txt"), flags);
            PngCodec.Save(Path.Combine(directory, $"{state}.png"), editor.Ui.Capture());

            // The whole editor through both renderers, written where `VIXEN_PANEL_CAPTURE` says —
            // `ScrollingPanelPictureTests`' harness, so the Vulkan half is the shipping renderer.
            using var device = ScrollingPanelPictureTests.OpenDevice();
            using var gpu = device is null
                ? null
                : new ScrollingPanelPictureTests.GpuPicture(
                    device,
                    ScrollingPanelPictureTests.WidthOf(editor),
                    ScrollingPanelPictureTests.HeightOf(editor)
                );

            ScrollingPanelPictureTests.Draw(editor, gpu, $"assetgrid-{state}");
        }

        Assert.Equal(Reference(state, "tree"), Normalise(tree));
        Assert.Equal(Reference(state, "flags"), Normalise(flags));
    }

    static string Normalise(string text) => text.ReplaceLineEndings("\n").Trim();

    /// <summary>A reference dump, read from the source tree beside this file.</summary>
    static string Reference(string state, string kind) {
        const string Relative = "Editor/Vixen.Editor.App.Tests/AssetGridDumps";

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, Relative.Replace('/', Path.DirectorySeparatorChar), $"{state}.{kind}.txt");

            if (File.Exists(candidate)) {
                return Normalise(File.ReadAllText(candidate));
            }
        }

        throw new FileNotFoundException($"'{Relative}/{state}.{kind}.txt' was not found above '{AppContext.BaseDirectory}'.");
    }

    // ── Driving the panel ─────────────────────────────────────────────────────

    /// <summary>The editor with the Project panel showing its grid.</summary>
    static EditorSession Started() {
        var editor = EditorSession.Start();

        editor.Open("project");

        if (Grid(editor).HasClass("hidden")) {
            Descendants(editor.Panel("project")).OfType<ButtonBase>().First(button => button.Label == "Grid").Activate();
            editor.Settle();
        }

        return editor;
    }

    static AssetGrid Grid(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<AssetGrid>().FirstOrDefault()
        ?? throw editor.Fail("the browser has no grid");

    static Select Picker(EditorSession editor) =>
        Descendants(editor.Panel("project")).OfType<Select>().FirstOrDefault(select => select.HasClass("browser-tile-size"))
        ?? throw editor.Fail("the browser has no tile-size picker");

    /// <summary>The realised, unparked tile element whose caption reads a name.</summary>
    static UiElement TileElement(EditorSession editor, string name) =>
        Descendants(Grid(editor))
            .Where(element => element.Tag == "asset-caption" && element.Text == name)
            .Select(caption => caption.Parent!)
            .FirstOrDefault(tile => !tile.HasClass("parked"))
        ?? throw editor.Fail($"no tile for '{name}'");

    static void DoubleClick(EditorSession editor, string name) {
        var tile = TileElement(editor, name);

        editor.Ui.At(tile.Bounds.X + (tile.Bounds.Width * 0.5f), tile.Bounds.Y + (tile.Bounds.Height * 0.5f)).DoubleClick();
        editor.Settle();
    }

    static IEnumerable<UiElement> Descendants(UiElement element) {
        foreach (var child in element.Children) {
            yield return child;

            foreach (var found in Descendants(child)) {
                yield return found;
            }
        }
    }
}
