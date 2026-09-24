// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Imaging;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The keybinding panel as the editor hosts it, held to what it drew before it left the editor (#650).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Recorded before the move and not after it.</b> Every dump below was produced by
///         <c>KeyBindingsView</c> while it was still <c>Vixen.Editor.Ui</c>'s, opened through
///         <c>EditorShell</c>'s own registration. The move put the panel in another assembly, took
///         its seven stylesheet rules from the editor's sheet into <c>AdvancedTheme.vcss</c> — which
///         the cascade reads earlier — and replaced its nineteen strings and its preset list with a
///         control library's. Each of those can move a pixel while every behavioural test stays
///         green, so the move is required to reproduce these byte for byte in five states reached
///         through the interface.
///     </para>
///     <para>
///         ⚠ <b>Two dumps per state</b>, for <c>MessageLogViewDumpTests</c>' reason: the picker's value,
///         the Record button's label and its checked bit are all where only <c>UiTest.Flags</c> looks.
///     </para>
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "UiTest.Flags reads nine properties by name; tests are not trimmed.")]
public sealed class KeyBindingsViewDumpTests {
    /// <summary>Where a run writes its dumps and pictures, when somebody asks it to.</summary>
    static readonly string? Record = Environment.GetEnvironmentVariable("VIXEN_KEYBINDINGS_RECORD");

    [Fact]
    public void Opened_the_panel_lists_the_shells_commands() {
        using var harness = new Harness();

        Check(harness, "opened", OpenedTree, OpenedFlags);
    }

    [Fact]
    public void Choosing_a_row_ungreys_record() {
        using var harness = new Harness();

        harness.ClickRow(1);

        Assert.NotNull(harness.View.Selected);
        Check(harness, "chosen", ChosenTree, ChosenFlags);
    }

    /// <summary>Record pressed with a pointer: the button renames and ticks itself.</summary>
    [Fact]
    public void Recording_renames_and_ticks_the_button() {
        using var harness = new Harness();

        harness.ClickRow(1);
        harness.Ui.Get("keybindings-toolbar button").First().Click();
        harness.Settle();

        Assert.True(harness.View.IsCapturing);
        Check(harness, "capturing", CapturingTree, CapturingFlags);
    }

    /// <summary>A chord another command holds: refused, named, and the line reddened.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked of the glyphs, not of the line.</b> This test used to compare the computed
    ///     <c>color</c> of <c>keybindings-status</c> before and after, and passed — while every
    ///     picture of the conflict state showed a grey sentence. The line never draws a glyph: the
    ///     <c>@Sentence()</c> interpolation makes a child <c>text</c> element, and
    ///     <c>ControlTheme.vcss</c>'s <c>text { color: var(--text); }</c> overrode whatever that child
    ///     would have inherited (removed in #1372). So the parent turned red, the pixels stayed <c>--text</c>, and a test
    ///     reading the parent certified a colour nothing painted. It now counts red pixels inside the
    ///     line in a real capture — none while calm, some once refused — and requires the drawn
    ///     child's colour to be the line's own in both states.
    /// </remarks>
    [Fact]
    public void A_refused_chord_reddens_the_line() {
        using var harness = new Harness();

        harness.ClickRow(1);

        var sentence = Assert.Single(harness.View.Status.Children);
        Assert.Equal("text", sentence.Tag);
        Assert.Equal(harness.Ui.ColorOf(harness.View.Status, "color"), harness.Ui.ColorOf(sentence, "color"));
        Assert.Equal(0, RedPixels(harness));

        var taken = harness.Shell.Keys.ChordFor(harness.Shell.Commands.Commands
            .Select(command => command.Id)
            .Order(StringComparer.Ordinal)
            .First(id => id != harness.View.Selected && harness.Shell.Keys.ChordFor(id).IsBound));

        Assert.Equal(BindResult.Conflict, harness.View.Rebind(taken));
        harness.Settle();

        Assert.True(harness.View.Status.HasClass("conflict"));

        sentence = Assert.Single(harness.View.Status.Children);
        Assert.Equal(harness.Ui.ColorOf(harness.View.Status, "color"), harness.Ui.ColorOf(sentence, "color"));

        // The floor is a guard, not a measurement: a sentence of some forty glyphs drawn in
        // `--danger` covers hundreds of pixels, and a grey one covers none that pass the test.
        var red = RedPixels(harness);
        Assert.True(red > 50, $"{red} red pixels in the status line: the refusal was not drawn red");

        Check(harness, "conflict", ConflictTree, ConflictFlags);
    }

    /// <summary>Pixels inside the status line whose red clearly dominates, in a software capture.</summary>
    /// <remarks>
    ///     The line's ground is <c>--surface-sunken</c>, a near-neutral grey, and the calm sentence is
    ///     <c>--text-muted</c>, another; only <c>--danger</c> (and its antialiased edges) has a red
    ///     channel sixty levels above both others.
    /// </remarks>
    static int RedPixels(Harness harness) {
        var image = harness.Ui.Capture();
        var bounds = harness.View.Status.Bounds;
        var red = 0;

        for (var y = (int)bounds.Top; y < (int)bounds.Bottom && y < image.Height; y++) {
            for (var x = (int)bounds.Left; x < (int)bounds.Right && x < image.Width; x++) {
                var offset = image.Offset(x, y);
                var r = image.Pixels[offset];
                var g = image.Pixels[offset + 1];
                var b = image.Pixels[offset + 2];

                if (r > g + 60 && r > b + 60) {
                    red++;
                }
            }
        }

        return red;
    }

    /// <summary>The picker offers the editor's three presets, which only the shell can tell it.</summary>
    /// <remarks>
    ///     ⚠ <b>No dump above can see this.</b> Since #650 the panel knows nobody's preset names, and
    ///     one line in <c>EditorShell</c> hands it <see cref="KeyMapPresets.Names" />. With that line
    ///     gone every dump stays byte-identical, because the options live in a root-level popover and
    ///     the closed picker shows the value it was given. <c>Select.Value</c> also accepts a value
    ///     that no option carries, so a test that sets <c>Presets.Value = "Unreal"</c> passes too.
    ///     Unity and Unreal would silently leave the editor's picker, and this is the only test that
    ///     would notice.
    /// </remarks>
    [Fact]
    public void The_picker_offers_the_editors_presets() {
        using var harness = new Harness();

        Assert.Equal(KeyMapPresets.Names, harness.View.Presets.Options.Select(option => option.Value));
    }

    [Fact]
    public void The_filter_narrows_the_grid() {
        using var harness = new Harness();

        harness.View.Search.Value = "panel";
        harness.Settle();

        Check(harness, "filtered", FilteredTree, FilteredFlags);
    }

    static void Check(Harness harness, string state, string tree, string flags) {
        var actualTree = harness.Ui.Tree(harness.View);
        var actualFlags = harness.Ui.Flags(harness.View);

        if (Record is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{state}.tree.txt"), actualTree);
            File.WriteAllText(Path.Combine(directory, $"{state}.flags.txt"), actualFlags);
            PngCodec.Save(Path.Combine(directory, $"{state}.png"), harness.Ui.Capture());
        }

        Assert.Equal(Normalise(tree), Normalise(actualTree));
        Assert.Equal(Normalise(flags), Normalise(actualFlags));
    }

    static string Normalise(string text) => text.ReplaceLineEndings("\n").Trim();

    /// <summary>The shell, with the keybinding panel opened through its own registration.</summary>
    sealed class Harness : IDisposable {
        public Harness() {
            Shell = new EditorShell(820f, 460f, ThemeMode.Dark);
            Font(Shell.Document);

            Shell.RegisterLayout(
                "Keys",
                new StringId("test.layout.keys", "Keys"),
                () => LayoutPresets.Single(EditorShell.KeyBindingsPanel)
            );

            Shell.Workspace.Reset();

            Ui = UiTest.Adopt(Shell.Document);
            View = Shell.Keyboard ?? throw new InvalidOperationException("the shell did not build its keybinding panel");

            Settle();
        }

        public EditorShell Shell { get; }

        public UiTest Ui { get; }

        public KeyBindingsView View { get; }

        /// <summary>Clicks a grid row by its position from the top, the way a person picks one.</summary>
        public void ClickRow(int index) {
            Ui.Get("data-row").Nth(index).Click();
            Settle();
        }

        public void Settle() => Ui.Frames(2);

        /// <summary>⚠ The shell and no more — see <c>EditorChromeVisualTests.ChromeFixture.Dispose</c>.</summary>
        public void Dispose() => Shell.Dispose();

        static void Font(UiDocument document) {
            var face = FontFace.Load(File.ReadAllBytes(TypefacePath()), name: "OpenSans");

            document.Fonts.Register(face.Name, face);
            document.Fonts.Default = face;
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
    }

    // ── The reference dumps, taken from the panel while it was the editor's ──────

    const string OpenedTree = """
        <keybindings-view .size-md .variant-default> 3,3 810×359
          <keybindings-toolbar> 0,0 810×41
            <search-box .empty .size-md .variant-default> 6,5 192×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 138×22 "Filter commands…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 202,4 110×32
              <select-field> 10,5 72×22 "Vixen"
              <icon .size-md .variant-default> 88,10 12×12
            <button .size-sm .variant-subtle> 316,4 108×28
              <label> 10,4 88×20 "Press a Key…"
            <button .size-sm .variant-subtle> 428,4 70×28
              <label> 10,4 50×20 "Unbind"
            <button .size-sm .variant-subtle> 502,4 58×28
              <label> 10,4 38×20 "Reset"
            <button .size-sm .variant-subtle> 564,4 78×28
              <label> 10,4 58×20 "Reset All"
            <button .size-sm .variant-subtle> 646,4 78×28
              <label> 10,4 58×20 "Import…"
            <button .size-sm .variant-subtle> 728,4 76×28
              <label> 10,4 56×20 "Export…"
          <data-grid .size-md .variant-default> 0,41 810×290
            <scroll-view .size-md .variant-default> 0,0 810×290
              <scroll-content> 0,0 810×314
                <data-header> 0,0 810×26
                  <data-header-cell .size-md .unsorted .variant-default> 0,0 240×25
                    <data-text> 8,1 211×22 "Command"
                    <icon .size-md .variant-default> 223,8 9×9
                    <data-resizer> 234,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 240,0 120×25
                    <data-text> 8,1 91×22 "Category"
                    <icon .size-md .variant-default> 103,8 9×9
                    <data-resizer> 114,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 360,0 150×25
                    <data-text> 8,1 121×22 "Shortcut"
                    <icon .size-md .variant-default> 133,8 9×9
                    <data-resizer> 144,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 510,0 90×25
                    <data-text> 8,1 61×22 "Source"
                    <icon .size-md .variant-default> 73,8 9×9
                    <data-resizer> 84,0 6×25
                <data-row .size-md .variant-default> 0,26 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Search Everywhere…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Edit"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+F"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,50 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Mode"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Mode"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,74 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Close Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+W"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,98 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Float Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,122 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keys"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,146 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,170 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Command Palette…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+K"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,194 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keyboard Shortcuts"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,218 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Message Log"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,242 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Previous Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,266 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Reset Layout"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,290 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Toggle Dark Theme"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Alt+D"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
              <scrollbar .size-md .variant-default .vertical> 800,0 10×290
              <scrollbar .horizontal .size-md .variant-default> 0,280 810×10
          <keybindings-status> 0,331 810×28
            <text> 9,5 204×19 "Choose a command to rebind it."
        """;

    const string OpenedFlags = """
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter commands…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid Value="Vixen"
        <button .size-sm .variant-subtle> State=Disabled Disabled=True Label="Press a Key…"
        <button .size-sm .variant-subtle> Label="Unbind"
        <button .size-sm .variant-subtle> Label="Reset"
        <button .size-sm .variant-subtle> Label="Reset All"
        <button .size-sm .variant-subtle> Label="Import…"
        <button .size-sm .variant-subtle> Label="Export…"
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ChosenTree = """
        <keybindings-view .size-md .variant-default> 3,3 810×359
          <keybindings-toolbar> 0,0 810×41
            <search-box .empty .size-md .variant-default> 6,5 192×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 138×22 "Filter commands…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 202,4 110×32
              <select-field> 10,5 72×22 "Vixen"
              <icon .size-md .variant-default> 88,10 12×12
            <button .size-sm .variant-subtle> 316,4 108×28
              <label> 10,4 88×20 "Press a Key…"
            <button .size-sm .variant-subtle> 428,4 70×28
              <label> 10,4 50×20 "Unbind"
            <button .size-sm .variant-subtle> 502,4 58×28
              <label> 10,4 38×20 "Reset"
            <button .size-sm .variant-subtle> 564,4 78×28
              <label> 10,4 58×20 "Reset All"
            <button .size-sm .variant-subtle> 646,4 78×28
              <label> 10,4 58×20 "Import…"
            <button .size-sm .variant-subtle> 728,4 76×28
              <label> 10,4 56×20 "Export…"
          <data-grid .size-md .variant-default> 0,41 810×290
            <scroll-view .size-md .variant-default> 0,0 810×290
              <scroll-content> 0,0 810×314
                <data-header> 0,0 810×26
                  <data-header-cell .size-md .unsorted .variant-default> 0,0 240×25
                    <data-text> 8,1 211×22 "Command"
                    <icon .size-md .variant-default> 223,8 9×9
                    <data-resizer> 234,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 240,0 120×25
                    <data-text> 8,1 91×22 "Category"
                    <icon .size-md .variant-default> 103,8 9×9
                    <data-resizer> 114,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 360,0 150×25
                    <data-text> 8,1 121×22 "Shortcut"
                    <icon .size-md .variant-default> 133,8 9×9
                    <data-resizer> 144,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 510,0 90×25
                    <data-text> 8,1 61×22 "Source"
                    <icon .size-md .variant-default> 73,8 9×9
                    <data-resizer> 84,0 6×25
                <data-row .size-md .variant-default> 0,26 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Search Everywhere…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Edit"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+F"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,50 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Mode"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Mode"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,74 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Close Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+W"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,98 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Float Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,122 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keys"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,146 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,170 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Command Palette…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+K"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,194 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keyboard Shortcuts"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,218 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Message Log"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,242 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Previous Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,266 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Reset Layout"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,290 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Toggle Dark Theme"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Alt+D"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
              <scrollbar .size-md .variant-default .vertical> 800,0 10×290
              <scrollbar .horizontal .size-md .variant-default> 0,280 810×10
          <keybindings-status> 0,331 810×28
            <text> 9,5 228×19 "Press a Key, or double-click the row."
        """;

    const string ChosenFlags = """
        <keybindings-view .size-md .variant-default> State=Hover, FocusWithin
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter commands…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid Value="Vixen"
        <button .size-sm .variant-subtle> Label="Press a Key…"
        <button .size-sm .variant-subtle> Label="Unbind"
        <button .size-sm .variant-subtle> Label="Reset"
        <button .size-sm .variant-subtle> Label="Reset All"
        <button .size-sm .variant-subtle> Label="Import…"
        <button .size-sm .variant-subtle> Label="Export…"
        <data-grid .size-md .variant-default> State=Hover, Focus, FocusWithin
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content> State=Hover
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-row .size-md .variant-default> State=Hover, Checked
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> State=Hover Label=Vixen.Ui.UiElement
        <data-text> State=Hover
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string CapturingTree = """
        <keybindings-view .size-md .variant-default> 3,3 810×359
          <keybindings-toolbar> 0,0 810×41
            <search-box .empty .size-md .variant-default> 6,5 218×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 138×22 "Filter commands…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 228,4 110×32
              <select-field> 10,5 72×22 "Vixen"
              <icon .size-md .variant-default> 88,10 12×12
            <button .size-sm .variant-subtle> 342,4 82×28
              <label> 10,4 62×20 "Waiting…"
            <button .size-sm .variant-subtle> 428,4 70×28
              <label> 10,4 50×20 "Unbind"
            <button .size-sm .variant-subtle> 502,4 58×28
              <label> 10,4 38×20 "Reset"
            <button .size-sm .variant-subtle> 564,4 78×28
              <label> 10,4 58×20 "Reset All"
            <button .size-sm .variant-subtle> 646,4 78×28
              <label> 10,4 58×20 "Import…"
            <button .size-sm .variant-subtle> 728,4 76×28
              <label> 10,4 56×20 "Export…"
          <data-grid .size-md .variant-default> 0,41 810×290
            <scroll-view .size-md .variant-default> 0,0 810×290
              <scroll-content> 0,0 810×314
                <data-header> 0,0 810×26
                  <data-header-cell .size-md .unsorted .variant-default> 0,0 240×25
                    <data-text> 8,1 211×22 "Command"
                    <icon .size-md .variant-default> 223,8 9×9
                    <data-resizer> 234,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 240,0 120×25
                    <data-text> 8,1 91×22 "Category"
                    <icon .size-md .variant-default> 103,8 9×9
                    <data-resizer> 114,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 360,0 150×25
                    <data-text> 8,1 121×22 "Shortcut"
                    <icon .size-md .variant-default> 133,8 9×9
                    <data-resizer> 144,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 510,0 90×25
                    <data-text> 8,1 61×22 "Source"
                    <icon .size-md .variant-default> 73,8 9×9
                    <data-resizer> 84,0 6×25
                <data-row .size-md .variant-default> 0,26 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Search Everywhere…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Edit"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+F"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,50 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Mode"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Mode"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,74 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Close Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+W"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,98 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Float Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,122 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keys"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,146 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,170 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Command Palette…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+K"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,194 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keyboard Shortcuts"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,218 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Message Log"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,242 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Previous Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,266 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Reset Layout"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,290 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Toggle Dark Theme"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Alt+D"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
              <scrollbar .size-md .variant-default .vertical> 800,0 10×290
              <scrollbar .horizontal .size-md .variant-default> 0,280 810×10
          <keybindings-status> 0,331 810×28
            <text> 9,5 282×19 "Press the shortcut you want. Escape cancels."
        """;

    const string CapturingFlags = """
        <keybindings-view .size-md .variant-default> State=Hover, Focus, FocusWithin
        <keybindings-toolbar> State=Hover
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter commands…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid Value="Vixen"
        <button .size-sm .variant-subtle> State=Hover, Checked Label="Waiting…"
        <label> State=Hover
        <button .size-sm .variant-subtle> Label="Unbind"
        <button .size-sm .variant-subtle> Label="Reset"
        <button .size-sm .variant-subtle> Label="Reset All"
        <button .size-sm .variant-subtle> Label="Import…"
        <button .size-sm .variant-subtle> Label="Export…"
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-row .size-md .variant-default> State=Checked
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string ConflictTree = """
        <keybindings-view .size-md .variant-default> 3,3 810×359
          <keybindings-toolbar> 0,0 810×41
            <search-box .empty .size-md .variant-default> 6,5 192×30
              <icon .size-md .variant-default> 10,8 14×14
              <field-placeholder> 29,3 138×22 "Filter commands…"
              <field-text> 30,5 0×20
              <icon-button .size-md .variant-subtle> 0,0 0×0
                <icon .size-md .variant-default> 0,0 0×0
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 202,4 110×32
              <select-field> 10,5 72×22 "Vixen"
              <icon .size-md .variant-default> 88,10 12×12
            <button .size-sm .variant-subtle> 316,4 108×28
              <label> 10,4 88×20 "Press a Key…"
            <button .size-sm .variant-subtle> 428,4 70×28
              <label> 10,4 50×20 "Unbind"
            <button .size-sm .variant-subtle> 502,4 58×28
              <label> 10,4 38×20 "Reset"
            <button .size-sm .variant-subtle> 564,4 78×28
              <label> 10,4 58×20 "Reset All"
            <button .size-sm .variant-subtle> 646,4 78×28
              <label> 10,4 58×20 "Import…"
            <button .size-sm .variant-subtle> 728,4 76×28
              <label> 10,4 56×20 "Export…"
          <data-grid .size-md .variant-default> 0,41 810×290
            <scroll-view .size-md .variant-default> 0,0 810×290
              <scroll-content> 0,0 810×314
                <data-header> 0,0 810×26
                  <data-header-cell .size-md .unsorted .variant-default> 0,0 240×25
                    <data-text> 8,1 211×22 "Command"
                    <icon .size-md .variant-default> 223,8 9×9
                    <data-resizer> 234,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 240,0 120×25
                    <data-text> 8,1 91×22 "Category"
                    <icon .size-md .variant-default> 103,8 9×9
                    <data-resizer> 114,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 360,0 150×25
                    <data-text> 8,1 121×22 "Shortcut"
                    <icon .size-md .variant-default> 133,8 9×9
                    <data-resizer> 144,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 510,0 90×25
                    <data-text> 8,1 61×22 "Source"
                    <icon .size-md .variant-default> 73,8 9×9
                    <data-resizer> 84,0 6×25
                <data-row .size-md .variant-default> 0,26 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Search Everywhere…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Edit"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+F"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,50 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Mode"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Mode"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,74 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Close Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+W"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,98 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Float Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,122 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keys"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,146 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Next Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,170 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Command Palette…"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+K"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,194 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keyboard Shortcuts"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,218 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Message Log"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,242 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Previous Tab"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Shift+Tab"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,266 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Reset Layout"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,290 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Toggle Dark Theme"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+Alt+D"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
              <scrollbar .size-md .variant-default .vertical> 800,0 10×290
              <scrollbar .horizontal .size-md .variant-default> 0,280 810×10
          <keybindings-status .conflict> 0,331 810×28
            <text> 9,5 426×19 "Ctrl+Shift+F is already Search Everywhere…. Press it again to take it."
        """;

    const string ConflictFlags = """
        <keybindings-view .size-md .variant-default> State=Hover, FocusWithin
        <search-box .empty .size-md .variant-default> State=PlaceholderShown, Valid Placeholder="Filter commands…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid Value="Vixen"
        <button .size-sm .variant-subtle> Label="Press a Key…"
        <button .size-sm .variant-subtle> Label="Unbind"
        <button .size-sm .variant-subtle> Label="Reset"
        <button .size-sm .variant-subtle> Label="Reset All"
        <button .size-sm .variant-subtle> Label="Import…"
        <button .size-sm .variant-subtle> Label="Export…"
        <data-grid .size-md .variant-default> State=Hover, Focus, FocusWithin
        <scroll-view .size-md .variant-default> State=Hover
        <scroll-content> State=Hover
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-row .size-md .variant-default> State=Hover, Checked
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> State=Hover Label=Vixen.Ui.UiElement
        <data-text> State=Hover
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;

    const string FilteredTree = """
        <keybindings-view .size-md .variant-default> 3,3 810×359
          <keybindings-toolbar> 0,0 810×45
            <search-box .size-md .variant-default> 6,4 192×36
              <icon .size-md .variant-default> 10,11 14×14
              <field-placeholder> 0,0 0×0 "Filter commands…"
              <field-text> 30,7 42×22 "panel"
              <icon-button .size-md .variant-subtle> 78,5 26×26
                <icon .size-md .variant-default> 6,6 14×14
                <label> 0,0 0×0 "Clear"
            <select .size-sm .variant-default> 202,6 110×32
              <select-field> 10,5 72×22 "Vixen"
              <icon .size-md .variant-default> 88,10 12×12
            <button .size-sm .variant-subtle> 316,4 108×28
              <label> 10,4 88×20 "Press a Key…"
            <button .size-sm .variant-subtle> 428,4 70×28
              <label> 10,4 50×20 "Unbind"
            <button .size-sm .variant-subtle> 502,4 58×28
              <label> 10,4 38×20 "Reset"
            <button .size-sm .variant-subtle> 564,4 78×28
              <label> 10,4 58×20 "Reset All"
            <button .size-sm .variant-subtle> 646,4 78×28
              <label> 10,4 58×20 "Import…"
            <button .size-sm .variant-subtle> 728,4 76×28
              <label> 10,4 56×20 "Export…"
          <data-grid .size-md .variant-default> 0,45 810×286
            <scroll-view .size-md .variant-default> 0,0 810×286
              <scroll-content> 0,0 810×122
                <data-header> 0,0 810×26
                  <data-header-cell .size-md .unsorted .variant-default> 0,0 240×25
                    <data-text> 8,1 211×22 "Command"
                    <icon .size-md .variant-default> 223,8 9×9
                    <data-resizer> 234,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 240,0 120×25
                    <data-text> 8,1 91×22 "Category"
                    <icon .size-md .variant-default> 103,8 9×9
                    <data-resizer> 114,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 360,0 150×25
                    <data-text> 8,1 121×22 "Shortcut"
                    <icon .size-md .variant-default> 133,8 9×9
                    <data-resizer> 144,0 6×25
                  <data-header-cell .size-md .unsorted .variant-default> 510,0 90×25
                    <data-text> 8,1 61×22 "Source"
                    <icon .size-md .variant-default> 73,8 9×9
                    <data-resizer> 84,0 6×25
                <data-row .size-md .variant-default> 0,26 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Close Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "Ctrl+W"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,50 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Float Panel"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "View"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,74 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Keyboard Shortcuts"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .size-md .variant-default> 0,98 600×24
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 240×24
                    <data-text> 8,1 224×22 "Message Log"
                  <data-cell .size-md .variant-default> 240,0 120×24
                    <data-text> 8,1 104×22 "Panel"
                  <data-cell .size-md .variant-default> 360,0 150×24
                    <data-text> 8,1 134×22 "—"
                  <data-cell .size-md .variant-default> 510,0 90×24
                    <data-text> 8,1 74×22 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Keys"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "View"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "—"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Next Tab"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "View"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Ctrl+Tab"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Command Palette…"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "View"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Ctrl+K"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Keyboard Shortcuts"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Panel"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "—"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Message Log"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Panel"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "—"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Previous Tab"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "View"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Ctrl+Shift+Tab"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Reset Layout"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "View"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "—"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
                <data-row .parked .size-md .variant-default> 0,0 0×0
                  <data-group-label .hidden> 0,0 0×0
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Toggle Dark Theme"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "View"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Ctrl+Alt+D"
                  <data-cell .size-md .variant-default> 0,0 0×0
                    <data-text> 0,0 0×0 "Default"
              <scrollbar .size-md .variant-default .vertical> 800,0 10×286
              <scrollbar .horizontal .size-md .variant-default> 0,276 810×10
          <keybindings-status> 0,331 810×28
            <text> 9,5 204×19 "Choose a command to rebind it."
        """;

    const string FilteredFlags = """
        <search-box .size-md .variant-default> State=Valid Value="panel" Placeholder="Filter commands…"
        <icon-button .size-md .variant-subtle> Label="Clear"
        <select .size-sm .variant-default> State=Valid Value="Vixen"
        <button .size-sm .variant-subtle> State=Disabled Disabled=True Label="Press a Key…"
        <button .size-sm .variant-subtle> Label="Unbind"
        <button .size-sm .variant-subtle> Label="Reset"
        <button .size-sm .variant-subtle> Label="Reset All"
        <button .size-sm .variant-subtle> Label="Import…"
        <button .size-sm .variant-subtle> Label="Export…"
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-header-cell .size-md .unsorted .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <data-cell .size-md .variant-default> Label=Vixen.Ui.UiElement
        <scrollbar .size-md .variant-default .vertical> Value=0
        <scrollbar .horizontal .size-md .variant-default> Value=0
        """;
}
