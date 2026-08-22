using Vixen.Ui;
using Vixen.Ui.Composition;
using Vixen.Ui.Controls;

namespace VixenApp1;

/// <summary>The document, and the three sheets that style it. No platform, no device, no window.</summary>
/// <remarks>
///     <para>
///         This file builds a <see cref="UiDocument" /> and mounts <c>AppShell.vxml</c> into it,
///         which is worth keeping as the application grows: the interface stays testable without any
///         of the machinery that eventually puts it on a screen. <c>AppHost</c> is the half that
///         needs a GPU, and the two share exactly one type.
///     </para>
///     <para>
///         ⚠ <b>Start in <c>AppShell.vxml</c>, not here.</b> The interface is markup and a
///         stylesheet; this file is the seam between them and the frame loop, and most applications
///         never need to change it.
///     </para>
/// </remarks>
sealed class AppDocument : IDisposable {
    /// <summary>Builds the interface into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    public AppDocument(float width, float height) {
        Document = new UiDocument(width, height);

        // The control set's theme, as a user-agent stylesheet. Everything loaded after it out-
        // specifies it simply by being an author sheet, which is the arrangement the whole theme is
        // designed around.
        ControlTheme.Install(Document);

        // ⚠ **The generated sheet, and there is no code behind that name.** `Theme/vixen.ui.vcss` is
        // the tokens; every `.vxml` and every `.cs` in this project is scanned for class names at
        // build time; the rules for the ones actually used are compiled into `VixenUtilityStyles`
        // before the compiler runs. Nothing here walks a manifest or runs a scanner.
        //
        // ⚠ It is the cheapest check that the wiring is there at all: a project whose build step did
        // not run compiles perfectly and produces an empty sheet, and every class in the markup then
        // quietly does nothing. `VixenUtilityStyles.RuleCount` is how many rules came out.
        Document.Load(VixenUtilityStyles.Css);

        // The root is the one element no markup owns, so its two classes are set here. Everything
        // else this application looks like is a class name in AppShell.vxml.
        Document.Root.AddClass("p-0");
        Document.Root.AddClass("bg-slate-900");

        BuildContext.BuildInto(new AppShell(), Document, Document.Root);
    }

    /// <summary>The document the host lays out, draws and dispatches into.</summary>
    public UiDocument Document { get; }

    /// <summary>Advances whatever moves by itself.</summary>
    /// <param name="now">The time since the application started.</param>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>The host drives the clock, and every timed control in the set is built that way.</b>
    ///     Nothing in <c>Vixen.Ui</c> knows what time it is except through an input event, so a
    ///     spinner that spins and a toast that expires both need telling. It is what keeps a golden
    ///     image of a control from depending on what time it was taken.
    ///
    ///     ⚠ <b><c>Document.Tick</c>, and not <c>Document.Gestures.Tick</c>.</b> This line said the
    ///     second for a long time — as did <c>EditorShell</c> and <c>Samples/02</c>, which is how a
    ///     copied frame loop goes wrong three times — and the recogniser is only one of the four
    ///     things that needs the clock. The others are <c>UiDocument.Ticked</c>, which is what an
    ///     <c>Overlay</c>'s delay and a <c>Toasts</c> dismissal hang on; <c>UiDocument.Now</c>, which
    ///     is what a toast is stamped with; and the CSS animator. That last one fails in the
    ///     direction nobody expects: a transition stamped against a clock that never leaves zero
    ///     makes no progress on any frame, so a declared <c>transition</c> holds the property at the
    ///     value it was leaving rather than jumping to the new one.
    /// </remarks>
    public void Tick(TimeSpan now, TimeSpan delta) {
        _ = delta;

        Document.Tick(now);
    }

    /// <summary>Changes the surface's size.</summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">Its height.</param>
    public void Resize(float width, float height) => Document.Resize(width, height);

    /// <inheritdoc />
    public void Dispose() => Document.Dispose();
}
