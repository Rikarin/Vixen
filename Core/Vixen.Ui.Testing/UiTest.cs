// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Core.Mathematics;
using Vixen.Input;
using Vixen.Ui.Rendering;
using Vixen.Ui.Styling;
using Vixen.Ui.Testing.Visual;
using Vixen.Ui.Text.Rasterizing;

namespace Vixen.Ui.Testing;

/// <summary>An interface under test: a document, a clock, a pointer, and the frames between them.</summary>
/// <remarks>
///     <para>
///         The whole of the harness. It owns a real <see cref="UiDocument" /> — not a stub, not a
///         recording of one — so a test drives the same cascade, the same flexbox, the same event
///         routing and the same draw list the game does. What it adds is the three things a test has
///         that an application does not: a clock it controls, an input source it can speak into, and
///         a loop it can step.
///     </para>
///     <para>
///         ⚠ <b>Commands run eagerly, unlike Cypress's.</b> Cypress enqueues and returns a chainable
///         promise because it is JavaScript and the browser is somebody else's loop; translating that
///         faithfully into C# would buy a debugger that never stops on the line that failed. Here
///         <c>Get(".save").Click()</c> has clicked by the time it returns, and each command does its
///         own waiting inside itself. The chaining reads the same and the stack traces are real.
///     </para>
///     <example>
///         <code>
///         using var ui = UiTest.Create(800, 600);
///         ui.Load(".btn { width: 120px; height: 40px; }");
///
///         var button = ui.Create("button", ui.Document.Root, "save", "btn");
///         button.Text = "Save";
///
///         ui.Get("#save").ShouldBeVisible().ShouldHaveText("Save").Click();
///         ui.Get(".toast").ShouldExist();          // runs frames until it appears
///         ui.Screenshot("save-pressed");
///         </code>
///     </example>
/// </remarks>
public sealed class UiTest : IDisposable {
    readonly GlyphFieldCache glyphs = new(new GlyphAtlas(1024, 1024));
    readonly UiGeometryBuilder geometry = new();
    readonly Dictionary<int, Vector2> positions = [];

    UiTest(UiDocument document, UiTestOptions options) {
        Document = document;
        Options = options;
    }

    /// <summary>The interface being tested.</summary>
    /// <remarks>
    ///     Exposed rather than wrapped. A harness that hid the document behind its own vocabulary
    ///     would have to grow a passthrough for every method Vixen.Ui ever adds, and a test that
    ///     needs one that is not there would be stuck. Building the tree is the document's business;
    ///     driving and interrogating it is this type's.
    /// </remarks>
    public UiDocument Document { get; }

    /// <summary>How long commands wait and where pictures live.</summary>
    public UiTestOptions Options { get; }

    /// <summary>Everything this test has done.</summary>
    public CommandLog Log { get; } = new();

    /// <summary>What time it is, on the clock the document's gestures read.</summary>
    /// <remarks>Starts at zero and moves only when <see cref="Frame" /> says so.</remarks>
    public TimeSpan Now { get; private set; }

    /// <summary>How many frames have run.</summary>
    public int FrameCount { get; private set; }

    /// <summary>Where the first pointer is.</summary>
    /// <remarks>
    ///     The one every single-pointer command uses. <see cref="PointerAt" /> is how a multi-touch
    ///     test asks about the others.
    /// </remarks>
    public Vector2 Pointer => PointerAt(PointerId);

    /// <summary>Which modifiers a test has said are held.</summary>
    /// <remarks>
    ///     Held on the harness rather than passed to every call, because a test that presses Shift
    ///     means it for the next few commands rather than for one. <see cref="Hold" /> sets it.
    /// </remarks>
    public ModifierKeys Modifiers { get; private set; }

    /// <summary>Everything in the document, as a subject to chain from.</summary>
    public UiSubject Root => UiSubject.Of(this, "root", [Document.Root]);

    /// <summary>Runs once per frame, before the passes.</summary>
    /// <remarks>
    ///     <para>
    ///         Where the game goes. Almost nothing an interface does happens on the frame that caused
    ///         it: a click starts a request, a state machine advances, an animation finishes, and
    ///         three frames later a toast appears. A harness with no per-frame hook can only test
    ///         interfaces that change synchronously inside an event handler, which is the easy half
    ///         and not the half that breaks.
    ///     </para>
    ///     <para>
    ///         ⚠ After the gesture tick and before the passes, which is the order a game loop has:
    ///         input, then update, then layout. A handler that ran after the layout would have its
    ///         changes laid out one frame late, and every assertion about them would need a spare
    ///         frame that nobody could explain.
    ///     </para>
    ///     <example>
    ///         <code>
    ///         ui.Ticked += () => game.Update(ui.Options.FrameDelta);
    ///         ui.Get(".victory-banner").ShouldBeVisible();   // runs frames until the game shows it
    ///         </code>
    ///     </example>
    /// </remarks>
    public event Action? Ticked;

    /// <summary>Opens an interface of the given size.</summary>
    /// <param name="width">The viewport's width in pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="options">How long commands wait and where pictures live.</param>
    /// <remarks>
    ///     Runs one frame before returning, so that an element created before the first explicit
    ///     frame still has a layout. A harness that handed back a document with every rectangle at
    ///     zero would make the first assertion of every test a surprise.
    /// </remarks>
    public static UiTest Create(float width = 1280f, float height = 720f, UiTestOptions? options = null) {
        var test = new UiTest(new UiDocument(width, height), options ?? new UiTestOptions());
        test.Frame();
        return test;
    }

    /// <summary>Adds a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <returns>Its index, for <see cref="UiDocument.ReloadStyles" />.</returns>
    public int Load(string css) => Document.Load(css);

    /// <summary>Creates an element.</summary>
    /// <param name="tag">Its tag name.</param>
    /// <param name="parent">What to put it under. The root, when omitted.</param>
    /// <param name="id">Its id, if it has one.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    /// <remarks>A shorthand for <see cref="UiDocument.Create(string, UiElement?, string?, ReadOnlySpan{string})" />, so that a test building a fixture does not read as a paragraph about the document.</remarks>
    public UiElement Create(string tag, UiElement? parent = null, string? id = null, params string[] classNames) =>
        Document.Create(tag, parent ?? Document.Root, id, classNames);

    /// <summary>Runs one frame: the clock moves, gestures tick, the passes run, the interface draws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>In that order, and it is not arbitrary.</b> A long press fires because nothing
    ///         happened, so the tick has to come before the passes — a recogniser that fired after
    ///         the layout would put its consequences one frame late, and a test asserting on the
    ///         frame it pressed would see the previous arrangement.
    ///     </para>
    ///     <para>
    ///         The draw runs every frame rather than only when a screenshot asks, so that the frame
    ///         diff and the batcher are exercised by every test rather than by the few that take a
    ///         picture. A still interface costs a version comparison, which is what that machinery
    ///         exists to make cheap.
    ///     </para>
    /// </remarks>
    public void Frame() {
        Now += Options.FrameDelta;
        FrameCount++;
        Document.Gestures.Tick(Now);
        Ticked?.Invoke();
        Document.Update();
        Document.Draw();
    }

    /// <summary>Runs several frames.</summary>
    /// <param name="count">How many.</param>
    public void Frames(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var i = 0; i < count; i++) {
            Frame();
        }
    }

    /// <summary>Runs frames until at least this much time has passed.</summary>
    /// <param name="duration">How long to advance.</param>
    /// <remarks>
    ///     For the thresholds that are written in milliseconds — a long press, a double-tap window —
    ///     so a test says what it means rather than dividing by the frame delta itself.
    /// </remarks>
    public void Advance(TimeSpan duration) {
        var until = Now + duration;

        while (Now < until) {
            Frame();
        }
    }

    /// <summary>Holds modifiers down for the commands that follow.</summary>
    /// <param name="modifiers">What to hold. <see cref="ModifierKeys.None" /> releases everything.</param>
    /// <returns>This, so it reads as part of a chain.</returns>
    public UiTest Hold(ModifierKeys modifiers) {
        Modifiers = modifiers;
        return this;
    }

    /// <summary>Everything matching a CSS selector.</summary>
    /// <param name="selector">A selector, meaning exactly what it means in a stylesheet.</param>
    /// <returns>A subject that re-runs the selector every time it is asked.</returns>
    /// <remarks>
    ///     ⚠ <b>The subject is a recipe, not a snapshot.</b> That is what makes retrying work at all:
    ///     <c>Get(".toast").ShouldExist()</c> runs the selector again on each frame, so an element
    ///     created three frames later is found. A subject holding the elements it matched when it was
    ///     built could only ever assert about the past.
    /// </remarks>
    public UiSubject Get(string selector) {
        ArgumentNullException.ThrowIfNull(selector);

        var query = SelectorQuery.Compile(Document, selector);
        return UiSubject.Deferred(this, $"get \"{selector}\"", () => query.Match(Document, Document.Root));
    }

    /// <summary>Everything whose text contains this.</summary>
    /// <param name="text">What to look for.</param>
    /// <returns>A subject over the matches, in document order.</returns>
    /// <remarks>
    ///     The other half of how a test names things. A selector addresses the structure somebody
    ///     wrote; this addresses what a player can actually read, which is the more durable of the
    ///     two — a class rename breaks every selector in a suite and changes nothing a user sees.
    /// </remarks>
    public UiSubject Contains(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return UiSubject.Deferred(
            this,
            $"contains \"{text}\"",
            () => Descendants(Document.Root)
                .Where(element => element.Text is { } value && value.Contains(text, StringComparison.Ordinal))
                .ToList()
        );
    }

    /// <summary>Whatever is under a point, if anything.</summary>
    /// <param name="x">Where, in document pixels.</param>
    /// <param name="y">Ditto.</param>
    /// <returns>A subject over it, empty when nothing is there.</returns>
    public UiSubject At(float x, float y) =>
        UiSubject.Deferred(
            this,
            $"at ({x:0.#}, {y:0.#})",
            () => Document.HitTest(x, y) is { } hit ? [hit] : []
        );

    /// <summary>Moves a pointer, without pressing anything.</summary>
    /// <param name="x">Where to, in document pixels.</param>
    /// <param name="y">Ditto.</param>
    /// <param name="pointer">Which pointer. The first one, unless a test is using two.</param>
    /// <returns>What it went to, or <c>null</c> if nothing was there.</returns>
    public UiElement? MovePointer(float x, float y, int pointer = PointerId) {
        positions[pointer] = new Vector2(x, y);
        return Send(PointerAction.Moved, PointerButton.None, pointer);
    }

    /// <summary>Presses a pointer button where that pointer is.</summary>
    /// <param name="button">Which button.</param>
    /// <param name="pointer">Which pointer.</param>
    /// <returns>What it went to.</returns>
    public UiElement? PressPointer(PointerButton button = PointerButton.Primary, int pointer = PointerId) =>
        Send(PointerAction.Pressed, button, pointer);

    /// <summary>Releases a pointer button where that pointer is.</summary>
    /// <param name="button">Which button.</param>
    /// <param name="pointer">Which pointer.</param>
    /// <returns>What it went to.</returns>
    public UiElement? ReleasePointer(PointerButton button = PointerButton.Primary, int pointer = PointerId) =>
        Send(PointerAction.Released, button, pointer);

    /// <summary>Where a pointer is.</summary>
    /// <param name="pointer">Which one.</param>
    /// <returns>Its position, or the origin if it has never been moved.</returns>
    public Vector2 PointerAt(int pointer) =>
        positions.TryGetValue(pointer, out var position) ? position : Vector2.Zero;

    /// <summary>Drags from one point to another, in steps.</summary>
    /// <param name="fromX">Where to press, in document pixels.</param>
    /// <param name="fromY">Ditto.</param>
    /// <param name="toX">Where to release.</param>
    /// <param name="toY">Ditto.</param>
    /// <param name="steps">How many moves to break it into.</param>
    /// <returns>What the press landed on, or <c>null</c> if nothing was there.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one action that takes coordinates rather than an element</b>, and it exists
    ///         because some things a player drags are not elements. A slider's thumb, a range
    ///         slider's <i>low</i> thumb, a splitter's grip and a scrollbar's block are all drawn by
    ///         a control rather than laid out by the cascade — so
    ///         <see cref="UiSubject.DragTo(float, float)" />, which starts at an element's centre,
    ///         can only ever grab whichever one happens to be nearest the middle.
    ///     </para>
    ///     <para>
    ///         Broken into steps for the reason <see cref="UiSubject.DragTo(float, float)" /> is: a
    ///         press, one jump and a release is a tap somewhere else as far as the gesture recogniser
    ///         is concerned, and a control that tracks the pointer sees one move rather than a drag.
    ///     </para>
    /// </remarks>
    public UiElement? Drag(float fromX, float fromY, float toX, float toY, int steps = 8) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steps);

        var index = Log.Begin($"drag ({fromX:0.#}, {fromY:0.#}) → ({toX:0.#}, {toY:0.#})");

        MovePointer(fromX, fromY);
        var target = PressPointer();
        Frame();

        for (var step = 1; step <= steps; step++) {
            var t = (float)step / steps;
            MovePointer(fromX + ((toX - fromX) * t), fromY + ((toY - fromY) * t));
            Frame();
        }

        ReleasePointer();
        Frame();

        Log.Complete(index, target is null ? "nothing" : Describe(target), 0);
        return target;
    }

    /// <summary>Turns a wheel where the pointer is.</summary>
    /// <param name="deltaX">How far sideways.</param>
    /// <param name="deltaY">How far down.</param>
    /// <returns>What it went to.</returns>
    public UiElement? Wheel(float deltaX, float deltaY) =>
        Document.Dispatch(new WheelEvent {
            PointerId = PointerId,
            X = Pointer.X,
            Y = Pointer.Y,
            DeltaX = deltaX,
            DeltaY = deltaY,
            Timestamp = Now
        });

    /// <summary>Presses and releases a key, with whatever <see cref="Hold" /> is holding.</summary>
    /// <param name="key">Which physical key, by its US-QWERTY legend.</param>
    /// <returns>What it went to.</returns>
    public UiElement? PressKey(InputKey key) {
        var target = KeyDown(key);
        KeyUp(key);
        return target;
    }

    /// <summary>Puts a key down.</summary>
    /// <param name="key">Which one.</param>
    /// <param name="isRepeat">Whether this is the platform's auto-repeat.</param>
    /// <returns>What it went to.</returns>
    public UiElement? KeyDown(InputKey key, bool isRepeat = false) =>
        Document.Dispatch(new KeyEvent {
            Key = key,
            Action = KeyAction.Pressed,
            Modifiers = Modifiers,
            IsRepeat = isRepeat,
            Timestamp = Now
        });

    /// <summary>Lets a key up.</summary>
    /// <param name="key">Which one.</param>
    /// <returns>What it went to.</returns>
    public UiElement? KeyUp(InputKey key) =>
        Document.Dispatch(new KeyEvent {
            Key = key,
            Action = KeyAction.Released,
            Modifiers = Modifiers,
            Timestamp = Now
        });

    /// <summary>Sends typed text to whatever has the focus.</summary>
    /// <param name="text">What was typed.</param>
    /// <returns>What it went to.</returns>
    /// <remarks>
    ///     ⚠ <b>Text, not keys.</b> A key is a position and this is a character — on an AZERTY
    ///     keyboard the key that types <c>a</c> is <see cref="InputKey.Q" />, so a test that spelled
    ///     a word out of key codes would be testing a layout rather than a text box.
    ///     <see cref="UiSubject.Type" /> is the one that does both, because a real typist produces
    ///     both.
    /// </remarks>
    public UiElement? TypeText(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return Document.Dispatch(new TextInputEvent { Text = text, Timestamp = Now });
    }

    /// <summary>Moves the focus the way Tab does.</summary>
    /// <param name="backwards">Whether to go the other way, as Shift-Tab does.</param>
    /// <returns>Whether the focus moved.</returns>
    public bool Tab(bool backwards = false) =>
        Document.MoveFocus(backwards ? FocusDirection.Previous : FocusDirection.Next);

    /// <summary>Draws the interface as it stands, on the CPU.</summary>
    /// <returns>The picture, 8-bit RGBA.</returns>
    /// <remarks>
    ///     No device, no driver and no window. See <see cref="SoftwareUiRasterizer" /> for why that
    ///     is the right renderer for a regression suite even though a GPU is what ships.
    /// </remarks>
    public Bitmap Capture() {
        var width = (int)MathF.Round(Document.Viewport.ViewportWidth);
        var height = (int)MathF.Round(Document.Viewport.ViewportHeight);

        var frame = geometry.Build(Document.Drawing, glyphs, new Rectangle(0, 0, width, height));
        return SoftwareUiRasterizer.Render(frame, glyphs.Atlas, width, height, Options.Background);
    }

    /// <summary>Checks the interface against a committed picture, or records it as the new one.</summary>
    /// <param name="name">What the picture is called, which is also its file's name.</param>
    /// <param name="tolerance">How far it may drift. <see cref="UiTestOptions.Tolerance" /> otherwise.</param>
    /// <exception cref="UiTestException">It differs, or there is nothing to compare it with yet.</exception>
    public void Screenshot(string name, ImageTolerance? tolerance = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var index = Log.Begin($"screenshot \"{name}\"");
        var outcome = VisualBaseline.Verify(this, name, Capture(), tolerance ?? Options.Tolerance);
        Log.Complete(index, outcome, 0);
    }

    /// <summary>What the cascade resolved a property to on an element, as text.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The property, as a stylesheet writes it.</param>
    /// <returns>The value, or <c>null</c> if nothing gave the element one.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Interned, so this is a lookup rather than a parse</b> — and interning is
    ///         per-document, because two documents number their names independently. Asking through
    ///         the harness rather than caching an id is what keeps a test from carrying one
    ///         document's numbering into another's tables.
    ///     </para>
    ///     <para>
    ///         <see cref="NameTable.Lookup" /> rather than <c>Intern</c>: asking about a property no
    ///         stylesheet ever mentioned must not add it to the table. A read that grows the thing it
    ///         reads makes a test's own assertions change what the next one measures.
    ///     </para>
    /// </remarks>
    public string? StyleOf(UiElement element, string property) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        var id = Document.Styles.Properties.Lookup(property);

        if (id == NameTable.None || !element.Style.TryGet(id, out var value)) {
            return null;
        }

        return Document.Styles.Values.NameOf(value);
    }

    /// <summary>The colour the cascade resolved a property to, if it resolved one.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The property, such as <c>background-color</c>.</param>
    /// <returns>The colour, or <c>null</c>.</returns>
    public Color4? ColorOf(UiElement element, string property) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        var id = Document.Styles.Properties.Lookup(property);
        return id == NameTable.None ? null : Document.ColorOf(element.Style, id);
    }

    /// <summary>The absolute length the cascade resolved a property to, if it resolved one.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The property, such as <c>border-top-left-radius</c>.</param>
    /// <returns>The length in pixels, or <c>null</c>.</returns>
    public float? LengthOf(UiElement element, string property) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        var id = Document.Styles.Properties.Lookup(property);
        return id == NameTable.None ? null : Document.LengthOf(element.Style, id);
    }

    /// <summary>The bare number the cascade resolved a property to, if it resolved one.</summary>
    /// <param name="element">The element.</param>
    /// <param name="property">The property, such as <c>opacity</c> or <c>flex-grow</c>.</param>
    /// <returns>The number, or <c>null</c>.</returns>
    public float? NumberOf(UiElement element, string property) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(property);

        var id = Document.Styles.Properties.Lookup(property);
        return id == NameTable.None ? null : Document.NumberOf(element.Style, id);
    }

    /// <summary>The interface written out, one element per line.</summary>
    /// <remarks>
    ///     What a failure prints, and what to call from a debugger. Tag, id, classes, rectangle and
    ///     text — the five things that answer "why did my selector not match", in the order somebody
    ///     asking that question reads them.
    /// </remarks>
    /// <returns>The tree.</returns>
    public string Tree() {
        var text = new StringBuilder();
        Describe(Document.Root, 0, text);
        return text.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public void Dispose() => Document.Dispose();

    /// <summary>The pointer a single-pointer command uses.</summary>
    /// <remarks>
    ///     A mouse is one pointer and so is a first finger, and every command that does not say
    ///     otherwise means this one — so a test reads the same whether the platform it is standing in
    ///     for has a mouse or a touchscreen.
    /// </remarks>
    public const int PointerId = 1;

    /// <summary>The pointer a two-finger gesture uses for its second finger.</summary>
    public const int SecondPointerId = 2;

    /// <summary>Every element under one, in document order, itself included.</summary>
    internal static IEnumerable<UiElement> Descendants(UiElement element) {
        yield return element;

        foreach (var child in element.Children) {
            foreach (var descendant in Descendants(child)) {
                yield return descendant;
            }
        }
    }

    /// <summary>How an element reads in a failure message.</summary>
    internal string Describe(UiElement element) {
        var text = new StringBuilder("<").Append(element.Tag);

        if (Document.Styles.Tree.IsAlive(element.StyleNode)) {
            if (Document.Styles.Tree.GetId(element.StyleNode) is { } id) {
                text.Append(" #").Append(id);
            }

            foreach (var className in Document.Styles.Tree.GetClassNames(element.StyleNode)) {
                text.Append(" .").Append(className);
            }
        }

        return text.Append('>').ToString();
    }

    UiElement? Send(PointerAction action, PointerButton button, int pointer) {
        var position = PointerAt(pointer);

        return Document.Dispatch(new PointerEvent {
            PointerId = pointer,
            X = position.X,
            Y = position.Y,
            Button = button,
            Modifiers = Modifiers,
            Action = action,
            Timestamp = Now
        });
    }

    void Describe(UiElement element, int depth, StringBuilder text) {
        text.Append(' ', depth * 2).Append(Describe(element));

        if (!element.IsRemoved) {
            text.Append(' ')
                .Append(element.Left.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                .Append(',')
                .Append(element.Top.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(element.Width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                .Append('×')
                .Append(element.Height.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (element.Text is { Length: > 0 } content) {
            text.Append(" \"").Append(content).Append('"');
        }

        text.AppendLine();

        foreach (var child in element.Children) {
            Describe(child, depth + 1, text);
        }
    }
}
