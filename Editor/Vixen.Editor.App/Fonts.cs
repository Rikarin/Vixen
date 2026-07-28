// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Text;

namespace Vixen.Editor.App;

/// <summary>Finds a face to draw with on whatever machine this is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Borrowed from the operating system rather than committed, and the reason is that
///         the repository has no Latin UI font to commit.</b> The fourteen files under
///         <c>Vixen.Ui.Text.Tests/Fonts</c> are the Unicode Consortium's shaping fixtures — Balinese,
///         Kannada, Lanna — and a sample that drew its buttons in Lanna would be demonstrating the
///         shaper rather than the controls. Adding a font to the tree is a licence entry and a few
///         hundred kilobytes for something only a sample needs.
///     </para>
///     <para>
///         <b>Nothing found is not a failure.</b> A machine with none of these leaves the document
///         with no face, every label measures zero, and the controls draw their boxes and their
///         chrome exactly as before — which is worth knowing about the framework as well as
///         convenient here: text is a thing an element has, not a thing the layout requires.
///     </para>
///     <para>
///         An application ships its own font as an asset. That is doc 08's business and it is what a
///         game does; this is what a sample does.
///     </para>
/// </remarks>
static class Fonts {
    /// <summary>Registers a face under the family the theme asks for, if one can be found.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether one was found.</returns>
    public static bool Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var path in Candidates()) {
            if (!File.Exists(path)) {
                continue;
            }

            try {
                var face = FontFace.Load(File.ReadAllBytes(path), name: Path.GetFileNameWithoutExtension(path));

                // Registered as the default rather than under a name, because the theme never says
                // `font-family` — a control set that named a family would be one whose text
                // disappeared on every machine that did not have it.
                document.Fonts.Register(face.Name, face);
                document.Fonts.Default = face;

                return true;
            } catch (InvalidDataException) {
                // A file with the right extension that this parser does not accept — a collection,
                // a variable font with an outline format it does not read. Try the next one rather
                // than stopping: the list exists precisely because no single path is reliable.
            }
        }

        return false;
    }

    /// <summary>Where a plain TrueType UI face tends to live, best first.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <c>.ttc</c> collections.</b> macOS's system faces are collections —
    ///     <c>Helvetica.ttc</c>, <c>SFNS.ttf</c> as a variable font — and this parser reads a single
    ///     face. The supplemental directory is where the plain files are.
    /// </remarks>
    static IEnumerable<string> Candidates() {
        if (OperatingSystem.IsMacOS()) {
            yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
            yield return "/System/Library/Fonts/Supplemental/Verdana.ttf";
            yield return "/Library/Fonts/Arial.ttf";
        }

        if (OperatingSystem.IsWindows()) {
            yield return @"C:\Windows\Fonts\segoeui.ttf";
            yield return @"C:\Windows\Fonts\arial.ttf";
            yield return @"C:\Windows\Fonts\tahoma.ttf";
        }

        if (OperatingSystem.IsLinux()) {
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
            yield return "/usr/share/fonts/TTF/DejaVuSans.ttf";
        }
    }
}
