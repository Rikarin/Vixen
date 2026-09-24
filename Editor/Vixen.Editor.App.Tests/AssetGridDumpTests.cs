// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Imaging;
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
