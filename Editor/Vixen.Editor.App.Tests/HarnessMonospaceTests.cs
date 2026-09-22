// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     An editor under test resolves <c>font-family: monospace</c> to a fixed-pitch face, which is
///     what every suite measuring the code editor, the errors panel, the revisions patch or an
///     asset editor's code view has been assuming without it being true.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Verify the instrument first, and here the instrument was the whole defect.</b>
///         #1259 gave the product <c>SystemFonts.InstallMonospace</c> and left this harness without
///         one, on the stated ground that <c>EditorSession</c> could not reach <c>SystemFonts</c> —
///         which is false: <c>Vixen.Editor.Testing</c> references <c>Vixen.Editor.Host</c>, which
///         references <c>Vixen.Ui.Desktop</c>, and transitive project references flow here (this
///         assembly's own <c>using Vixen.Editor.Testing</c> reaches types it never references
///         directly). What was true is that the harness must not borrow the <i>machine's</i> face,
///         because three platforms would then measure three widths and the golden images would
///         follow. <c>HarnessFonts</c> registers the synthetic <c>TestMono.ttf</c> instead.
///     </para>
///     <para>
///         ⚠ <b>Fixed pitch is measured, not read off a flag.</b> <c>post.isFixedPitch</c> is a
///         claim a face makes about itself and several proportional faces make it wrongly; the
///         only thing that settles it is shaping the narrowest and the widest Latin letters and
///         comparing the two advances. The control half shapes the same two strings in the
///         document's default face, so a machine on which the editor's own face were somehow fixed
///         pitch could not make this test pass by accident.
///     </para>
/// </remarks>
public class HarnessMonospaceTests {
    const string Narrow = "iiiiiiii";

    const string Wide = "WWWWWWWW";

    /// <summary>The family the four stylesheets name is registered, and not to the default.</summary>
    [Fact]
    public void The_family_the_stylesheets_name_resolves_to_a_face_of_its_own() {
        using var editor = EditorSession.Start();
        var fonts = editor.Document.Fonts;

        var mono = fonts.Resolve("monospace");

        Assert.NotNull(mono);
        Assert.NotNull(fonts.Default);
        Assert.NotSame(fonts.Default, mono);
    }

    /// <summary>And it is fixed pitch, which the default face is not.</summary>
    [Fact]
    public void The_monospace_family_is_fixed_pitch_and_the_default_is_not() {
        using var editor = EditorSession.Start();
        var fonts = editor.Document.Fonts;

        var mono = fonts.Resolve("monospace");
        Assert.NotNull(mono);

        var narrow = TextShaper.Shape(mono, Narrow).Advance;
        var wide = TextShaper.Shape(mono, Wide).Advance;

        Assert.True(narrow > 0f, "the fixed-pitch face shaped to nothing");
        Assert.Equal(wide, narrow, 0.01f);

        var face = fonts.Default;
        Assert.NotNull(face);

        // The control: Open Sans is proportional, so the same two strings differ there — which is
        // what a code editor measured before this registration was drawing with.
        Assert.NotEqual(TextShaper.Shape(face, Wide).Advance, TextShaper.Shape(face, Narrow).Advance, 0.01f);
    }

    /// <summary>The registration never takes the document's default with it.</summary>
    /// <remarks>
    ///     <c>FontRegistry.Register</c> claims <c>Default</c> for the first face a bare document
    ///     sees and <c>HarnessFonts</c> puts it back, so the ordering in <c>EditorSession</c> is
    ///     load-bearing: registered before the editor's own face, boxes would be what every label
    ///     in the editor drew in.
    /// </remarks>
    [Fact]
    public void The_editors_own_face_is_still_the_default() {
        using var editor = EditorSession.Start();
        var fonts = editor.Document.Fonts;

        var face = fonts.Default;
        Assert.NotNull(face);
        Assert.NotSame(fonts.Resolve("monospace"), face);
        Assert.Same(face, fonts.Resolve("OpenSans"));
    }
}
