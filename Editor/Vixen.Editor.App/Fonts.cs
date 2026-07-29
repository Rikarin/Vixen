// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Text;

namespace Vixen.Editor.App;

/// <summary>The face the editor draws its interface in.</summary>
/// <remarks>
///     <para>
///         <b>Open Sans, shipped with the editor rather than borrowed from the machine.</b> This
///         file used to take whatever the operating system happened to have — Arial on macOS, Segoe
///         on Windows, DejaVu on Linux — and the consequence was three editors that measured
///         differently, wrapped differently and photographed differently, plus a set of missing
///         glyphs that changed per platform: macOS's Arial has no ⌘ ⇧ ⌥ ⌃, so the menu bar wrote
///         "L+S" for Save until the shell was taught to ask what the face could draw.
///     </para>
///     <para>
///         ⚠ <b>Embedded rather than copied beside the executable.</b> A font next to the binary is
///         a font a publish step can drop, and the failure is silent: every label measures zero and
///         the interface draws its chrome with nothing in it. As a resource it cannot go missing
///         without the assembly going with it.
///     </para>
///     <para>
///         ⚠ <b>Two weights, registered under a family name, and the regular is also the
///         default.</b> <c>FontRegistry.Resolve</c> chooses the family before the weight, so the
///         semibold only reaches text whose <c>font-family</c> names <see cref="Family" /> — which
///         is what the editor's own sheet declares on the root. Everything that names no family
///         still gets the regular through <c>Default</c>, including a plugin's panel and a golden
///         test with no stylesheet of its own.
///     </para>
///     <para>
///         The operating system's faces are still tried, behind the shipped one. Nothing should need
///         them — they are here for a build where the resource has been trimmed away — and finding
///         nothing at all is still not a failure: the document is left with no face, every label
///         measures zero, and the controls draw their boxes exactly as before.
///     </para>
/// </remarks>
static class Fonts {
    /// <summary>What the editor's stylesheet names in <c>font-family</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>The name the sheet uses, not the file's own.</b> <c>EditorTheme</c> declares
    ///     <c>font-family: OpenSans</c> on the root, and a family registered under anything else
    ///     would leave that declaration resolving to <c>Default</c> — which is the regular, for
    ///     every bold label in the application.
    /// </remarks>
    public const string Family = "OpenSans";

    /// <summary>Registers the editor's faces on a document.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Whether anything was found to draw with.</returns>
    public static bool Install(UiDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        // ⚠ The regular first, because the first face registered becomes `Default` and the default
        // is what everything without a `font-family` draws in. Registering the semibold first would
        // put the whole editor in semibold wherever the sheet does not reach.
        if (Embedded("OpenSans-Regular.ttf") is { } regular) {
            document.Fonts.Register(Family, regular, FontRegistry.RegularWeight);
            document.Fonts.Default = regular;

            if (Embedded("OpenSans-SemiBold.ttf") is { } semibold) {
                // 600 rather than 700, because that is what the file is. `FontRegistry` picks the
                // nearest weight to the one asked for, so `font-weight: bold` still lands here and
                // a sheet that asks for 600 gets it exactly.
                document.Fonts.Register(Family, semibold, 600);
            }

            return true;
        }

        foreach (var path in Candidates()) {
            if (!File.Exists(path) || Read(path) is not { } face) {
                continue;
            }

            document.Fonts.Register(face.Name, face);
            document.Fonts.Default = face;

            return true;
        }

        return false;
    }

    /// <summary>Loads one of the faces compiled into this assembly.</summary>
    static FontFace? Embedded(string file) {
        var assembly = typeof(Fonts).Assembly;

        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".Fonts." + file);

        if (stream is null) {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return Parse(buffer.ToArray(), Path.GetFileNameWithoutExtension(file));
    }

    static FontFace? Read(string path) {
        try {
            return Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Parses a face, answering null for anything this parser will not take.</summary>
    /// <remarks>
    ///     ⚠ <b>A face that will not parse is not an editor that will not start.</b> The list below
    ///     exists precisely because no single path is reliable — a <c>.ttc</c> collection and a
    ///     variable font with an outline format this parser does not read both live at plausible
    ///     names — so the answer to one of them is the next candidate rather than an exception.
    /// </remarks>
    static FontFace? Parse(byte[] bytes, string name) {
        try {
            return FontFace.Load(bytes, name: name);
        } catch (InvalidDataException) {
            return null;
        }
    }

    /// <summary>Where a plain TrueType UI face tends to live, best first.</summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately not <c>.ttc</c> collections.</b> macOS's system faces are collections —
    ///     <c>Helvetica.ttc</c>, <c>SFNS.ttf</c> as a variable font — and this parser reads a single
    ///     face. The supplemental directory is where the plain files are.
    /// </remarks>
    static IEnumerable<string> Candidates() {
        // A copy the user installed themselves comes first, because it is the one that matches what
        // the editor would have shipped.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS()) {
            yield return Path.Combine(home, "Library", "Fonts", "OpenSans-Regular.ttf");
            yield return "/Library/Fonts/OpenSans-Regular.ttf";
            yield return "/System/Library/Fonts/Supplemental/Arial.ttf";
            yield return "/System/Library/Fonts/Supplemental/Verdana.ttf";
            yield return "/Library/Fonts/Arial.ttf";
        }

        if (OperatingSystem.IsWindows()) {
            yield return @"C:\Windows\Fonts\OpenSans-Regular.ttf";
            yield return @"C:\Windows\Fonts\segoeui.ttf";
            yield return @"C:\Windows\Fonts\arial.ttf";
            yield return @"C:\Windows\Fonts\tahoma.ttf";
        }

        if (OperatingSystem.IsLinux()) {
            yield return "/usr/share/fonts/truetype/open-sans/OpenSans-Regular.ttf";
            yield return "/usr/share/fonts/TTF/OpenSans-Regular.ttf";
            yield return "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
            yield return "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf";
            yield return "/usr/share/fonts/TTF/DejaVuSans.ttf";
        }
    }
}
