using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace VixenApp1;

/// <summary>The interface. No platform, no device, no window.</summary>
/// <remarks>
///     <para>
///         This file constructs a <see cref="UiDocument" /> and nothing else, which is worth keeping
///         as the application grows: the interface stays testable without any of the machinery that
///         eventually puts it on a screen. <c>AppHost</c> is the half that needs a GPU, and the two
///         share exactly one type.
///     </para>
///     <para>
///         Start here. Replace the card below with the application you are actually writing.
///     </para>
/// </remarks>
sealed class AppShell : IDisposable {
    readonly TextBlock count;

    int clicks;

    /// <summary>Builds the interface into a new document.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    public AppShell(float width, float height) {
        Document = new UiDocument(width, height);

        // The control set's theme, as a user-agent stylesheet. Everything below out-specifies it
        // simply by being an author sheet, which is the arrangement the whole theme is designed
        // around — a plain `card { … }` here beats the theme's rule because of where it came from.
        ControlTheme.Install(Document);
        Document.Load(Style);

        var shell = Document.Root.Add<Panel>();
        shell.AddClass("shell");

        var card = shell.Add<Card>();
        card.Header.Add<TextBlock>().Text = "VixenApp1";

        card.Body.Add<TextBlock>().Text = "A Vixen application: Vixen.Ui, a window, and no engine.";

        count = card.Body.Add<TextBlock>();

        var button = card.Body.Add<Button>();
        button.Label = "Click me";
        button.Variant = ControlVariant.Primary;
        button.Clicked += _ => {
            clicks++;
            Refresh();
        };

        Refresh();
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

    void Refresh() =>
        count.Text = string.Create(CultureInfo.CurrentCulture, $"Clicked {clicks} times.");

    /// <summary>The application's own stylesheet, over the one the controls bring.</summary>
    const string Style = """
        root { padding: 0px; background-color: var(--surface-sunken); }

        .shell {
            flex-direction: column;
            flex-grow: 1;
            align-items: center;
            justify-content: center;
            padding: 24px;
        }

        card { width: 420px; }
        card-body { gap: 8px; }
        """;
}
