// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Text;

namespace Vixen.Ui.Desktop;

/// <summary>Finds a face to draw with on whatever machine this is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Borrowed from the operating system, which is a starting point and not a shipping
///         answer.</b> An application that ships decides what it looks like: it carries its own face
///         as an asset and registers it against <c>UiDocument.Fonts</c> itself, because "whatever
///         Arial the machine happens to have" is not a design. This is what a sample does, what a
///         freshly scaffolded application does on its first run, and what an application does before
///         its own asset has loaded.
///     </para>
///     <para>
///         <b>Nothing found is not a failure.</b> A machine with none of these leaves the document
///         with no face, every label measures zero, and the controls draw their boxes and their
///         chrome exactly as before — which is worth knowing about the framework as well as
///         convenient here: text is a thing an element has, not a thing the layout requires.
///     </para>
///     <para>
///         This existed three times before it existed once: <c>Samples/02-HelloUi</c>'s
///         <c>Fonts</c>, the <c>vixen-app</c> template's <c>AppFonts</c>, and — with a committed face
///         in front of it — <c>Vixen.Editor.App</c>'s <c>Fonts</c>. The three candidate lists had
///         already begun to differ.
///     </para>
/// </remarks>
public static class SystemFonts {
    /// <summary>Registers a face as the document's default, if one can be found.</summary>
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

                // Registered as the default rather than under a name, because the control theme
                // never says `font-family` — a control set that named a family would be one whose
                // text disappeared on every machine that did not have it.
                document.Fonts.Register(face.Name, face);
                document.Fonts.Default = face;

                return true;
            } catch (InvalidDataException) {
                // A file with the right extension that this parser does not accept — a collection, a
                // variable font with an outline format it does not read. Try the next one rather
                // than stopping: the list exists precisely because no single path is reliable.
            }
        }

        return false;
    }

    /// <summary>Where a plain TrueType UI face tends to live, best first.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <c>.ttc</c> collections.</b> macOS's system faces are collections —
    ///     <c>Helvetica.ttc</c>, and <c>SFNS.ttf</c> as a variable font — and this parser reads a
    ///     single face. The supplemental directory is where the plain files are.
    /// </remarks>
    public static IEnumerable<string> Candidates() {
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

    /// <summary>The generic family name a stylesheet uses for a fixed-pitch face.</summary>
    /// <remarks>
    ///     CSS's own keyword, registered as an ordinary family name because <c>FontRegistry</c> has
    ///     no notion of a generic family: a declaration that names nothing registered resolves to
    ///     <c>Default</c>, and for a document whose default is a UI face that is a code editor
    ///     drawn in a proportional font.
    /// </remarks>
    public const string MonospaceFamily = "monospace";

    /// <summary>Registers the machine's fixed-pitch face under <see cref="MonospaceFamily" />, if one can be found.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether one was found.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The family it registers is not decoration.</b> <c>CodeEditor</c> turns a column
    ///         into an x by multiplying a one-digit probe, so a document whose
    ///         <c>font-family: monospace</c> resolves to the UI face puts the caret, the selection
    ///         and every click in the wrong place — an <c>i</c> is a third of a cell wide in a
    ///         proportional face. Until #1259 nothing in the tree registered a family by this name,
    ///         so every declaration of it fell through to <c>Default</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Never the default.</b> <c>FontRegistry.Register</c> makes the first face
    ///         registered the document's default, so on a document with no face yet this would make
    ///         every label monospaced; the default is put back to what it was. Call this after
    ///         <see cref="Install" /> or after the application's own face, not instead of either.
    ///     </para>
    /// </remarks>
    public static bool InstallMonospace(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var path in MonospaceCandidates()) {
            if (!File.Exists(path)) {
                continue;
            }

            try {
                var face = FontFace.Load(File.ReadAllBytes(path), name: Path.GetFileNameWithoutExtension(path));
                var standby = document.Fonts.Default;

                document.Fonts.Register(MonospaceFamily, face);
                document.Fonts.Default = standby;

                return true;
            } catch (InvalidDataException) {
                // As in `Install`: the next candidate, not an exception.
            }
        }

        return false;
    }

    /// <summary>Where a plain TrueType fixed-pitch face tends to live, best first.</summary>
    /// <remarks>
    ///     The same rule as <see cref="Candidates" />: single-face files only. macOS's Menlo and
    ///     Monaco are collections, so Courier New from the supplemental directory is what is left.
    /// </remarks>
    public static IEnumerable<string> MonospaceCandidates() {
        if (OperatingSystem.IsMacOS()) {
            yield return "/System/Library/Fonts/Supplemental/Courier New.ttf";
            yield return "/System/Library/Fonts/Supplemental/Andale Mono.ttf";
            yield return "/Library/Fonts/Courier New.ttf";
        }

        if (OperatingSystem.IsWindows()) {
            yield return @"C:\Windows\Fonts\consola.ttf";
            yield return @"C:\Windows\Fonts\CascadiaMono.ttf";
            yield return @"C:\Windows\Fonts\lucon.ttf";
            yield return @"C:\Windows\Fonts\cour.ttf";
        }

        if (OperatingSystem.IsLinux()) {
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf";
            yield return "/usr/share/fonts/truetype/liberation/LiberationMono-Regular.ttf";
            yield return "/usr/share/fonts/liberation-fonts/LiberationMono-Regular.ttf";
            yield return "/usr/share/fonts/TTF/DejaVuSansMono.ttf";
        }
    }
}
