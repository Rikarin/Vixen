// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Microsoft.Extensions.Logging;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Editor.Testing;
using Vixen.Editor.Ui;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Desktop;
using Vixen.Ui.Renderer;
using Vixen.Ui.Rendering;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     The panels #1275 converted from <c>overflow: auto</c> to a <c>ScrollView</c>, drawn before and
///     after a scroll, with the difference between the two pictures held to the view's own box — and
///     the two it closed by removing a declaration, held to the panel's own scroll.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The conversions were landed, reviewed and merged on counters alone.</b> Every test
///         beside this one says the element is a <c>ScrollView</c>, that its <c>MaximumTop</c> is
///         positive and that a scroll brings the last row's box inside the port — all of which stays
///         true of a view that scrolls its content <i>and draws it over the dialog above</i>, because
///         the clip is not the scroller's: under a tag of its own the control loses the
///         <c>overflow: hidden</c> its user-agent rule would have given it (#1327), and nothing in a
///         layout rectangle says whether a draw was cut.
///     </para>
///     <para>
///         ⚠ <b>So the oracle is closed-form and about pixels: scrolling changes the picture, and
///         changes it nowhere outside the view.</b> Two frames of the same editor, identical but for
///         the scroll offset. A view that does not scroll leaves them equal and fails the first half;
///         a view that scrolls but does not clip moves the rows it lets hang out, over whatever is
///         beside it, and fails the second. Neither half needs a committed picture, and the frame is
///         the application's own — <see cref="EditorSession" /> builds the editor as the host does.
///     </para>
///     <para>
///         ⚠ <b>Drawn twice, and the two renderers answer the same question.</b> The software
///         rasterizer always, because it needs no device and is what the suite can run everywhere;
///         the Vulkan <see cref="UiRenderer" /> when a device opens, because the clip is a scissor on
///         the GPU and a software picture is a claim about the draw list rather than about what ships.
///         <c>VIXEN_REQUIRE_VULKAN=1</c> makes a missing device a failure. The pictures are written
///         only when <c>VIXEN_PANEL_CAPTURE=&lt;directory&gt;</c> names somewhere to put them.
///     </para>
///     <para>
///         ⚠ <b>Each of the three oracles was seen to fail on the picture it describes before it was
///         trusted.</b> With <c>overflow: hidden</c> taken off <c>choice-scroller</c>, "Move Set"
///         draws over the dialog's "What kind of asset?" title and 987 pixels outside the view change;
///         off <c>message-log-detail</c>, sixteen thousand. With <c>position: relative</c> taken off
///         <c>console-detail</c> the pixel oracle stays <i>green</i> — the view's clip hides where the
///         thumb went — and the picture shows the thumb gone from the top of the scroll, because the
///         bar now spans the whole console; that is why the bars are checked as boxes as well.
///     </para>
///     <para>
///         ⚠ <b>Drawn at the session's own 1600×1000, and once at 1280×800 on purpose.</b> At the
///         smaller size the console's detail pane — 132 px and <c>flex-shrink: 0</c> until #1390 —
///         was taller than what the docked console had left under its toolbar: the list went to zero
///         rows and the pane's bottom 28 px, with the end of the stack and the whole thumb at the end
///         of the scroll, sat behind the dock panel's edge. The ancestor check below is what reports
///         that, and <see cref="The_console_detail_fits_a_short_console_and_its_thumb_reaches_the_end" />
///         runs it there.
///     </para>
/// </remarks>
public sealed class ScrollingPanelPictureTests {
    const int Width = 1600;
    const int Height = 1000;

    internal static int WidthOf(EditorSession fixture) => (int)MathF.Round(fixture.Document.Viewport.ViewportWidth);

    internal static int HeightOf(EditorSession fixture) => (int)MathF.Round(fixture.Document.Viewport.ViewportHeight);

    internal static readonly Color4 Background = new(0.05f, 0.05f, 0.05f, 1f);

    /// <summary>Where the pictures go, or null when nobody asked for any.</summary>
    static string? Destination => Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE");

    /// <summary>The New Asset… picker: the list that started #1275, and the one with a dialog above and below it.</summary>
    [Fact]
    public void The_new_asset_picker_scrolls_inside_its_box_and_nowhere_else() {
        using var fixture = Start();

        fixture.Run("assets.create").Settle();
        Assert.True(fixture.IsAsking);

        var view = Scroller(fixture, "choice-scroller");

        Check(fixture, view, "new-asset-picker");
    }

    /// <summary>The console's detail pane, over a stack forty frames deep.</summary>
    [Fact]
    public void The_console_detail_scrolls_inside_its_box_and_nowhere_else() {
        using var fixture = Start();

        var (_, view) = SelectDeepError(fixture);

        Check(fixture, view, "console-detail");
    }

    /// <summary>The same pane in an editor 800 px tall, where the docked console is shorter than the pane.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The size the session's own 1600×1000 hid (#1390).</b> The pane was 132 px and
    ///         <c>flex-shrink: 0</c>, and here the console under its toolbar has less than that: the
    ///         list went to zero rows and the pane's bottom 28 px sat behind the dock panel's edge.
    ///         <see cref="Check" />'s ancestor half is what reports the cut, and the list's height
    ///         is the other half of the issue.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the thumb, measured rather than looked at (#1327).</b> A capture of exactly
    ///         this view showed the vertical thumb present at the top of the scroll and gone at the
    ///         end, on the Vulkan device and the software rasteriser alike, and a reviewer looking at
    ///         the picture had missed it. The end of the track was in those hidden 28 px and a
    ///         24 px thumb fits inside them — so the question is asked of pixels, by
    ///         <see cref="ThumbAtTheEnd" /> from inside <see cref="Check" />: the thumb's own colour,
    ///         sampled where the thumb is in the first frame, has to be found in the last
    ///         thumb-length of the track in the second. Every vertical view here is held to it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_console_detail_fits_a_short_console_and_its_thumb_reaches_the_end() {
        using var fixture = Start(1280, 800);

        var (console, view) = SelectDeepError(fixture);

        Assert.True(
            console.List.Height >= console.List.RowHeight,
            $"with a record selected the console's list is {console.List.Height} px tall — not one "
            + $"{console.List.RowHeight} px row left to select from at {WidthOf(fixture)}×{HeightOf(fixture)}."
        );

        // The thumb half is in `Check`, which every vertical view in this class goes through.
        Check(fixture, view, "console-detail-short");
    }

    /// <summary>With nothing selected, the pane's one line and the list's row both fit a console 640 px tall.</summary>
    /// <remarks>
    ///     ⚠ <b>A floor for the list is a floor something else pays for.</b> The empty pane is one line
    ///     and does not shrink, and the first cut of #1390's floor — two rows — pushed that line
    ///     through the dock panel's bottom edge in the Preferences pictures' 1600×640 editor. Seen in
    ///     a picture taken for a different panel, which is why it is a fact of its own now: the
    ///     pane inside every clip above it, and the list at least a row.
    /// </remarks>
    [Fact]
    public void The_empty_console_detail_and_a_row_of_the_list_fit_a_short_console() {
        using var fixture = Start(Width, 640);

        fixture.Open("console");
        fixture.Frames(2);

        var console = Find<Vixen.Editor.Ui.ConsoleView>(fixture.Document.Root)
            ?? throw fixture.Fail("the console is not open");

        var view = Scroller(fixture, "console-detail");

        Assert.True(view.HasClass("empty"), "a record is selected, so this is not the empty pane");
        Assert.True(console.List.Height >= console.List.RowHeight, $"the list is {console.List.Height} px tall.");

        Uncut(fixture, view);
    }

    /// <summary>With a record selected, the pane and the list's row both fit the same console 640 px tall.</summary>
    /// <remarks>
    ///     ⚠ <b>The size where #1390's first fix still cut the pane.</b> Here the console is 97 px: a
    ///     41 px toolbar and a 22 px row leave 34 px, and a pane with a 48 px floor went 11 px through
    ///     the dock panel's bottom edge — the end of its track and its horizontal bar behind that edge
    ///     again, which is the original symptom at a shorter console. Found by a reviewer's probe
    ///     that ran <see cref="Check" /> at four sizes; the fact above only drew this one empty.
    /// </remarks>
    [Fact]
    public void A_selected_record_s_detail_and_a_row_of_the_list_fit_a_console_640_px_tall() {
        using var fixture = Start(Width, 640);

        var (console, view) = SelectDeepError(fixture);

        Assert.False(view.HasClass("empty"), "no record is selected, so this is the empty pane");
        Assert.True(
            console.List.Height >= console.List.RowHeight,
            $"with a record selected the console's list is {console.List.Height} px tall — not one "
            + $"{console.List.RowHeight} px row left to select from at {WidthOf(fixture)}×{HeightOf(fixture)}."
        );

        Check(fixture, view, "console-detail-640");
    }

    /// <summary>Whether the vertical thumb is drawn at the far end of its track once the view is scrolled there.</summary>
    /// <remarks>
    ///     <para>
    ///         The colour is sampled rather than assumed, in the same renderer's picture, at a point
    ///         of the track the thumb covers at the top of the scroll and has left at the end: half
    ///         its travel down, and never deeper than half its length. The thumb's length is
    ///         <c>ScrollBar</c>'s own rule — a floor of 24 px or half the track over a proportional
    ///         length — so where it must be at the end is closed form.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>With both bars shown the thumb ends where the horizontal bar begins, and all of it
    ///         is required.</b> Until #1401 the horizontal track was painted over the last 10 px of
    ///         the vertical one, the thumb travelled into them, and this asked for a third of it — at
    ///         1280×800 the console pane's end thumb was 748–760 over a track at 761–770. The end of
    ///         the track is measured off the other bar's box, so the oracle is not the control's own
    ///         arithmetic read back.
    ///     </para>
    /// </remarks>
    static void ThumbAtTheEnd(string renderer, ScrollView view, Bitmap top, Bitmap bottom) {
        // The pixels at the two ends of the thumb that are not its fill colour: a pixel of
        // antialiasing at each end of the pill, and since #1414 the ring off `--thumb-border-color`
        // inside each end. #1401's defect hid eleven pixels of the fill, which four cannot excuse.
        const int Ends = 4;

        var bar = view.VerticalBar;
        var x = (int)MathF.Floor(bar.AbsoluteLeft + bar.Width / 2f);
        var start = (int)MathF.Ceiling(bar.AbsoluteTop);
        var limit = (int)MathF.Floor(bar.AbsoluteTop + bar.Height);
        var end = view.MaximumLeft > 0f ? (int)MathF.Floor(view.HorizontalBar.AbsoluteTop) : limit;
        var length = (int)MathF.Floor(MathF.Max(MathF.Min((end - start) / 2f, 24f), (end - start) * bar.ViewportSize / bar.ContentSize));
        var travel = end - start - length;

        Assert.True(travel >= 4, $"[{renderer}] a thumb that travels {travel} px cannot be told from one that did not move.");

        var sample = start + Math.Clamp(travel / 2, 2, length / 2);
        var thumb = Pixel(top, x, sample);

        Assert.False(
            thumb.SequenceEqual(Pixel(bottom, x, sample)),
            $"[{renderer}] the top of the track looks the same at both ends of the scroll, so the colour sampled "
            + "there is not the thumb's and the count below would measure nothing."
        );

        var found = new List<int>();

        // ⚠ Clamped to the picture: a track that runs off the bottom of the window is the defect
        // this looks for at its worst, and a row nobody can see holds no thumb.
        for (var y = start; y < Math.Min(limit, bottom.Height); y++) {
            if (Pixel(bottom, x, y).SequenceEqual(thumb)) {
                found.Add(y);
            }
        }

        Assert.True(
            found.Count >= length - Ends,
            $"[{renderer}] scrolled to the end, the vertical thumb has {found.Count} pixels in column {x} of its "
            + $"track ({start}–{end}); a {length} px thumb belongs at {end - length}–{end}. The rest of it is drawn "
            + "somewhere nothing shows it."
        );

        Assert.True(
            found[0] >= end - length - 1,
            $"[{renderer}] scrolled to the end, the vertical thumb starts at y {found[0]}, above the last {length} px "
            + $"of its track ({end - length}–{end})."
        );

        Assert.True(
            found[^1] < end,
            $"[{renderer}] scrolled to the end, the vertical thumb runs to y {found[^1]}, into the horizontal bar at {end}."
        );
    }

    static byte[] Pixel(Bitmap picture, int x, int y) => picture.Pixels.AsSpan(((y * picture.Width) + x) * 4, 4).ToArray();

    /// <summary>Opens the console, logs an error with a stack forty frames deep and selects it.</summary>
    static (Vixen.Editor.Ui.ConsoleView Console, ScrollView Detail) SelectDeepError(EditorSession fixture) {
        fixture.Open("console");

        Sink(fixture)
            .CreateLogger("Vixen.Editor.Pictures")
            .Log(LogLevel.Error, default, "it went wrong", Deep(40), static (state, _) => state);
        fixture.Frames(2);

        var console = Find<Vixen.Editor.Ui.ConsoleView>(fixture.Document.Root)
            ?? throw fixture.Fail("the console is not open");

        var row = console.List.Rows.FirstOrDefault(candidate => !candidate.HasClass("parked"))
            ?? throw fixture.Fail("the console realised no row for the error");

        fixture.Click(row);
        fixture.Frames(2);

        return (console, Scroller(fixture, "console-detail"));
    }

    /// <summary>The message log's detail pane, over a detail forty lines long.</summary>
    [Fact]
    public void The_message_log_detail_scrolls_inside_its_box_and_nowhere_else() {
        using var fixture = Start();

        fixture.Open(EditorShell.MessageLogPanel);

        var detail = string.Join(
            "\n",
            Enumerable.Range(1, 40).Select(line => $"line {line} of a detail far longer than the pane")
        );

        // ⚠ An error's toast stays twelve seconds and sits over the log's first row, and a toast is
        // not what is being pictured: expire it at once, so the click reaches the row and the two
        // frames are of the panel alone.
        fixture.Shell.Notifications.ErrorDuration = TimeSpan.FromMilliseconds(1);
        fixture.Shell.Notifications.Error("Could not import the texture", detail);
        fixture.Frames(4);

        var log = fixture.Shell.Messages ?? throw fixture.Fail("the message log is not open");

        var row = log.List.Rows.FirstOrDefault(candidate => !candidate.HasClass("parked"))
            ?? throw fixture.Fail("the message log realised no row for the error");

        fixture.Click(row);
        fixture.Frames(2);

        // ⚠ A real click, and until the log's rows were markup it selected nothing: the hand-built row
        // listened for `ClickEvent`, which only a `Control` raises, so a person pressing a bare row
        // produced a `TapEvent` nobody heard and the detail pane this test pictures could be filled
        // from code and from nowhere else. The first version of this test raised the `ClickEvent`
        // itself to get past it. The `@rows` row's `on:click` is a tap, which is what a plain element
        // hears (#758).
        Assert.True(
            log.Selected is { Severity: NotificationSeverity.Error },
            $"clicking the row selected {log.Selected?.Message ?? "nothing"}, so the detail pane is empty and "
            + "there is nothing in it to scroll."
        );

        Check(fixture, Scroller(fixture, "message-log-detail"), "message-log-detail");
    }

    /// <summary>The preferences window's page, on the page taller than a short window.</summary>
    /// <remarks>
    ///     ⚠ <b>640 px, because that is where it goes wrong.</b> The Appearance page is a button, a
    ///     sentence and a theme editor that will not shrink below 220 px, and at this height the pane is
    ///     229 px — so before the pane was a <c>ScrollView</c> the theme editor was cut off at the
    ///     bottom with nothing to reach it. The General page fits at this height and drew the same
    ///     before and after, so it is not the page this looks at.
    /// </remarks>
    [Fact]
    public void The_settings_page_scrolls_inside_its_box_and_nowhere_else() {
        using var fixture = Start(Width, 640);

        var view = fixture.Control<SettingsView>("preferences");

        Assert.True(view.Select("appearance"), "the preferences window has no Appearance page");
        fixture.Settle();

        Check(fixture, Scroller(fixture, "settings-pane"), "settings-pane");
    }

    /// <summary>The sprite editor's list, over a sheet cut into more sprites than the column holds.</summary>
    /// <remarks>
    ///     ⚠ <b>A column, which is the case the ledger held back for a picture.</b> The list sits
    ///     under a bar and above the fields in <c>sprite-side</c>, and a <c>ScrollView</c> that grows
    ///     in a column from a content basis grows to its content: the bar never appears and the
    ///     fields are pushed off the bottom. What this proves is the other outcome.
    /// </remarks>
    [Fact]
    public void The_sprite_list_scrolls_inside_its_box_and_nowhere_else() {
        using var fixture = Start();

        var relative = "Assets/sheet.png";
        var absolute = fixture.Project.Paths.Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var opaque = new byte[128 * 128 * 4];
        Array.Fill(opaque, (byte)255);

        File.WriteAllBytes(absolute, Vixen.Editor.Assets.Tests.MinimalPng.Write(128, 128, opaque));
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out var entry));

        fixture.Editor.OpenAsset(entry.Guid);
        fixture.Frames(2);

        // The texture opens on its Texture tab; the sprite editor is the second.
        fixture.Click(
            Descendants(fixture.Document.Root).FirstOrDefault(element => element.Text == "Sprites")
            ?? throw fixture.Fail("the texture document has no Sprites tab")
        );

        fixture.Frames(2);

        var sprites = Find<Vixen.Editor.AssetEditors.Importing.SpriteSheetView>(fixture.Document.Root)
            ?? throw fixture.Fail("opening a texture opened no sprite editor");

        sprites.CellWidth.Number = 16;
        sprites.CellHeight.Number = 16;

        Assert.Equal(64, sprites.Slice());
        fixture.Frames(2);

        var list = Scroller(fixture, "sprite-list");

        // ⚠ And it reaches the bottom of its tab. Scrolling is not the whole claim: with the texture
        // document's tab set sized to its content, the list scrolled perfectly inside a 94 px box
        // above four hundred pixels of nothing, because nothing between the document and the list
        // was height-bound. Thirty-two pixels is three paddings with room to spare, against the
        // two hundred and more that the unbound chain leaves.
        var document = Ancestors(list).First(ancestor => ancestor.Tag == "texture-editor");
        var slack = (document.AbsoluteTop + document.Height) - (list.AbsoluteTop + list.Height);

        Assert.True(
            slack <= 32f,
            $"the sprite list stops {slack:0} px short of the bottom of its document, so the tab set is not filling it."
        );

        Check(fixture, list, "sprite-list");
    }

    /// <summary>The texture document's Texture tab, down to the import settings under the mip ladder.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>#1393: the settings were below the ladder, past the tab's clipped edge, with
    ///         nothing to reach them.</b> The tab held the preview, the alert, the channel bar, the
    ///         facts, the ladder and then <c>ImportSettingsView</c>, in a <c>document-tabs</c> tab set
    ///         bound to the document's height inside a <c>texture-editor</c> that clips — so max size,
    ///         compression and the platform overrides, everything a person opens a texture to change,
    ///         were drawn off the bottom of the panel.
    ///     </para>
    ///     <para>
    ///         A 128×128 texture has an eight-rung ladder, and at the session's 1600×1000 its last rung
    ///         sits on the document's lower edge: the size #1275's picture was taken at.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_texture_tab_scrolls_down_to_its_import_settings() {
        using var fixture = Start();

        var relative = "Assets/ladder.png";
        var absolute = fixture.Project.Paths.Absolute(relative);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        var opaque = new byte[128 * 128 * 4];
        Array.Fill(opaque, (byte)255);

        File.WriteAllBytes(absolute, Vixen.Editor.Assets.Tests.MinimalPng.Write(128, 128, opaque));
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out var entry));

        fixture.Editor.OpenAsset(entry.Guid);
        fixture.Frames(2);

        var texture = Find<Vixen.Editor.AssetEditors.Importing.TextureImportView>(fixture.Document.Root)
            ?? throw fixture.Fail("opening a texture opened no texture view");

        Assert.Equal(8, texture.Ladder.Children.Count);

        var settings = texture.SettingsView;
        var document = Ancestors(settings).First(ancestor => ancestor.Tag == "texture-editor");

        Draw(fixture, null, "texture-tab-top");

        // ⚠ The scroller that owns the settings, and it must be inside the document: the panel or
        // the window scrolling would not be the tab reaching its own content.
        var page = Ancestors(settings)
            .TakeWhile(ancestor => !ReferenceEquals(ancestor, document))
            .OfType<ScrollView>()
            .FirstOrDefault();

        Assert.True(
            page is not null,
            $"nothing between the texture document and its import settings scrolls, and the settings start "
            + $"{settings.AbsoluteTop - (document.AbsoluteTop + document.Height):0} px below the document's bottom edge."
        );

        Check(fixture, page!, "texture-page");

        // Scrolled to its end, the whole of the settings is inside the document: the last section is
        // what a person scrolls for, and a scroller that stopped short of it would pass the check above.
        page!.ScrollTo(page.MaximumTop, 0f);
        fixture.Frames(2);

        Draw(fixture, null, "texture-tab-bottom");

        // One bar and not one inside another: the settings' own scroller, in content with no height
        // bound, grows to what it holds and has nothing beyond a fold of its own.
        Assert.True(
            settings.Scroll.MaximumTop < 1f,
            $"the import settings' own scroller has {settings.Scroll.MaximumTop:0} px beyond its fold inside the tab's."
        );

        var bottom = settings.AbsoluteTop + settings.Height;

        Assert.True(
            bottom <= document.AbsoluteTop + document.Height + 0.5f,
            $"at the end of the tab's scroll the import settings still end {bottom - (document.AbsoluteTop + document.Height):0} px below the document."
        );
    }

    /// <summary>The scene document's Compiled tab, over more archetypes than the tab holds.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not a <c>ScrollView</c>, and the ledger's premise for this site is refuted.</b>
    ///         <c>compiled-scene-blocks</c> said <c>overflow-y: auto</c>, which in this UI clips and does
    ///         not scroll — but it never clipped here either. The scene document's dock panel scrolls
    ///         as a whole, and <c>dock-panel.scrolls &gt; *</c> keeps the tab set from shrinking, so the
    ///         table was always as tall as its rows and the last block was reached by the panel's
    ///         bar. A <c>ScrollView</c> was tried first and its bar never appeared, for that reason.
    ///         What this pins is the claim that matters to a person: every block is inside its table,
    ///         and the panel scrolls far enough to show the last one.
    ///     </para>
    ///     <para>
    ///         Thirty-one archetypes, from every non-empty combination of five components, is the
    ///         shape a real level has and a stock project does not: its four entities make three
    ///         blocks and fit.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_compiled_scene_blocks_are_all_reached_by_the_panel_s_own_scroll() {
        using var fixture = Start();

        PictureA.Register();

        var asset = fixture.Project.Assets.Entries.First(entry => entry.Path.EndsWith(".vxscene", StringComparison.Ordinal));

        fixture.Editor.OpenAsset(asset.Guid);
        fixture.Frames(2);

        fixture.Click(
            Descendants(fixture.Document.Root).FirstOrDefault(element => element.Text == "Compiled")
            ?? throw fixture.Fail("the scene document has no Compiled tab")
        );

        fixture.Frames(2);

        var view = Find<Vixen.Editor.AssetEditors.Scenes.CompiledSceneView>(fixture.Document.Root)
            ?? throw fixture.Fail("the scene document built no compiled view");

        // Into the document the tab is showing. ⚠ A first cut wrote into `EditorSession.Scene` and
        // compiled four blocks, because opening the scene file built a second document over it
        // (#1395). It is the editor's own scene now, and the reflection stays to say so.
        var scene = (Vixen.Editor.SceneView.SceneDocument)typeof(Vixen.Editor.AssetEditors.Scenes.CompiledSceneView)
            .GetField("document", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(view)!;

        Assert.Same(fixture.Scene, scene);

        for (var mask = 1; mask < 32; mask++) {
            var entity = scene.Create($"Combination {mask}", Vixen.Engine.Transforms.LocalTransform.Identity);

            PictureA.Add(scene.World, entity, mask);
        }

        Assert.True(view.Refresh(), "the scene did not compile");
        fixture.Frames(2);

        Assert.True(view.Content!.Blocks.Length >= 31, $"only {view.Content.Blocks.Length} blocks, so nothing is below the fold");

        var blocks = Descendants(view).First(element => element.Tag == "compiled-scene-blocks");
        var last = blocks.Children[^1];

        // Every row inside its table: a table that clipped would hold its last rows below its own edge.
        Assert.True(
            last.AbsoluteTop + last.Height <= blocks.AbsoluteTop + blocks.Height + 0.5f,
            "the last block is below the bottom of its own table, so the table cuts its rows off."
        );

        // And the panel scrolls far enough to show it.
        var panel = Ancestors(view).OfType<Vixen.Ui.Controls.Advanced.DockPanel>().First();

        Draw(fixture, null, "compiled-scene-top");

        panel.ScrollTo(float.MaxValue);
        fixture.Frames(2);

        Draw(fixture, null, "compiled-scene-bottom");

        Assert.True(panel.ScrollTop > 0f, "the scene document's panel did not scroll, so nothing reaches the last blocks.");

        Assert.True(
            last.AbsoluteTop + last.Height <= panel.AbsoluteTop + panel.Height + 0.5f,
            $"at the bottom of the panel's scroll the last block still ends {last.AbsoluteTop + last.Height - (panel.AbsoluteTop + panel.Height):0} px below it."
        );
    }

    /// <summary>The mixer's strips, sideways, over more buses than the body is wide.</summary>
    /// <remarks>
    ///     ⚠ <b>The first conversion on the other axis.</b> Strips sit side by side and a fader's
    ///     travel is the strip's height, so the content has to stretch to the view's height as well as
    ///     run past its width — a scroll content that was only as tall as its tallest strip's minimum
    ///     would shrink every fader to its 120 px floor. Hence the fader-height assertion beside the
    ///     scroll one.
    /// </remarks>
    [Fact]
    public void The_mixer_strips_scroll_sideways_inside_their_box_and_nowhere_else() {
        using var fixture = Start();

        var absolute = fixture.Project.Paths.Absolute("Assets/Game.vxmixer");

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, string.Empty);
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/Game.vxmixer", out var entry));

        fixture.Editor.OpenAsset(entry.Guid);
        fixture.Frames(2);

        var mixer = Find<Vixen.Editor.AssetEditors.Audio.AudioMixerView>(fixture.Document.Root)
            ?? throw fixture.Fail("opening a mixer opened no mixer view");

        for (var bus = 0; bus < 16; bus++) {
            fixture.Click(mixer.AddBus);
        }

        fixture.Frames(2);

        var strips = Scroller(fixture, "mixer-strips");

        // The faders take the strips' height, as they did before the strips were in a scroller.
        var fader = Descendants(strips).OfType<Slider>().First();

        Assert.True(
            fader.Height > 160f,
            $"a fader is {fader.Height:0} px tall in a {strips.Height:0} px mixer, so the strips no longer stretch to the view."
        );

        Check(fixture, strips, "mixer-strips", sideways: true);

        // ⚠ The side column keeps its 300 px beside sixteen buses (#1397). The strips' basis was their
        // content, so the column shrank with every bus: 143 px here, its analysis line cut to
        // "19 bus(e". Asserted only where the body has room for the column, a gap and one strip, and
        // after `Check` so that the pictures are written whichever way this comes out.
        var body = strips.Parent!;
        var side = Scroller(fixture, "mixer-side");

        Assert.True(body.Width > 300f + 6f + 76f, $"the mixer body is {body.Width:0} px, too narrow to say anything");
        Assert.True(
            MathF.Abs(side.Width - 300f) < 0.5f,
            $"the side column is {side.Width:0} px of its 300 in a {body.Width:0} px body, beside strips {strips.Width:0} px wide."
        );

        // ⚠ And nothing in it is wider than it. At its full 300 px the analysis line was still cut, to
        // "19 bus(es), 0 snapshot(s). Bui", because a scroll content is as wide as its widest unwrapped
        // line: 330 px here. The column scrolls down, so the line wraps rather than scrolling across.
        var message = Descendants(side).First(element => element.Tag == "analysis-message");

        Assert.True(
            side.MaximumLeft == 0f,
            $"the side column's content is {side.Content.Width:0} px in a {side.Width:0} px column, so its "
            + $"lines run under a sideways bar: the analysis line is {message.Width:0} px wide and reads '{message.Text}'."
        );

        Assert.True(
            message.Height > 30f,
            $"the analysis line is {message.Height:0} px tall in {message.Width:0} px, so it was cut rather than wrapped."
        );
    }

    /// <summary>The input debug panel, over more rows than the panel is tall.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ledger kept <c>input-debug</c> for a scroller inside the view, and it needed
    ///         none.</b> The panel's dock panel scrolls as a whole (<c>dock-panel.scrolls</c>) and the
    ///         view is its direct child, so <c>dock-panel.scrolls &gt; * { flex-shrink: 0 }</c> makes
    ///         the view as tall as its rows and the panel's bar reaches the last. The view's own
    ///         <c>overflow-y: auto</c> never clipped a row; it only raised 7009 on every open.
    ///     </para>
    ///     <para>
    ///         The rows are stood in for by the first list's height, because a headless session has no
    ///         devices to list — the property under test is the panel's, not the rows'.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_input_debug_view_is_reached_by_the_panel_s_own_scroll() {
        using var fixture = Start();

        var view = fixture.Control<Vixen.Editor.AssetEditors.Input.InputDebugView>(
            Vixen.Editor.AssetEditors.AssetEditorsModule.InputDebugPanelId
        );

        view.Devices.SetStyle("min-height", "1400px");
        fixture.Frames(2);

        var last = view.Actions;

        Assert.True(
            last.AbsoluteTop + last.Height <= view.AbsoluteTop + view.Height + 0.5f,
            "the last list is below the bottom of the view, so the view cuts its content off."
        );

        var panel = Ancestors(view).OfType<Vixen.Ui.Controls.Advanced.DockPanel>().First();

        Draw(fixture, null, "input-debug-top");

        panel.ScrollTo(float.MaxValue);
        fixture.Frames(2);

        Draw(fixture, null, "input-debug-bottom");

        Assert.True(panel.ScrollTop > 0f, "the input debug panel did not scroll, so nothing reaches its last list.");

        Assert.True(
            last.AbsoluteTop + last.Height <= panel.AbsoluteTop + panel.Height + 0.5f,
            $"at the bottom of the panel's scroll the last list still ends {last.AbsoluteTop + last.Height - (panel.AbsoluteTop + panel.Height):0} px below it."
        );
    }

    /// <summary>A model's platform-override grid, sideways, over more targets than the panel is wide.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The targets are added with the import settings scrolled down to the grid, the way
    ///         a person reaches it.</b> They used to be added at the top of the scroll as a workaround
    ///         for a crash: <c>ScrollView.Holds</c> walked <c>Parent</c>, which a removed element
    ///         keeps, so a view anchored on a row a rebuild had removed asked that row for its
    ///         position on the next settle and <c>UiElement.Document</c> threw (#1392). That is fixed
    ///         in <c>ScrollView</c> and pinned by
    ///         <c>ScrollAnchoringTests.A_removed_anchor_is_dropped_and_anchoring_carries_on</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This test is not that fix's regression test, and it stays green with the fix
    ///         taken out.</b> The grid's rows are the content of <c>override-body</c>, a
    ///         <c>ScrollView</c> of its own since #1275's conversion, and the settings view's anchor
    ///         walk stops at a nested scroll view — so the rows the rebuild removes can no longer be
    ///         that view's anchor. The conversion and the crash's report landed in one commit, so the
    ///         crash was most likely seen before the rows moved into that nested view; it is real of
    ///         any view anchored on a row that is rebuilt, and it is the unit test that shows it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_override_grid_scrolls_sideways_inside_its_box_and_nowhere_else() {
        using var fixture = Start();

        var absolute = fixture.Project.Paths.Absolute("Assets/Crate.gltf");

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "{\"asset\":{\"version\":\"2.0\"}}");
        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/Crate.gltf", out var entry));

        fixture.Editor.OpenAsset(entry.Guid);
        fixture.Frames(2);

        var first = Find<Vixen.Editor.AssetEditors.Importing.TargetOverrideMatrix>(fixture.Document.Root)
            ?? throw fixture.Fail("the model document has no platform-override grid");

        var settings = Ancestors(first).OfType<ScrollView>().First();

        settings.ScrollIntoView(first);
        fixture.Frames(2);

        // The premise: a view at the top is never anchored, so without a scroll the rebuild below
        // would prove nothing about a removed anchor.
        Assert.True(settings.ScrollTop > 0f, "the import settings did not scroll down to the override grid");

        foreach (var target in (ReadOnlySpan<string>)["Android", "iOS", "Switch", "WebGPU", "Windows", "Linux"]) {
            // Found again each time: adding a target rebuilds what is under the matrix.
            var matrix = Find<Vixen.Editor.AssetEditors.Importing.TargetOverrideMatrix>(fixture.Document.Root)
                ?? throw fixture.Fail("the model document has no platform-override grid");

            matrix.TargetName.Value = target;
            matrix.AddTarget.Activate();
            fixture.Frames(1);
        }

        fixture.Frames(2);

        var grid = Scroller(fixture, "override-body");

        // Brought on screen by the settings' own scroller, the way a person would reach it.
        Ancestors(grid).OfType<ScrollView>().First().ScrollIntoView(grid);
        fixture.Frames(2);

        Check(fixture, grid, "override-body", sideways: true);
    }

    internal static EditorSession Start(int width = Width, int height = Height) =>
        EditorSession.Start(new EditorSessionOptions { Width = width, Height = height });

    /// <summary>Draws the editor at the top of the scroll and at the bottom, and holds the difference to the view.</summary>
    static void Check(EditorSession fixture, ScrollView view, string name, bool sideways = false) {
        fixture.Frames(2);

        var reach = sideways ? view.MaximumLeft : view.MaximumTop;

        Assert.True(
            reach > 0f,
            $"<{view.Tag}> has nothing beyond its fold ({view.Content.Width}×{view.Content.Height} px of content in "
            + $"{view.Width}×{view.Height} px), so a scroll moves nothing and the comparison below proves nothing."
        );

        var box = Box(view);

        Uncut(fixture, view, sideways);

        // ⚠ And the bars are the view's. They are absolutely positioned, so their containing block is
        // the nearest *positioned* ancestor: a view under a tag of its own that lost the user-agent
        // rule's `position: relative` hangs its bars off whatever is positioned above it — the
        // console's bar then spans the whole console, its thumb sits under the toolbar at the top of
        // the scroll and is cut away, and the pixel oracle below is blind to it because the view's
        // own clip hides the difference. Seen, not supposed: that is exactly the picture with the
        // declaration removed.
        foreach (var bar in view.Children.OfType<ScrollBar>()) {
            // One pixel of slack, and it is the layout's rounding rather than tolerance for the defect:
            // a view 927.4 px wide rounds to 927 while its `right: 0` bar rounds to 918 + 10, so every
            // bar in the editor overhangs its view's right edge by a pixel the view's clip then cuts.
            // The anchoring this looks for is off by the height of a toolbar, not by one.
            const float Slack = 1f;

            var inside = bar.AbsoluteLeft >= view.AbsoluteLeft - Slack
                && bar.AbsoluteTop >= view.AbsoluteTop - Slack
                && bar.AbsoluteLeft + bar.Width <= view.AbsoluteLeft + view.Width + Slack
                && bar.AbsoluteTop + bar.Height <= view.AbsoluteTop + view.Height + Slack;

            Assert.True(
                inside,
                $"<{view.Tag}>'s {bar.Orientation} bar {Exact(bar)} is not inside the view {Exact(view)}, so it is anchored to "
                + "an ancestor rather than to the view: the view has lost the user-agent rule's `position: "
                + "relative` (Rikarin/Vixen#1327)."
            );
        }

        using var device = OpenDevice();
        using var gpu = device is null ? null : new GpuPicture(device, WidthOf(fixture), HeightOf(fixture));

        view.ScrollTo(0f, 0f);
        fixture.Frames(2);

        var top = Draw(fixture, gpu, $"{name}-top");

        view.ScrollTo(sideways ? 0f : reach, sideways ? reach : 0f);
        fixture.Frames(2);

        Assert.True(
            (sideways ? view.ScrollLeft : view.ScrollTop) > 0f,
            $"<{view.Tag}> did not move when it was scrolled to {reach}."
        );

        var bottom = Draw(fixture, gpu, $"{name}-bottom");

        Assert.Multiple(
            () => Oracle("software", view, box, top.Software, bottom.Software),
            () => {
                if (top.Gpu is { } before && bottom.Gpu is { } after) {
                    Oracle("Vulkan", view, box, before, after);
                }
            },
            () => {
                if (!sideways) {
                    ThumbAtTheEnd("software", view, top.Software, bottom.Software);
                }
            },
            () => {
                if (!sideways && top.Gpu is { } before && bottom.Gpu is { } after) {
                    ThumbAtTheEnd("Vulkan", view, before, after);
                }
            }
        );
    }

    /// <summary>Whether the whole of an element is inside every ancestor that clips it.</summary>
    /// <remarks>
    ///     ⚠ A view that scrolls and clips perfectly but is itself cut by the panel it sits in hides
    ///     the bottom of its own scroll — the last lines and the thumb that says there are any —
    ///     behind the panel's edge, and the difference oracle cannot see that: the rows it hides are
    ///     the same in both frames.
    /// </remarks>
    static void Uncut(EditorSession fixture, UiElement view, bool sideways = false) {
        var box = Box(view);

        foreach (var ancestor in Ancestors(view)) {
            if (!Clips(fixture, ancestor)) {
                continue;
            }

            var cut = Box(ancestor);

            // ⚠ Along the scroll's own axis only when it runs sideways. A sideways scroller may be
            // taller than a vertical one it sits in — the override grid is, inside the import
            // settings' region — and that is the outer view's business, reached by the outer bar.
            var across = box.Left >= cut.Left && box.Right <= cut.Right;
            var down = box.Top >= cut.Top && box.Bottom <= cut.Bottom;

            Assert.True(
                sideways ? across : across && down,
                $"<{view.Tag}> {box} is cut by <{ancestor.Tag}> {cut}, so the far end of its scroll is behind "
                + $"that element's edge at {WidthOf(fixture)}×{HeightOf(fixture)}."
            );
        }
    }

    static void Oracle(string renderer, ScrollView view, (int Left, int Top, int Right, int Bottom) box, Bitmap before, Bitmap after) {
        var inside = 0;
        var outside = 0;
        (int X, int Y)? first = null;

        for (var y = 0; y < before.Height; y++) {
            for (var x = 0; x < before.Width; x++) {
                var at = ((y * before.Width) + x) * 4;

                if (before.Pixels.AsSpan(at, 4).SequenceEqual(after.Pixels.AsSpan(at, 4))) {
                    continue;
                }

                if (x >= box.Left && x < box.Right && y >= box.Top && y < box.Bottom) {
                    inside++;
                } else {
                    outside++;
                    first ??= (x, y);
                }
            }
        }

        Assert.True(
            inside > 0,
            $"[{renderer}] scrolling <{view.Tag}> changed no pixel inside it, so what the reader sees did not move."
        );

        Assert.True(
            outside == 0,
            $"[{renderer}] scrolling <{view.Tag}> changed {outside} pixels outside its box {box} — the first at "
            + $"{first} — so its content draws over whatever is beside it. A ScrollView under a tag of its own "
            + "has lost the user-agent rule's `overflow: hidden` (Rikarin/Vixen#1327)."
        );
    }

    /// <summary>The view's border box in whole pixels, rounded outwards.</summary>
    static (int Left, int Top, int Right, int Bottom) Box(UiElement view) =>
        (
            (int)MathF.Floor(view.AbsoluteLeft),
            (int)MathF.Floor(view.AbsoluteTop),
            (int)MathF.Ceiling(view.AbsoluteLeft + view.Width),
            (int)MathF.Ceiling(view.AbsoluteTop + view.Height)
        );

    internal static (Bitmap Software, Bitmap? Gpu) Draw(EditorSession fixture, GpuPicture? gpu, string name) {
        var glyphs = new GlyphFieldCache(new GlyphAtlas(1024, 1024));
        var (width, height) = (WidthOf(fixture), HeightOf(fixture));
        var geometry = new UiGeometryBuilder().Build(fixture.Document.Drawing, glyphs, new Rectangle(0, 0, width, height));

        var software = SoftwareUiRasterizer.Render(geometry, glyphs.Atlas, width, height, Background);
        var hardware = gpu?.Render(geometry, glyphs.Atlas);

        if (Destination is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, $"{name}-software.png"), software);

            if (hardware is { } picture) {
                PngCodec.Save(Path.Combine(directory, $"{name}-vulkan.png"), picture);
            }
        }

        return (software, hardware);
    }

    internal static VulkanDevice? OpenDevice() {
        if (VulkanDevice.TryCreate(new(), out var device, out var reason)) {
            return device!;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        return null;
    }

    /// <summary>The editor's log ring, which the console reads and nothing public writes an exception into.</summary>
    static Vixen.Core.Diagnostics.RingBufferSink Sink(EditorSession fixture) {
        var field = typeof(EditorApplication).GetField("log", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw fixture.Fail("EditorApplication has no `log` field for the console to read");

        return ((EditorLog)field.GetValue(fixture.Editor)!).Sink;
    }

    static string Exact(UiElement element) =>
        FormattableString.Invariant(
            $"({element.AbsoluteLeft:0.##}, {element.AbsoluteTop:0.##}, {element.AbsoluteLeft + element.Width:0.##}, {element.AbsoluteTop + element.Height:0.##})"
        );

    static IEnumerable<UiElement> Ancestors(UiElement element) {
        for (var parent = element.Parent; parent is not null; parent = parent.Parent) {
            yield return parent;
        }
    }

    /// <summary>Whether an element cuts what hangs outside it, on either axis.</summary>
    static bool Clips(EditorSession fixture, UiElement element) {
        foreach (var property in (ReadOnlySpan<string>)["overflow", "overflow-x", "overflow-y"]) {
            if (fixture.Ui.StyleOf(element, property) is "hidden" or "clip" or "auto" or "scroll") {
                return true;
            }
        }

        return false;
    }

    static ScrollView Scroller(EditorSession fixture, string tag) =>
        Descendants(fixture.Document.Root).OfType<ScrollView>().SingleOrDefault(view => view.Tag == tag)
        ?? throw fixture.Fail($"no ScrollView under <{tag}> is on screen");

    static T? Find<T>(UiElement element) where T : UiElement =>
        Descendants(element).OfType<T>().FirstOrDefault();

    static IEnumerable<UiElement> Descendants(UiElement element) {
        yield return element;

        foreach (var child in element.Children) {
            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }

    static Exception Deep(int frames) {
        try {
            Recurse(frames);
        } catch (InvalidOperationException caught) {
            return caught;
        }

        throw new InvalidOperationException("the recursion did not throw");

        static void Recurse(int remaining) {
            if (remaining == 0) {
                throw new InvalidOperationException("because of this");
            }

            Recurse(remaining - 1);
        }
    }

    /// <summary>One device, one renderer, one target, drawn into and read back once per picture.</summary>
    internal sealed class GpuPicture(VulkanDevice device, int width, int height) : IDisposable {
        readonly UiRenderer renderer = new(device, UiShaderLibrary.Load(device), new RenderOutput([PixelFormat.Rgba8UNorm]));

        public Bitmap Render(in UiGeometry geometry, GlyphAtlas atlas) {
            VulkanDiagnostics.Reset();

            var target = device.CreateTexture(
                new(
                    PixelFormat.Rgba8UNorm,
                    width,
                    height,
                    TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.CopySource,
                    Name: "scrolling panel picture"
                )
            );

            var view = device.CreateTextureView(target);
            var bytes = width * height * 4;
            var readback = device.CreateBuffer(new(bytes, BufferUsage.CopyDestination, MemoryAccess.HostReadback, "readback"));

            device.BeginFrame();

            using (var commands = device.BeginCommandList(QueueKind.Graphics, "scrolling panel picture")) {
                renderer.Upload(commands, geometry, atlas);
                renderer.Compose(commands, geometry, new Int2(width, height), beneath: new UiBackdropSource(Background));

                commands.Barrier(
                    new BarrierGroup([], [new TextureBarrier(target, ResourceState.Undefined, ResourceState.ColourTarget)])
                );

                commands.BeginRenderPass(
                    new([new ColourAttachment(view, LoadAction.Clear, StoreAction.Store, Background)], name: "scrolling panel picture")
                );

                renderer.Record(commands, geometry, new Int2(width, height));

                commands.EndRenderPass();

                commands.Barrier(
                    new BarrierGroup([], [new TextureBarrier(target, ResourceState.ColourTarget, ResourceState.CopySource)])
                );

                commands.CopyTextureToBuffer(new TextureRegion(target), new(width, height, 1), readback, 0);

                commands.Finish();
                device.GraphicsQueue.Submit([commands]);
            }

            device.EndFrame();
            device.WaitIdle();

            var pixels = new byte[bytes];

            device.Read(readback, 0, pixels);

            device.Destroy(readback);
            device.Destroy(view);
            device.Destroy(target);

            Assert.True(
                VulkanDiagnostics.ErrorCount == 0,
                "the frame produced validation errors, so its pixels mean nothing: "
                + string.Join(Environment.NewLine, VulkanDiagnostics.Messages)
            );

            return new Bitmap(width, height, pixels);
        }

        public void Dispose() => renderer.Dispose();
    }
}

/// <summary>Five components whose combinations make a scene of thirty-one archetypes.</summary>
static class PictureA {
    public static void Register() {
        Vixen.Engine.Scenes.SceneComponentRegistry.Register<PictureA1>();
        Vixen.Engine.Scenes.SceneComponentRegistry.Register<PictureA2>();
        Vixen.Engine.Scenes.SceneComponentRegistry.Register<PictureA3>();
        Vixen.Engine.Scenes.SceneComponentRegistry.Register<PictureA4>();
        Vixen.Engine.Scenes.SceneComponentRegistry.Register<PictureA5>();
    }

    /// <summary>Gives an entity the components whose bits are set in <paramref name="mask" />.</summary>
    public static void Add(Vixen.Ecs.World world, Vixen.Core.Entity entity, int mask) {
        if ((mask & 1) != 0) {
            world.Add(entity, new PictureA1 { Value = mask });
        }

        if ((mask & 2) != 0) {
            world.Add(entity, new PictureA2 { Value = mask });
        }

        if ((mask & 4) != 0) {
            world.Add(entity, new PictureA3 { Value = mask });
        }

        if ((mask & 8) != 0) {
            world.Add(entity, new PictureA4 { Value = mask });
        }

        if ((mask & 16) != 0) {
            world.Add(entity, new PictureA5 { Value = mask });
        }
    }
}

/// <summary>One of <see cref="PictureA" />'s five.</summary>
[Vixen.Core.Component]
[Vixen.Core.DataContract("ScrollingPanelPictureA1")]
public struct PictureA1 {
    /// <summary>Something to store.</summary>
    public int Value { get; set; }
}

/// <summary>One of <see cref="PictureA" />'s five.</summary>
[Vixen.Core.Component]
[Vixen.Core.DataContract("ScrollingPanelPictureA2")]
public struct PictureA2 {
    /// <summary>Something to store.</summary>
    public int Value { get; set; }
}

/// <summary>One of <see cref="PictureA" />'s five.</summary>
[Vixen.Core.Component]
[Vixen.Core.DataContract("ScrollingPanelPictureA3")]
public struct PictureA3 {
    /// <summary>Something to store.</summary>
    public int Value { get; set; }
}

/// <summary>One of <see cref="PictureA" />'s five.</summary>
[Vixen.Core.Component]
[Vixen.Core.DataContract("ScrollingPanelPictureA4")]
public struct PictureA4 {
    /// <summary>Something to store.</summary>
    public int Value { get; set; }
}

/// <summary>One of <see cref="PictureA" />'s five.</summary>
[Vixen.Core.Component]
[Vixen.Core.DataContract("ScrollingPanelPictureA5")]
public struct PictureA5 {
    /// <summary>Something to store.</summary>
    public int Value { get; set; }
}
