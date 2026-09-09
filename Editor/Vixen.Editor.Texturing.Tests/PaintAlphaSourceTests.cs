// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Texturing.Painting;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Texturing.Tests;

/// <summary>An artist's own picture becomes a brush alpha, and says which channel it read.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/1090">#1090</a>, through the decoder
///         production uses rather than around it.</b> Every case here writes a real file into a real
///         <c>EditorProject</c> and lets <c>ImageDecoders.BuiltIn</c> read it, because the claim
///         being made is about what a decoded picture's channels contain — and a fixture that
///         handed <c>PaintAlphaSource</c> a <c>TextureData</c> it had built itself would be asserting
///         the channel rule against inputs chosen to satisfy it, with nothing said about the shape
///         the decoder actually produces.
///     </para>
///     <para>
///         ⚠ <b>Uncompressed 32-bit TGA, because it is the one format in
///         <c>StbImageDecoder.Extensions</c> a test can write in eighteen bytes of header.</b> The
///         file goes through the same <c>ImageDecoders.For</c> lookup and the same <c>Decode</c> as
///         a PNG an artist imports; what a hand-written encoder would risk — a channel order this
///         suite and the decoder disagree about — is exactly what the assertions below would catch,
///         because a swapped red and alpha changes which of the two rules fires.
///     </para>
/// </remarks>
public class PaintAlphaSourceTests {
    /// <summary>A picture whose alpha channel varies carries the weight there.</summary>
    [Fact]
    public void An_alpha_channel_that_varies_is_what_the_weight_is_read_from() {
        using var fixture = new TexturingFixture();

        // Opaque mid-grey throughout, with the shape in alpha alone: a left half that paints and a
        // right half that does not. Read as luminance this would be flat.
        Add(fixture, "wedge", (x, _) => (128, 128, 128, x < 8 ? (byte)255 : (byte)0));

        var (mask, message) = PaintAlphaSource.Resolve(fixture.Project, "Assets/wedge.tga");

        Assert.NotNull(mask);
        Assert.Contains("alpha channel", message, StringComparison.Ordinal);

        Assert.True(mask.Sample(new(0.25f, 0.5f)) > 0.9f, "the left half of the wedge does not paint.");
        Assert.True(mask.Sample(new(0.75f, 0.5f)) < 0.1f, "the right half of the wedge paints.");
    }

    /// <summary>A picture with a constant alpha channel carries the weight in its brightness.</summary>
    /// <remarks>
    ///     ⚠ <b>The defect the issue names, and it is silent.</b> Most brush alphas in circulation
    ///     are opaque greyscale — an alpha channel of 255 everywhere, because the format has one and
    ///     the author never used it. Read as an alpha mask that is a weight of one at every texel,
    ///     which is a plain square stamp: the brush works, the rotation control moves it, and the
    ///     shape the artist picked is nowhere. The other constant — 0 — is a brush that paints
    ///     nothing at all.
    /// </remarks>
    [Fact]
    public void A_constant_alpha_channel_means_the_weight_is_the_brightness() {
        using var fixture = new TexturingFixture();

        Add(fixture, "grey", (x, _) => (Ramp(x), Ramp(x), Ramp(x), 255));

        var (mask, message) = PaintAlphaSource.Resolve(fixture.Project, "Assets/grey.tga");

        Assert.NotNull(mask);
        Assert.Contains("brightness", message, StringComparison.Ordinal);

        var dark = mask.Sample(new(0.03f, 0.5f));
        var light = mask.Sample(new(0.97f, 0.5f));

        Assert.True(dark < 0.15f, $"the dark end of the ramp weighs {dark:F2}, so the mask is flat.");
        Assert.True(light > 0.85f, $"the light end weighs {light:F2}, so the mask is flat.");
    }

    /// <summary>And an alpha channel of nought everywhere is the same case, not a refusal.</summary>
    /// <remarks>
    ///     ⚠ <b>Both constants and not only 255.</b> A picture exported with an empty alpha channel
    ///     over a real greyscale shape is the second half of the same mistake, and reading it as an
    ///     alpha mask is a brush that deposits nothing while looking entirely healthy.
    /// </remarks>
    [Fact]
    public void An_alpha_channel_of_nought_everywhere_is_read_as_brightness_too() {
        using var fixture = new TexturingFixture();

        Add(fixture, "hollow", (x, _) => (Ramp(x), Ramp(x), Ramp(x), 0));

        var (mask, message) = PaintAlphaSource.Resolve(fixture.Project, "Assets/hollow.tga");

        Assert.NotNull(mask);
        Assert.Contains("brightness", message, StringComparison.Ordinal);
        Assert.True(mask.Sample(new(0.97f, 0.5f)) > 0.85f, "a picture with no alpha painted nothing.");
    }

    /// <summary>A picture that would paint nothing is refused with a sentence.</summary>
    [Fact]
    public void A_picture_that_is_black_everywhere_is_refused_rather_than_loaded() {
        using var fixture = new TexturingFixture();

        Add(fixture, "void", (_, _) => (0, 0, 0, 255));

        var (mask, message) = PaintAlphaSource.Resolve(fixture.Project, "Assets/void.tga");

        Assert.Null(mask);
        Assert.Contains("paints nothing", message, StringComparison.Ordinal);
    }

    /// <summary>Every other way it fails is a sentence too, and none of them is an exception.</summary>
    /// <remarks>
    ///     ⚠ <b>Including the empty box, which is not a failure at all.</b> Clearing the row is how
    ///     an artist goes back to the shelf, and a refusal sentence for it would be an error message
    ///     for doing nothing.
    /// </remarks>
    [Fact]
    public void A_missing_asset_an_undecodable_one_and_an_empty_box_are_each_a_sentence() {
        using var fixture = new TexturingFixture();

        var (empty, nothing) = PaintAlphaSource.Resolve(fixture.Project, "   ");

        Assert.Null(empty);
        Assert.Equal("", nothing);

        var (absent, missing) = PaintAlphaSource.Resolve(fixture.Project, "Assets/nowhere.png");

        Assert.Null(absent);
        Assert.Contains("not in this project's assets", missing, StringComparison.Ordinal);

        fixture.AddAsset("notes", ".txt", "this is not a picture");

        var (unreadable, why) = PaintAlphaSource.Resolve(fixture.Project, "Assets/notes.txt");

        Assert.Null(unreadable);
        Assert.Contains("decodes", why, StringComparison.Ordinal);
    }

    /// <summary>The brush column resolves a path, and the module is what gave it the project.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The caller, asserted where it can be absent.</b> <c>PaintAlphaSource</c> resolving
    ///         a path in a unit test says nothing about whether anything in the editor ever calls it
    ///         — which is this workstream's most-shipped defect, and the shape #1090 was already in:
    ///         <c>PaintImageMask</c> had existed for a batch with a shelf as its only source.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The seam is the thing being asserted, not the arithmetic.</b> Nothing in the
    ///         column's construction chain carries an <c>EditorProject</c>:
    ///         <c>PaintBrushInspector</c> has none, <c>LayerStackView</c> builds it and has none.
    ///         <c>TexturingModule</c>'s panel factory is the one place that does, so the assertion is
    ///         that opening the panel leaves the inspector able to answer — a resolver reachable
    ///         from the panel an artist actually opens.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_the_panel_gives_the_brush_column_a_way_to_resolve_a_project_picture() {
        using var fixture = new TexturingFixture();

        TexturingModule module = new();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);

        Add(fixture, "scratches", (x, y) => (0, 0, 0, (byte)((x * 16) + (y * 2))));

        fixture.Project.Selection.Set(fixture.AddStack("Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));
        Assert.NotNull(fixture.Shell.Workspace.Open(TexturingModule.StackPanel));

        var brush = module.Stack?.Brush;

        Assert.NotNull(brush);

        Assert.True(
            brush.Alphas is not null,
            "the panel opened with no way to resolve an imported alpha, so the row accepts a path and "
            + "changes no texel."
        );

        var (mask, message) = brush.Alphas!("Assets/scratches.tga");

        Assert.NotNull(mask);
        Assert.Contains("scratches", message, StringComparison.Ordinal);

        // And the tool takes it, which is the half between the resolver and the stroke.
        brush.Tool.SetAlphaAsset("Assets/scratches.tga", mask);

        Assert.Equal("Assets/scratches.tga", brush.Tool.AlphaAsset);
        Assert.True(brush.Tool.IsMasked, "the brush did not take the imported alpha.");
        Assert.Contains("scratches", brush.Tool.Describe(), StringComparison.Ordinal);
    }

    /// <summary>⚠ Typing a path into the row and pressing Enter is what puts the mask on the brush.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Every other case here calls the resolver, and the resolver is not the row.</b>
    ///         Deleting <c>Load</c>'s body, or the <c>Submitted</c> wiring, leaves the panel with a
    ///         box an artist can type into that changes no texel — this workstream's commonest defect
    ///         exactly, in the one place where the whole feature is the control.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Enter and not a keystroke</b>, which is the one way this row differs from the
    ///         plugin's other typed-path rows: resolving decodes a picture, so committing per
    ///         character would decode a file per character. The status line says so before anything
    ///         is typed, because the difference is otherwise invisible until it has caught somebody.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Typing_a_path_and_pressing_enter_is_what_loads_the_alpha() {
        using var fixture = new TexturingFixture();

        TexturingModule module = new();

        fixture.Host.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, module);

        Add(fixture, "scratches", (x, y) => (0, 0, 0, (byte)((x * 16) + (y * 2))));

        fixture.Project.Selection.Set(fixture.AddStack("Hull"));

        Assert.True(fixture.Shell.Commands.Execute(TexturingModule.OpenStackCommand));

        var panel = fixture.Shell.Workspace.Open(TexturingModule.StackPanel);

        Assert.NotNull(panel);

        var brush = module.Stack?.Brush;

        Assert.NotNull(brush);

        // The row says how it commits before anything has been typed into it.
        Assert.Contains("Enter", brush.AlphaStatus, StringComparison.Ordinal);
        Assert.False(brush.Tool.IsMasked);

        var box = Boxes(panel!).FirstOrDefault(one => one.Placeholder?.Contains(".png", StringComparison.Ordinal) == true);

        Assert.NotNull(box);

        box!.Value = "Assets/scratches.tga";

        // ⚠ Typing alone is not the gesture: this row commits on Enter, and a test that set `Value`
        // and asserted would be green against a row wired to nothing.
        Assert.False(brush.Tool.IsMasked, "the row committed on a keystroke, which is not what it says it does.");

        fixture.Shell.Document.Focus(box);
        fixture.Shell.Document.Dispatch(new KeyEvent { Key = InputKey.Enter, Action = KeyAction.Pressed });
        fixture.Shell.Document.Dispatch(new KeyEvent { Key = InputKey.Enter, Action = KeyAction.Released });

        Assert.True(brush.Tool.IsMasked, "Enter in the alpha row did not put a mask on the brush.");
        Assert.Equal("Assets/scratches.tga", brush.Tool.AlphaAsset);
        Assert.Contains("scratches", brush.AlphaStatus, StringComparison.Ordinal);
    }

    /// <summary>Every text box under one element, in tree order.</summary>
    /// <param name="root">Where to start.</param>
    /// <returns>The boxes.</returns>
    static List<TextBox> Boxes(UiElement root) {
        List<TextBox> found = [];

        void Walk(UiElement element) {
            if (element is TextBox box) {
                found.Add(box);
            }

            foreach (var child in element.Children) {
                Walk(child);
            }
        }

        Walk(root);

        return found;
    }

    /// <summary>Picking a shelf shape puts the imported alpha down.</summary>
    /// <remarks>
    ///     ⚠ <b>Whichever was set last wins, and both are readable.</b> An artist who loads an alpha
    ///     and then clicks Chisel means Chisel; one who clears the path box means the shape they had
    ///     before the import, and not a round brush.
    /// </remarks>
    [Fact]
    public void The_shelf_and_an_imported_alpha_take_turns_rather_than_stacking() {
        using var fixture = new TexturingFixture();

        Add(fixture, "grain", (x, _) => (0, 0, 0, Ramp(x)));

        var (mask, _) = PaintAlphaSource.Resolve(fixture.Project, "Assets/grain.tga");

        Assert.NotNull(mask);

        PaintTool tool = new();

        tool.SetAlpha(PaintAlphas.Chisel);
        tool.SetAlphaAsset("Assets/grain.tga", mask);

        Assert.Equal("Assets/grain.tga", tool.AlphaAsset);
        Assert.Same(mask, tool.Brush.Alpha);

        // Cleared: back to the shelf shape and not to a round brush.
        tool.SetAlphaAsset("", null);

        Assert.Equal("", tool.AlphaAsset);
        Assert.Equal(PaintAlphas.Chisel, tool.AlphaName);
        Assert.Same(PaintAlphas.Find(PaintAlphas.Chisel), tool.Brush.Alpha);

        // And a shelf shape chosen while an import is loaded puts the import down.
        tool.SetAlphaAsset("Assets/grain.tga", mask);
        tool.SetAlpha(PaintAlphas.Square);

        Assert.Equal("", tool.AlphaAsset);
        Assert.Same(PaintAlphas.Find(PaintAlphas.Square), tool.Brush.Alpha);
    }

    /// <summary>A left-to-right ramp byte.</summary>
    /// <param name="x">The column, 0…15.</param>
    /// <returns>The value.</returns>
    static byte Ramp(int x) => (byte)(x * 17);

    /// <summary>Writes a 16×16 uncompressed 32-bit TGA into the project and scans it in.</summary>
    /// <param name="fixture">The project.</param>
    /// <param name="name">What to call it, without the extension.</param>
    /// <param name="texel">What each column and row is, as red, green, blue and alpha.</param>
    /// <remarks>
    ///     ⚠ <b>Top-left origin and BGRA order, which is what the format says and not what a reader
    ///     of the bytes below would guess.</b> Bit 5 of the descriptor is what puts row zero at the
    ///     top; without it the picture is written upside down, which every assertion here that reads
    ///     a column would survive and one reading a row would not.
    /// </remarks>
    static void Add(TexturingFixture fixture, string name, Func<int, int, (byte R, byte G, byte B, byte A)> texel) {
        const int Side = 16;

        var bytes = new byte[18 + (Side * Side * 4)];

        bytes[2] = 2;
        bytes[12] = Side;
        bytes[14] = Side;
        bytes[16] = 32;
        bytes[17] = 0x28;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var (r, g, b, a) = texel(x, y);
                var at = 18 + (((y * Side) + x) * 4);

                bytes[at] = b;
                bytes[at + 1] = g;
                bytes[at + 2] = r;
                bytes[at + 3] = a;
            }
        }

        var relative = "Assets/" + name + ".tga";

        File.WriteAllBytes(fixture.Paths.Absolute(relative), bytes);

        var report = fixture.Project.Assets.Scan();

        Assert.DoesNotContain(report.Issues, issue => issue.Kind != AssetIssueKind.MetaCreated);
        Assert.True(fixture.Project.Assets.TryGetByPath(relative, out _), "the scan did not pick " + relative + " up");
    }
}
