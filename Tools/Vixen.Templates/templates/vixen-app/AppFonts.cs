using Vixen.Ui;
using Vixen.Ui.Text;

namespace VixenApp1;

/// <summary>Finds a face to draw with on whatever machine this is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Borrowed from the operating system, which is a starting point and not a shipping
///         answer.</b> An application that ships decides what it looks like — it carries its own
///         face as an asset and registers it here instead of hunting for one, because "whatever
///         Arial the machine happens to have" is not a design.
///     </para>
///     <para>
///         <b>Nothing found is not a failure.</b> A machine with none of these leaves the document
///         with no face, every label measures zero, and the controls draw their boxes and their
///         chrome exactly as before: text is a thing an element has, not a thing the layout
///         requires.
///     </para>
/// </remarks>
static class AppFonts {
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

                // Registered as the default rather than under a name, because the theme never says
                // `font-family` — a control set that named a family would be one whose text
                // disappeared on every machine that did not have it.
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
    ///     ⚠ <b>Deliberately not <c>.ttc</c> collections.</b> macOS's system faces are collections
    ///     and this parser reads a single face; the supplemental directory is where the plain files
    ///     are.
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
