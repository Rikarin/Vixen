// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Desktop.Tests;

/// <summary>
///     <c>font-family: monospace</c> resolves to a fixed-pitch face on a desktop, and never to the
///     UI face.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>These walk the machine, which is what every other test in this assembly turns
///         off.</b> They have to: the property under test is that a face is borrowed. So each one
///         asserts something on both kinds of machine — a face found registers the family and is
///         fixed-pitch; none found registers nothing and says so — rather than skipping, because a
///         skip is what this suite would print on the day the candidate list was wrong for the CI
///         image and nobody would read it.
///     </para>
///     <para>
///         Fixed-pitch is measured, not read off <c>post.isFixedPitch</c>: the narrowest and the
///         widest Latin letters shape to the same advance as a digit, which is the property
///         <c>CodeEditor</c>'s <c>column × CharacterWidth</c> actually depends on.
///     </para>
/// </remarks>
public class SystemFontsMonospaceTests {
    static float Advance(FontFace face, string text) => TextShaper.Shape(face, text).Advance;

    /// <summary>The list is never empty on a platform this ships on.</summary>
    [Fact]
    public void Every_desktop_platform_has_candidates() =>
        Assert.NotEmpty(SystemFonts.MonospaceCandidates());

    /// <summary>The name registered is CSS's keyword, the one the stylesheets write.</summary>
    [Fact]
    public void The_family_name_is_the_css_keyword() =>
        Assert.Equal("monospace", SystemFonts.MonospaceFamily);

    /// <summary>A face found is registered under the family, is fixed-pitch, and is not the default.</summary>
    [Fact]
    public void A_found_face_is_fixed_pitch_and_named_monospace() {
        var document = new UiDocument(800f, 600f);
        var found = SystemFonts.InstallMonospace(document);
        var expected = SystemFonts.MonospaceCandidates().Any(File.Exists);

        // Both halves of the instrument: the return value says what the file system says.
        Assert.Equal(expected, found);

        if (!found) {
            Assert.Equal(0, document.Fonts.Count);
            Assert.Null(document.Fonts.Default);

            return;
        }

        var face = document.Fonts.Resolve("monospace");
        Assert.NotNull(face);

        var digit = Advance(face, "0");
        Assert.True(digit > 0f);
        Assert.Equal(digit, Advance(face, "i"), 0.01f);
        Assert.Equal(digit, Advance(face, "W"), 0.01f);
        Assert.Equal(digit * 8f, Advance(face, "if (x) {"), 0.05f);
    }

    /// <summary>On a document with no face at all, the fixed-pitch one does not become the default.</summary>
    /// <remarks>
    ///     The case <see cref="The_default_is_left_alone" /> cannot reach on a machine that has a
    ///     UI face: <c>Register</c> only claims the default when there is none, so this is the one
    ///     document where the restore does any work — and where dropping it puts every unnamed
    ///     label into a code face.
    /// </remarks>
    [Fact]
    public void On_a_bare_document_it_is_not_the_default() {
        var document = new UiDocument(800f, 600f);
        var found = SystemFonts.InstallMonospace(document);

        Assert.Null(document.Fonts.Default);
        Assert.Equal(found ? 1 : 0, document.Fonts.Count);
    }

    /// <summary>Installed after a UI face, the UI face stays the default.</summary>
    /// <remarks>
    ///     ⚠ <c>FontRegistry.Register</c> makes the first face registered the default, and a document
    ///     that has a UI face already must keep it: the fixed-pitch face is for the declarations that
    ///     name it, not for every label with none.
    /// </remarks>
    [Fact]
    public void The_default_is_left_alone() {
        var document = new UiDocument(800f, 600f);

        if (!SystemFonts.Install(document)) {
            // No UI face on this machine: then a fixed-pitch one must not become the default either.
            SystemFonts.InstallMonospace(document);
            Assert.Null(document.Fonts.Default);

            return;
        }

        var ui = document.Fonts.Default;
        Assert.NotNull(ui);

        var found = SystemFonts.InstallMonospace(document);
        Assert.Same(ui, document.Fonts.Default);

        if (found) {
            Assert.NotSame(ui, document.Fonts.Resolve("monospace"));
        }
    }
}
