// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;
using Vixen.Ui.Styling;
using Vixen.Ui.Text;

namespace Vixen.Ui;

/// <summary>An element tree, its stylesheets, and the pass that turns one into geometry.</summary>
/// <remarks>
///     <para>
///         Three subsystems that were built and tested apart finally run together here: the cascade
///         decides what applies, <see cref="LayoutStyleBuilder" /> turns that into lengths, and the
///         flexbox engine turns those into rectangles. Everything before this point could be judged
///         by a conformance suite; this is the first thing that can be judged by looking at it.
///     </para>
///     <para>
///         <b>The pass is four walks and they cannot be merged.</b> The cascade needs parents
///         resolved before children because inheritance reads the parent's resolved table; font size
///         needs the same order for the same reason and cannot be folded into the cascade because it
///         is a <i>computed</i> value the cascade has no opinion about; the layout style depends on
///         the font size; and layout itself is the flexbox algorithm, which is not a walk at all.
///     </para>
///     <para>
///         Elements can be removed as well as added — see <see cref="Remove" /> — but a removed
///         style slot is tombstoned rather than reused, so a document that builds and tears down a
///         list every frame grows without bound. <see cref="StyleTree.DeadCount" /> is the number
///         that says so, and compaction is owed.
///     </para>
/// </remarks>
public sealed partial class UiDocument : IDisposable {
    readonly DrawListBuilder drawings;
    readonly int pointerEvents;
    readonly int visibility;
    readonly int visibilityHidden;
    readonly int visibilityCollapse;
    readonly int fontFamily;
    readonly int whiteSpace;
    readonly int textWrap;
    readonly int overflowWrap;
    readonly int wordBreak;
    readonly int textOverflow;
    readonly int lineClamp;
    readonly int tabSize;
    readonly int ellipsis;
    readonly int nowrap;
    readonly int anywhere;
    readonly int breakWord;
    readonly int breakAll;
    readonly int keepAll;
    readonly int textTransform;
    readonly int uppercase;
    readonly int lowercase;
    readonly int capitalize;
    readonly int letterSpacing;
    readonly int wordSpacing;
    readonly int textIndent;
    readonly int fontFeatureSettings;
    readonly int fontVariantNumeric;
    readonly int lineHeight;
    readonly int zIndex;
    readonly int direction;
    readonly int directionRtl;
    readonly int fontWeight;
    readonly int fontStyle;
    readonly int bold;
    readonly int italic;
    readonly int oblique;
    readonly OverflowReader overflow;
    readonly TranslationReader translation;
    readonly TransformReader transform;
    /// <summary>How many tombstoned slots it takes before compacting is worth the walk.</summary>
    /// <remarks>
    ///     A floor rather than a pure ratio, because the ratio alone would compact a four-element
    ///     document that removed three — a walk of the whole tree to reclaim three slots, on the frame
    ///     where somebody happened to close a menu.
    /// </remarks>
    const int CompactionFloor = 64;

    readonly int none;

    /// <summary>The subtrees a <c>Remove</c> is part-way through announcing.</summary>
    /// <remarks>
    ///     ⚠ <b>Because <c>OnRemoved</c> is allowed to remove things and one of them would corrupt
    ///     the walk.</b> Removing a popup from inside the hook is the whole point and is safe. Removing
    ///     an <i>ancestor</i> of the subtree currently being announced is not: the outer call is
    ///     holding an element it is about to detach, and the inner one would detach it first, leaving
    ///     the outer one to take a node out of a parent it no longer has. Refused with a message
    ///     rather than left to be found as a null reference three frames later.
    /// </remarks>
    readonly List<UiElement> removing = [];

    bool dirty = true;

    /// <summary>Whether a pass is running, so that a nested <see cref="Update" /> can refuse.</summary>
    bool updating;

    /// <summary>Creates a document over a surface of a given size.</summary>
    /// <param name="width">The surface's width in device-independent pixels.</param>
    /// <param name="height">Its height.</param>
    /// <param name="rootFontSize">The font size <c>rem</c> measures against.</param>
    /// <param name="logger">
    ///     Where a stylesheet Vixen could not read is reported, or <c>null</c> for nowhere.
    ///     <para>
    ///         ⚠ <b>Optional, and a document given none is a document whose refused rules are silent
    ///         again.</b> That is the same bargain <c>EffectScheduler</c> makes and it is a bargain
    ///         rather than an oversight: a Core assembly cannot invent a sink, and inventing a default
    ///         one would put a second logging story next to <c>Vixen.Core.Diagnostics</c>. Every host
    ///         in this repository that has a log passes it — see <c>EditorShell</c> — so the silent
    ///         case is a document built by a test or by an embedder that has not wired one yet.
    ///     </para>
    /// </param>
    public UiDocument(
        float width,
        float height,
        float rootFontSize = LengthContext.InitialFontSize,
        ILogger? logger = null
    ) {
        this.rootFontSize = rootFontSize;
        this.logger = logger ?? NullLogger.Instance;
        Styles = new StyleEngine();

        // ⚠ Before anything can load a sheet, and that includes this constructor. A sheet installed
        // without the preprocessor in place is a sheet whose `@apply` reaches ExCSS verbatim, and the
        // only symptom is a rule missing declarations.
        Styles.Preprocessor = ExpandApply;
        Restyler = new StyleUpdater(Styles);
        Layout = new LayoutTree();
        Builder = new LayoutStyleBuilder(Styles.Properties, Styles.Values, Styles.Names);
        drawings = new DrawListBuilder(Styles.Properties, Styles.Values, Styles.Names);

        reader = new StyleValueParser(Styles.Values, Styles.Names);

        pointerEvents = Styles.Properties.Intern("pointer-events");
        visibility = Styles.Properties.Intern("visibility");
        visibilityHidden = Styles.Values.Intern("hidden");
        visibilityCollapse = Styles.Values.Intern("collapse");
        color = Styles.Properties.Intern("color");
        fontFamily = Styles.Properties.Intern("font-family");
        whiteSpace = Styles.Properties.Intern("white-space");
        textWrap = Styles.Properties.Intern("text-wrap");
        overflowWrap = Styles.Properties.Intern("overflow-wrap");
        wordBreak = Styles.Properties.Intern("word-break");
        textOverflow = Styles.Properties.Intern("text-overflow");
        lineClamp = Styles.Properties.Intern("-webkit-line-clamp");
        tabSize = Styles.Properties.Intern("tab-size");
        ellipsis = Styles.Values.Intern("ellipsis");
        nowrap = Styles.Values.Intern("nowrap");
        anywhere = Styles.Values.Intern("anywhere");
        breakWord = Styles.Values.Intern("break-word");
        breakAll = Styles.Values.Intern("break-all");
        keepAll = Styles.Values.Intern("keep-all");
        textTransform = Styles.Properties.Intern("text-transform");
        uppercase = Styles.Values.Intern("uppercase");
        lowercase = Styles.Values.Intern("lowercase");
        capitalize = Styles.Values.Intern("capitalize");
        letterSpacing = Styles.Properties.Intern("letter-spacing");
        wordSpacing = Styles.Properties.Intern("word-spacing");
        textIndent = Styles.Properties.Intern("text-indent");
        fontFeatureSettings = Styles.Properties.Intern("font-feature-settings");
        fontVariantNumeric = Styles.Properties.Intern("font-variant-numeric");
        lineHeight = Styles.Properties.Intern("line-height");
        zIndex = Styles.Properties.Intern("z-index");
        direction = Styles.Properties.Intern("direction");
        directionRtl = Styles.Values.Intern("rtl");
        fontWeight = Styles.Properties.Intern("font-weight");
        fontStyle = Styles.Properties.Intern("font-style");
        bold = Styles.Values.Intern("bold");
        italic = Styles.Values.Intern("italic");
        oblique = Styles.Values.Intern("oblique");
        overflow = new OverflowReader(Styles.Properties, Styles.Values);
        translation = new TranslationReader(Styles.Properties, Styles.Values, Styles.Names);
        transform = new TransformReader(Styles.Properties, Styles.Values, Styles.Names);
        none = Styles.Values.Intern("none");
        InternCursors();
        InternContainers();

        Root = Create("root", null, null, []);

        // ⚠ After the root, because a surface is a place a subtree is shown and the primary one
        // shows the whole document. It is the first entry of `Surfaces` and the only one that
        // cannot be removed, which is what makes every single-window caller — every test, every
        // sample, the whole of `Vixen.Ui.Testing` — carry on meaning what it meant.
        // ⚠ `MediaScopes.Document` rather than a scope of its own, so that an element created
        // outside every surface — which is what `Root` itself is until this line runs — is in the
        // same scope as the primary window rather than in one that answers nothing.
        var primary = new UiSurface(
            this,
            0,
            Root,
            width,
            height,
            1f,
            Drawing,
            ColorSchemePreference.NoPreference
        ) {
            Scope = MediaScopes.Document
        };

        Adopt(primary, width, height, 1f);

        // ⚠ After the surface exists, because the context is read off it — and before any caller can
        // reach `Load`, because a sheet loaded against a nought-by-nought surface would answer every
        // breakpoint no on its first pass. Nothing is loaded yet, so this only records the context;
        // see `Media.cs`.
        Remedia(primary);
    }

    /// <summary>What <c>rem</c> measures against, kept because a new surface needs it too.</summary>
    readonly float rootFontSize;

    /// <summary>The cascade.</summary>
    public StyleEngine Styles { get; }

    /// <summary>Which component built which element, for the elements that are a component's host.</summary>
    /// <remarks>
    ///     ⚠ <b>Weak on the element, which is what makes this bookkeeping-free.</b> A component is
    ///     reachable for exactly as long as the element it drew itself into is, so a branch that
    ///     leaves the tree takes its component with it and nothing has to be told. A dictionary
    ///     would need an entry removed at every place an element can go, and the one that was
    ///     forgotten would be a leak nobody could see.
    /// </remarks>
    readonly ConditionalWeakTable<UiElement, Composition.Component> components = [];

    /// <summary>What has to run when an element a component drew itself into is removed.</summary>
    /// <remarks>
    ///     ⚠ <b>Because a component is not an element and has no hook of its own in the tree.</b> A
    ///     markup-authored <see cref="UiElement" /> stops its own composition from <c>OnRemoved</c>;
    ///     a <see cref="Composition.Component" /> can only be reached through the element it drew
    ///     itself into, so that is where the answer is kept. Without it, the only thing that ended a
    ///     component was the region that built it — and a component built onto a mount, which is
    ///     every markup panel in the editor, has no region above it. An inspector rebuilt on every
    ///     selection change leaked a panel's worth of effects each time.
    ///
    ///     ⚠ Weak on the element, for the reason <see cref="components" /> is: the entry is wanted
    ///     for exactly as long as the host is, and a strong table would need a removal at every
    ///     place an element can go.
    /// </remarks>
    readonly ConditionalWeakTable<UiElement, IDisposable> teardowns = [];

    /// <summary>The component whose host this element is, if it is one.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The component, or null when the element is not a component's host.</returns>
    /// <remarks>
    ///     <para>
    ///         The question every caller of a mounted component eventually asks. A component is not
    ///         an element, so a panel that was handed one and a test that goes looking for one have
    ///         no way back from the tree to the object — which is what a control gives for free
    ///         simply by <i>being</i> the element.
    ///     </para>
    ///     <para>
    ///         It is also what keeps a mounted component alive. Its elements are in the document and
    ///         its <i>effects</i> are not: before this, the only thing holding a panel's bindings was
    ///         whatever reference the caller happened to keep, so a caller that mounted a component
    ///         and dropped it had a subtree that stopped updating at the next collection.
    ///     </para>
    /// </remarks>
    public Composition.Component? ComponentAt(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);
        return components.TryGetValue(element, out var component) ? component : null;
    }

    /// <summary>Records that a component drew itself into an element.</summary>
    /// <remarks>
    ///     <c>AddOrUpdate</c> rather than <c>Add</c>: a hot reload re-mounts the same component onto
    ///     the same host, and a second <c>Add</c> for one key throws.
    /// </remarks>
    internal void Mounted(UiElement host, Composition.Component component) =>
        components.AddOrUpdate(host, component);

    /// <summary>Records what ends the component whose host this element is.</summary>
    internal void TearsDownAt(UiElement host, IDisposable teardown) => teardowns.AddOrUpdate(host, teardown);

    /// <summary>Ends the component this element is the host of, if it is one and it has not ended.</summary>
    void TearDown(UiElement element) {
        if (!teardowns.TryGetValue(element, out var teardown)) {
            return;
        }

        teardowns.Remove(element);
        teardown.Dispose();
    }

    /// <summary>Where this document's bindings queue, and what a frame drains.</summary>
    /// <remarks>
    ///     <para>
    ///         Every effect <see cref="Composition.BuildContext" /> creates is registered here rather
    ///         than on <see cref="Reactive.EffectScheduler.Default" />, which is what the scheduler's
    ///         own advice says a thing that owns a frame loop should do — and a document is exactly
    ///         that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default is per <i>thread</i>, and that is the wrong granularity here.</b> An
    ///         editor has more than one document — a shell, a preview pane, a floating window — and
    ///         a test process has one per test. Flushing the thread's queue from one document's
    ///         frame runs the bindings of every other document on that thread, including the
    ///         disposed ones, whose effects then assign to elements that have been removed. It is
    ///         not a hypothetical: it turned a ten-second test run into one that did not finish.
    ///     </para>
    ///     <para>
    ///         <b>Drained by <see cref="Update" />, before the pass and never inside one.</b> A pass
    ///         walks the tree and an effect mutates it, so an effect that ran mid-walk would change
    ///         the thing being walked — which is the whole reason writing a signal only ever queues.
    ///         Draining at the top of <see cref="Update" /> satisfies that and is the one place that
    ///         knows where a pass begins; a nested call is already inside one and drains nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This used to be the host's job, and it was a job hosts did not know they had.</b>
    ///         Every test flushes explicitly — which is why the gap was invisible — and of the real
    ///         hosts only <c>EditorShell</c> did. A game built on the <c>vixen-app</c> template, or on
    ///         <c>Samples/02</c>, drew an interface whose bindings never ran: a signal written from a
    ///         click queued an effect that nothing dequeued, so the element it assigned to kept the
    ///         value it was built with for the life of the process.
    ///     </para>
    ///     <para>
    ///         A host that wants the drain at some other point in its frame — <c>EditorShell</c> does,
    ///         because it pumps its dialogs and its background tasks first — calls
    ///         <see cref="Reactive.EffectScheduler.Flush" /> itself and the pass then finds nothing
    ///         queued. Flushing twice is not an error; it is a queue, and the second one is empty.
    ///     </para>
    /// </remarks>
    public Reactive.EffectScheduler Effects { get; } = new();

    /// <summary>What holds every element's computed style and keeps it that way.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>StyleEngine.ResolveAll</c>, which is what this used to be and is why a hover
    ///     cost a full cascade.</b> The engine resolves the document; the updater resolves what a
    ///     change could have reached and stops descending where the answer did not move. Both produce
    ///     the same styles — that is the property <c>IncrementalDocumentTests</c> gates — and only one
    ///     of them is affordable sixty times a second.
    /// </remarks>
    public StyleUpdater Restyler { get; }

    /// <summary>The flexbox engine.</summary>
    public LayoutTree Layout { get; }

    /// <summary>The step between them.</summary>
    public LayoutStyleBuilder Builder { get; }

    /// <summary>The commands the last <see cref="Draw()" /> produced.</summary>
    public DrawList Drawing { get; } = new();

    /// <summary>Whether a translucent subtree is composited as a group rather than faded in place.</summary>
    /// <remarks>
    ///     ⚠ <b>Set it to match the renderer that will draw this document, and never to match a
    ///     preference.</b> See <see cref="DrawListBuilder.Compositing" />: a group is only a picture if
    ///     whoever consumes the draw list renders <c>UiGeometry.Layers</c> into offscreen surfaces, and
    ///     a consumer that ignores them draws a faded panel at full strength rather than approximating
    ///     it. On by default now that both <c>SoftwareUiRasterizer</c> and <c>Vixen.Ui.Renderer</c>
    ///     composite — which for the second one means the host also calls <c>UiRenderer.Compose</c> and
    ///     supplied a <c>UiShaders.Image</c>. Turn it off for a consumer of your own that does neither.
    /// </remarks>
    public bool Compositing {
        get => drawings.Compositing;
        set => drawings.Compositing = value;
    }

    /// <summary>The primary surface's size and root font size.</summary>
    /// <remarks>
    ///     The <i>primary</i> one, now that there can be more than one — see <see cref="Surfaces" />.
    ///     A length resolved against this is resolved against the main window, which is what every
    ///     caller predating multiple surfaces meant by it; content in a torn-off window reads
    ///     <see cref="UiSurface.Metrics" /> instead, and the pass hands it down rather than making
    ///     anything ask.
    /// </remarks>
    public LengthContext Viewport => Primary.Metrics;

    /// <summary>The element every other one descends from.</summary>
    public UiElement Root { get; }

    /// <summary>How many elements had a layout style written on the last pass.</summary>
    /// <remarks>
    ///     Exposed because it is the number the incremental story is about, and a claim about work
    ///     avoided that cannot be measured is a claim nobody can check. A second
    ///     <see cref="Update" /> over an unchanged tree should report zero.
    /// </remarks>
    public int StylesApplied { get; private set; }

    /// <summary>Loads a stylesheet.</summary>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <returns>The sheet's index, for <see cref="ReloadStyles" />.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>@apply</c> is expanded on the way in, against every <c>@theme</c> the document
    ///         holds rather than only this sheet's — see <see cref="ExpandApply" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A sheet that brings tokens re-expands the ones before it.</b> <c>ControlTheme</c>
    ///         is installed before <c>EditorTheme</c>, so an <c>@apply p-4</c> in the first would
    ///         otherwise be measured against a <c>--spacing</c> the second had not declared yet — and
    ///         the shipped palette answers, so the failure is a wrong number rather than an error.
    ///         See <see cref="TokensCameLate" /> for what makes this cost nothing in a document
    ///         without an <c>@apply</c> in it.
    ///     </para>
    /// </remarks>
    public int Load(string css, StyleOrigin origin = StyleOrigin.Author) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(css);

        // Asked before the load, so that "a sheet that came earlier" excludes this one.
        var late = TokensCameLate(css);

        // The merge is not invalidated here: a load always grows the sheet list, and `Theme` rebuilds
        // whenever the count it was built from has moved. `ReloadStyles` is the case that has to say
        // so out loud, because replacing a sheet leaves the count alone.
        var sheet = Styles.Load(css, origin);

        if (late) {
            Styles.Reload();
            Forget();
        }

        DrainStyleDiagnostics();
        Invalidate();

        return sheet;
    }

    /// <summary>Loads a stylesheet once for a key, however many times it is asked for.</summary>
    /// <param name="key">What the sheet belongs to. A component's type.</param>
    /// <param name="css">Its text.</param>
    /// <param name="origin">Who it came from.</param>
    /// <returns>Whether this call was the one that loaded it.</returns>
    /// <remarks>
    ///     ⚠ <b>Per document, not per process.</b> Two documents are two cascades — an editor with a
    ///     second window loads the same component's rules into each of them — and a static set would
    ///     leave the second document styling nothing at all, which is the kind of bug that only
    ///     appears once somebody opens a second window.
    /// </remarks>
    public bool LoadOnce(object key, string css, StyleOrigin origin = StyleOrigin.Author) {
        // ⚠ Above the `loadedOnce` set rather than left to `Load`. A refused key is remembered even
        // when the load never happens, so a disposed document would answer the *next* caller of this
        // key `false` — a sheet silently never loaded, on a document that had since been rebuilt.
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);

        if (!loadedOnce.Add(key)) {
            return false;
        }

        Load(css, origin);
        return true;
    }

    readonly HashSet<object> loadedOnce = [];

    /// <summary>Replaces a loaded stylesheet with new text.</summary>
    /// <param name="sheet">The index <see cref="Load" /> returned.</param>
    /// <param name="css">The new text.</param>
    /// <remarks>
    ///     <para>
    ///         Forgets what every element applied, for the same reason <see cref="Resize(float,float)" /> does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is currently redundant, and kept anyway.</b> A reload rebuilds the interning
    ///         cache, so a computed style from before it is a different object from the identical
    ///         one after — the pass's reference comparison already calls every element changed, and
    ///         replacing this with a plain <c>Invalidate</c> breaks no test. It stays because the
    ///         redundancy is an accident of how the reload happens to be implemented rather than a
    ///         property of what it means, and an interning cache that survived a reload one day
    ///         would turn that accident into every element keeping the geometry a deleted rule gave
    ///         it. Said out loud rather than defended by a test that cannot exist.
    ///     </para>
    /// </remarks>
    public void ReloadStyles(int sheet, string css) {
        ThrowIfDisposed();

        // ⚠ The sheet count does not move, so `Theme`'s own staleness check cannot see this — and a
        // saved theme file whose `--spacing` just changed has to reach every `@apply` in the
        // document, not only the ones in the sheet that was saved. `Replace` reloads everything,
        // which re-runs the preprocessor over all of them against the merge rebuilt below.
        theme = null;

        Styles.Replace(sheet, css);
        DrainStyleDiagnostics();
        Forget();
    }

    /// <summary>Changes the surface's size.</summary>
    /// <param name="width">The new width.</param>
    /// <param name="height">The new height.</param>
    /// <remarks>
    ///     ⚠ Forgets what was applied rather than only marking the document dirty. Nothing an
    ///     element <i>declared</i> has changed, so its computed style is the same interned object
    ///     and its font size is the same number — the skip below would match on both and every
    ///     <c>vw</c> in the document would keep its old value while the window visibly changed size.
    ///     A document with no viewport-relative length pays for the rebuild, and finding out which
    ///     documents those are is not worth the bookkeeping: resizing happens at human speed.
    /// </remarks>
    public void Resize(float width, float height) => Resize(Primary, width, height, Primary.DpiScale);

    /// <summary>Marks the document as needing a fresh pass over every element.</summary>
    /// <remarks>
    ///     ⚠ <b>The conservative door, and every caller that is not a class or a state change comes
    ///     through it.</b> A new element, a removal, a move, an inline style and a stylesheet all land
    ///     here and all cost a cold pass. That is correct — <see cref="StyleUpdater" /> narrows a
    ///     change to <i>an existing element's</i> names or state and cannot express any of them — and
    ///     it is the reason this stays public and unnarrowed: an outside caller that has changed
    ///     something the document cannot see must get the pass that assumes the worst.
    /// </remarks>
    public void Invalidate() {
        dirty = true;
        ForgetChanges();
    }

    /// <summary>Marks every element as needing its layout style rebuilt.</summary>
    void Forget() {
        Forget(Root);
        Invalidate();
    }

    static void Forget(UiElement element) {
        element.AppliedStyle = null;

        foreach (var child in element.Children) {
            Forget(child);
        }
    }

    /// <summary>Creates an element.</summary>
    /// <param name="tag">Its element name.</param>
    /// <param name="parent">Its parent, or <c>null</c> for the root.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    public UiElement Create(string tag, UiElement? parent, string? id = null, params ReadOnlySpan<string> classNames) =>
        Create<UiElement>(tag, parent, id, classNames);

    /// <summary>Creates an element of a particular type.</summary>
    /// <typeparam name="T">The element type, which needs a parameterless constructor.</typeparam>
    /// <param name="tag">Its element name, or <c>null</c> to take the one the type answers to.</param>
    /// <param name="parent">Its parent, or <c>null</c> for the root.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instance is made before the style node, which is the opposite of the obvious
    ///         order and is what lets a type name itself.</b> A control's stylesheet selects on its
    ///         tag — <c>button { … }</c> — and a caller that had to pass <c>"button"</c> alongside
    ///         <c>Button</c> would eventually pass something else, at which point the control is
    ///         still a <see cref="UiElement" /> and silently unstyled. Asking the element for
    ///         <see cref="UiElement.TagName" /> makes the two impossible to disagree.
    ///     </para>
    ///     <para>
    ///         <b>Three steps, in this order:</b> bind, attach, then
    ///         <see cref="UiElement.OnCreated" />. A control builds its parts in that last one and
    ///         every one of them needs a document to be created in — so the hook cannot be a
    ///         constructor, and it cannot run before the element is in the tree either, because a
    ///         part added to an unattached parent would be laid out relative to nothing.
    ///     </para>
    /// </remarks>
    public T Create<T>(string? tag, UiElement? parent, string? id = null, params ReadOnlySpan<string> classNames)
        where T : UiElement, new() =>
        (T) Adopt(new T(), tag, parent, id, classNames);

    /// <summary>Makes an element this document already has an instance of part of it.</summary>
    /// <param name="element">The instance, which must not belong to a document yet.</param>
    /// <param name="tag">Its element name, or <c>null</c> to take the one the type answers to.</param>
    /// <param name="parent">Its parent, or <c>null</c> for the root.</param>
    /// <param name="id">Its identifier.</param>
    /// <param name="classNames">Its classes.</param>
    /// <returns>The element.</returns>
    /// <remarks>
    ///     The half of <see cref="Create{T}" /> below the <c>new</c>, and it is separate because
    ///     <see cref="Composition.BuildContext" /> cannot use the generic form: the type argument it
    ///     has is constrained to <see cref="Composition.IComposable" /> rather than to
    ///     <see cref="UiElement" />, because the same tag may name a component instead. Everything
    ///     the order of these three steps buys is documented on <see cref="Create{T}" /> and none of
    ///     it changed.
    /// </remarks>
    public UiElement Adopt(
        UiElement element,
        string? tag,
        UiElement? parent,
        string? id = null,
        params ReadOnlySpan<string> classNames
    ) {
        // ⚠ The one seam both `Create` overloads come through, and the one that used to abort the
        // process rather than throw: creating an element allocates a layout node, and a `LayoutTree`
        // whose arrays had been freed grew from a capacity of nought by copying out of them and
        // freeing them again. `LayoutTree.Dispose` clears its four fields now, so the abort is gone
        // and this guard is back to doing the ordinary job of a guard. See `ThrowIfDisposed`.
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(element);
        tag ??= element.TagName;

        var styleNode = Styles.Tree.CreateElement(tag, parent?.StyleNode, id, classNames);
        var layoutNode = Layout.CreateNode();

        element.Bind(this, tag, parent, styleNode, layoutNode);

        if (parent is not null) {
            parent.Attach(element);
            Layout.AddChild(parent.LayoutNode, layoutNode);
        }

        Invalidate();
        element.OnCreated();

        // ⚠ After the child's own `OnCreated` and not inside `Attach`, because the first thing a
        // registrar does is read a part the child builds there — see `UiElement.OnChildAdded`, which
        // is what makes a container writable as nested tags at all.
        parent?.OnChildAdded(element);

        return element;
    }

    /// <summary>Moves an element to another position among its siblings.</summary>
    /// <param name="element">The element to move.</param>
    /// <param name="index">Where it should end up.</param>
    /// <remarks>
    ///     <para>
    ///         All three stores at once, for the same reason removal is: an element is a handle into
    ///         a style tree and a layout tree, and one moved in only two of them is in two places.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reordering is a style change, not just a layout one.</b> <c>:nth-child</c>,
    ///         <c>:first-child</c> and the sibling combinators all read position, so moving an
    ///         element restyles it and the siblings it passed. That is why this invalidates rather
    ///         than only marking the layout dirty — and it is the reason a reconciler that moves
    ///         elements is worth having over one that rebuilds them, because a rebuild loses the
    ///         focus and the scroll position as well.
    ///     </para>
    ///     <para>
    ///         Within one parent only. Reparenting would move an element's style slot relative to
    ///         its new parent's, and a child whose slot is below its parent's breaks the three
    ///         passes that read slot order as depth order — the same invariant that makes removal
    ///         tombstone rather than reuse.
    ///     </para>
    /// </remarks>
    public void Move(UiElement element, int index) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(element);

        if (!ReferenceEquals(element.Document, this)) {
            throw new ArgumentException("that element belongs to another document.", nameof(element));
        }

        if (element.Parent is not { } parent) {
            throw new InvalidOperationException("the root has no siblings to move among.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, parent.Children.Count);

        if (element.IndexInParent == index) {
            return;
        }

        parent.MoveChild(element, index);

        // ⚠ The style tree takes the element index unchanged below and the layout tree cannot, for
        // the reason `LayoutIndexOf` documents: a surface root stays in the element tree and the
        // style tree and is taken out of the layout tree's child list, so a parent that owns one has
        // two different child counts. `index` is read after `MoveChild`, when the element is already
        // where it is going, so the surface roots counted are the ones genuinely ahead of it.
        //
        // ⚠ And a surface root being moved touches the layout tree not at all — it is not in a child
        // list to be moved within, and inserting it would lay a second window out inside the first.
        if (element.SurfaceRoot is null) {
            Layout.RemoveChild(parent.LayoutNode, element.LayoutNode);
            Layout.InsertChild(parent.LayoutNode, element.LayoutNode, LayoutIndexOf(parent, index));
        }

        Styles.Tree.Move(element.StyleNode, index);
        Invalidate();
    }

    /// <summary>Removes an element and everything under it.</summary>
    /// <param name="element">The element.</param>
    /// <remarks>
    ///     <para>
    ///         Out of all three stores at once, which is the point of doing it here rather than in
    ///         any of them: an element is a handle into a style tree and a layout tree, and one that
    ///         left either behind would keep matching selectors or keep taking up space in a flex
    ///         line while being gone from the document.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whatever was pointing at it has to stop.</b> The focus, a captured pointer and a
    ///         gesture in progress all name an element, and each of them outlives the element unless
    ///         something says otherwise — a drag whose target was deleted mid-drag delivers its next
    ///         move to a detached object, and a focus left on a removed element makes Tab start from
    ///         somewhere that is not on the screen.
    ///     </para>
    ///     <para>
    ///         The root cannot be removed. A document without one has no tree to walk and nothing to
    ///         lay out, and the alternative to refusing is a null check in every pass.
    ///     </para>
    /// </remarks>
    public void Remove(UiElement element) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(element);

        if (ReferenceEquals(element, Root)) {
            throw new InvalidOperationException("the root cannot be removed — a document is its tree.");
        }

        if (!ReferenceEquals(element.Document, this)) {
            throw new ArgumentException("that element belongs to another document.", nameof(element));
        }

        // An `OnRemoved` that removes something already on its way out. Its own subtree is fine — it
        // is about to go regardless — but an ancestor of one is not: see `removing`.
        foreach (var pending in removing) {
            for (var ancestor = pending; ancestor is not null; ancestor = ancestor.Parent) {
                if (ReferenceEquals(ancestor, element)) {
                    throw new InvalidOperationException(
                        "OnRemoved cannot remove an ancestor of the element being removed — "
                        + "the outer removal is holding it. Remove what the control owns elsewhere in "
                        + "the tree instead."
                    );
                }
            }
        }

        // ⚠ Before anything is detached, and before `Release`, because an override's whole purpose is
        // to reach elsewhere in the document — a menu closing the popover it parented on the root —
        // and a handler that runs after the subtree is out of the stores can ask almost nothing. It
        // may remove other elements; `removing` is what stops it removing one of these.
        removing.Add(element);

        try {
            Announce(element);
        } finally {
            removing.Remove(element);
        }

        // Before anything is detached, because finding out whether the focus is inside the subtree
        // means walking up from the focus to a parent this is about to clear.
        Release(element);

        element.Parent?.Detach(element);
        Layout.RemoveChild(element.Parent!.LayoutNode, element.LayoutNode);
        Layout.DestroyRecursive(element.LayoutNode);
        Styles.Tree.Remove(element.StyleNode);

        Retire(element);
        Invalidate();
    }

    /// <summary>Drops anything that was pointing into a subtree about to go.</summary>
    void Release(UiElement element) {
        for (var focused = Focused; focused is not null; focused = focused.Parent) {
            if (ReferenceEquals(focused, element)) {
                Focus(null);
                break;
            }
        }

        // ⚠ Separately from the focus, because the two are allowed to be different elements — that
        // is what `CommandFocus` is for — and the one this releases is by definition the one the
        // focus is *not* on. A view removed while a menu is open would otherwise leave the route
        // walking up through the parents of something no longer in the tree.
        ReleaseCommandFocus(element);

        for (var captured = Captured; captured is not null; captured = captured.Parent) {
            if (ReferenceEquals(captured, element)) {
                ReleasePointer();
                break;
            }
        }

        Gestures.Forget(element);
        ForgetHover(element);
    }

    /// <summary>Tells a subtree it is going, deepest last.</summary>
    /// <remarks>
    ///     ⚠ <b>Parents before children, which is the opposite of a disposal order and is right
    ///     here.</b> A control's <c>OnRemoved</c> tears down what it owns, and what it owns includes
    ///     its own parts — so a panel that closes its menu wants to run before that menu's own hook,
    ///     not after it has already been told. It mirrors <c>OnCreated</c>, which builds outward from
    ///     the type that was asked for.
    ///
    ///     The list is snapshotted per level, because a handler may add or remove children of the
    ///     element it is called on — a popover closing removes its own items — and iterating the live
    ///     collection would then skip half of them.
    ///
    ///     ⚠ <b>And a component's teardown is announced here too, after the element's own hook.</b>
    ///     It is the same event — this subtree is going — for the one kind of thing in the tree that
    ///     cannot be told directly, because a <see cref="Composition.Component" /> is not an element.
    ///     After, so that a control's <c>OnRemoved</c> still runs against a component that has not
    ///     yet been stopped. See <see cref="teardowns" />.
    /// </remarks>
    void Announce(UiElement element) {
        element.OnRemoved();
        TearDown(element);

        foreach (var child in element.Children.ToArray()) {
            Announce(child);
        }
    }

    /// <summary>Marks a subtree as no longer part of any document.</summary>
    static void Retire(UiElement element) {
        element.Retire();

        foreach (var child in element.Children) {
            Retire(child);
        }
    }

    /// <summary>How many times the style store has been compacted.</summary>
    /// <remarks>
    ///     Exposed for the same reason <c>DrawList.Batched</c> is: "a document that builds and tears
    ///     down a list no longer grows without bound" is a claim, and a claim about work that cannot
    ///     be counted is one nobody can check.
    /// </remarks>
    public int StyleCompactions { get; private set; }

    /// <summary>Reclaims the style slots removal left behind.</summary>
    /// <returns>Whether anything was reclaimed.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the document's to do and nobody else's.</b> A slot is an index, so
    ///         compacting moves every <c>StyleNodeId</c> in existence — and the only object that
    ///         knows where they all are is the one that handed them out. <c>StyleTree.Compact</c>
    ///         therefore returns a mapping rather than doing this quietly, and this is what walks the
    ///         element tree applying it.
    ///     </para>
    ///     <para>
    ///         Public as well as automatic, because a caller that has just torn down a large subtree
    ///         knows something the heuristic below does not.
    ///     </para>
    /// </remarks>
    public bool CompactStyles() {
        var tree = Styles.Tree;

        if (tree.DeadCount == 0) {
            return false;
        }

        var remap = new int[tree.Count];
        tree.Compact(remap);
        Remap(Root, remap);

        // The updater's styles are indexed by slot, so a compaction it was not told about would leave
        // every element wearing the style of whatever used to be several slots along.
        //
        // ⚠ **Insurance, and labelled as insurance because a sabotage deleting it failed to fail.**
        // The line below forces the next pass to be cold, and a cold pass writes every entry of that
        // array — so the remapped values are overwritten before anything can read one. It is kept
        // because `StyleUpdater.Compact` is part of the updater's own contract rather than a
        // courtesy, and because the redundancy is a property of *these two lines being adjacent*: a
        // compaction that one day preserves the incremental pass makes the remap load-bearing again,
        // and finding that out by way of a wrong interface would be finding it out the hard way.
        Restyler.Compact(remap);

        // ⚠ Nor is this one insurance, and no amount of coldness saves it. A running transition is
        // keyed by slot and lives *across* passes by design — that is what makes it a transition, and
        // it is the one piece of per-element state a cold pass does not rewrite — so a compaction it
        // was not told about leaves every fade in the document playing on whatever element has since
        // landed on its index. `Animator.Compact` remaps rather than clearing, for the reason written
        // on it: clearing would jolt every fade on the frame a list happened to shrink, which is a
        // worse bug than the leak and a rarer one.
        Styles.Animations.Compact(remap);

        // ⚠ This one is not insurance either. A recorded change names a slot, compaction moves every slot,
        // and a change replayed afterwards would restyle whatever has since landed on that index.
        ForgetChanges();
        StyleCompactions++;

        return true;
    }

    /// <summary>Points every element at the slot its style moved to.</summary>
    /// <remarks>
    ///     ⚠ A walk of the tree, so every live element is reached exactly once and no removed one is.
    ///     A list in creation order would need the removed entries taken out of it first, which is the
    ///     bookkeeping compaction exists to stop doing.
    /// </remarks>
    static void Remap(UiElement element, ReadOnlySpan<int> remap) {
        element.Restyle(new StyleNodeId(remap[element.StyleNode.Index]));

        foreach (var child in element.Children) {
            Remap(child, remap);
        }
    }

    /// <summary>Runs the passes, if anything has changed since the last one.</summary>
    /// <returns>Whether any work was done.</returns>
    /// <remarks>
    ///     ⚠ <b>A call from inside a pass does nothing, and leaves the document dirty.</b> Several
    ///     controls run a pass in the middle of their own refresh — <c>TreeView</c>, <c>DataGrid</c>
    ///     and <c>CodeEditor</c> all write a content height as a declaration and then need it as a
    ///     measurement — and those same refreshes are what <see cref="LayoutFinished" /> exists to
    ///     call. Without this guard, hanging one on the event re-enters <c>Update</c> from inside
    ///     <see cref="Settle" />, which invokes the handlers again from a nested frame: the recursion
    ///     terminates only because the document runs out of changes, and the
    ///     <see cref="SettlePasses" /> budget that is supposed to bound it is reset by every nested
    ///     call. Refusing the nested call instead makes the settle loop the one place a pass is run,
    ///     and the refresh gets its measurement on the next turn of that loop rather than from a
    ///     stack frame underneath itself.
    /// </remarks>
    public bool Update() {
        ThrowIfDisposed();

        // ⚠ Inside the guard rather than above it. Dirtying nodes and invalidating the document from
        // underneath a pass that is already walking them is the one thing the guard exists to refuse,
        // and a registration made mid-pass is picked up by the loop that is already running.
        if (!updating) {
            // ⚠ Before `Refont` and before the dirty check, because an effect is the most common way
            // a frame becomes dirty at all: a binding assigns a class, a text or a child list, and
            // the document is clean until it has run. Draining after the early return would be a
            // pass that goes home having read the queue's effects one frame late, for ever.
            Effects.Flush();

            Refont();
        }

        // Not cleared on the way out of the guard: the caller's writes are still pending, and the
        // loop that is already running is what will pick them up.
        if (updating || !dirty) {
            if (!updating) {
                StylesApplied = 0;
            }

            return false;
        }

        updating = true;
        dirty = false;
        StylesApplied = 0;
        StylesResolved = 0;

        // ⚠ Here rather than in `Recontain`, for the reason `StylesResolved` is: the settle loop
        // arranges again and the counter is about the frame rather than about the last pass of it.
        ContainerScopesEntered = 0;

        // ⚠ Before anything reads a slot, and only when the tombstones outnumber the elements. Here
        // rather than in `Remove`, because compaction is O(elements) and removing a thousand-row list
        // one row at a time would then be O(elements²) — and because a pass is the one moment where
        // every id is about to be re-read anyway, so nothing is holding a stale one across it.
        //
        // The floor stops a document with four elements compacting because it removed three.
        if (Styles.Tree.DeadCount >= CompactionFloor && Styles.Tree.DeadCount > Styles.Tree.LiveCount) {
            CompactStyles();
        }

        // ⚠ Before the restyle, because the restyle is what starts transitions: the updater stamps
        // each one with this and a stamp from the previous frame is a fade that begins in the past.
        Restyler.Now = seconds;

        StylesResolved = Restyle();
        Arrange();

        try {
            Settle();
        } finally {
            // In a finally, because a handler is application code and is entitled to throw. A flag
            // left set would make every later Update a silent no-op — an interface that stops
            // repainting, with nothing in the exception to say why.
            updating = false;
        }

        // ⚠ After the settle rather than after the restyle, because a settle pass restyles too — a
        // handler that assigns a class runs the bridge again, and a drain placed above `Settle` would
        // report the refusals of the first pass and hold the rest until the next frame. See
        // `DrainBuilderDiagnostics`, and note that it is outside the `finally` on purpose.
        DrainBuilderDiagnostics();

        return true;
    }

    /// <summary>What <see cref="FontRegistry.Revision" /> was when the text was last measured.</summary>
    int measuredFonts;

    /// <summary>Makes every element that measures text measure it again, if the faces have changed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The other half of <see cref="FontRegistry.Revision" />, and without it the first
    ///         half buys nothing.</b> <see cref="UiElement.Block()" /> compares the revision and so drops
    ///         a line shaped against faces that have since changed — but a line is only rebuilt when
    ///         somebody asks for one, and what asks is the measure function, and what calls the
    ///         measure function is a layout pass over a node that is <i>dirty</i>. Registering a face
    ///         changes nothing on an element, so nothing is dirty, so nothing measures: an element
    ///         that measured zero because there was no face when it was first laid out keeps the zero
    ///         for the life of the document.
    ///     </para>
    ///     <para>
    ///         Which is the shape of the fault it repairs — a host that builds its interface and
    ///         <i>then</i> installs a font gets an interface whose text is the right colour, the right
    ///         string and nought pixels wide. The menu bar and the toolbar of the editor were exactly
    ///         that, while every panel below them was fine, because a panel is rebuilt after the first
    ///         frame and a menu bar is not.
    ///     </para>
    ///     <para>
    ///         ⚠ Before <see cref="Update" />'s early return rather than after it, because a
    ///         registration is the one change that leaves the document clean: nothing else about it
    ///         moved, so a pass that asked "is anything dirty" first would answer no and go home.
    ///     </para>
    /// </remarks>
    void Refont() {
        if (measuredFonts == Fonts.Revision) {
            return;
        }

        measuredFonts = Fonts.Revision;

        Remeasure(Root);
        Invalidate();
    }

    /// <summary>Dirties the layout node of every element in a subtree that measures its own text.</summary>
    /// <remarks>
    ///     ⚠ Only the ones with text, because <see cref="LayoutTree.MarkDirty" /> throws for a node
    ///     with no measure function — deliberately, and it is right to: nothing about a node laid out
    ///     purely from its style and its children can have changed here.
    /// </remarks>
    void Remeasure(UiElement element) {
        if (!string.IsNullOrEmpty(element.Text)) {
            Layout.MarkDirty(element.LayoutNode);
        }

        foreach (var child in element.Children) {
            Remeasure(child);
        }
    }

    /// <summary>Raised when every box in the document is final for this frame.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a control needs and could not have.</b> A scroll bar's range is its content's
    ///         height, a virtualiser's row count is its viewport's, and both are results of the layout
    ///         rather than inputs to it — so a control that computed them in a property setter was
    ///         computing them against the previous frame's boxes. <c>ScrollView.Refresh</c>,
    ///         <c>TreeView.Refresh</c> and the sample's own resize handler all existed to paper over
    ///         that, and all of them are a caller being asked to know when the framework had finished.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A handler may change the document, and doing so is normal rather than an abuse.</b>
    ///         A virtualiser that has just learned its viewport is taller realises more rows, which is
    ///         a structural change to the tree during a pass that has already run. So this re-enters:
    ///         after the handlers, a document that was dirtied runs the whole pass again, and it keeps
    ///         going until nothing more is asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Bounded, because the fixed point is not guaranteed to exist.</b> A handler that
    ///         adds a row whenever it is called, or two that undo each other, would spin for ever — and
    ///         "the interface hangs" is a worse failure than any interface it could produce. After
    ///         <see cref="SettlePasses" /> attempts the loop stops and <see cref="Settled" /> reports
    ///         false, which is a frame drawn one pass stale rather than a frame never drawn.
    ///     </para>
    /// </remarks>
    public event Action<UiDocument>? LayoutFinished;

    /// <summary>How many times a pass will re-run for handlers that changed something.</summary>
    /// <remarks>
    ///     Three, because the shapes that legitimately need more than one are two deep — a virtualiser
    ///     inside a scroll view, where realising rows changes the content size, which changes the
    ///     bar's range, which can change the viewport's width — and nothing sane is three.
    /// </remarks>
    public const int SettlePasses = 3;

    /// <summary>Whether the last <see cref="Update" /> reached a fixed point.</summary>
    /// <remarks>
    ///     False means a handler was still asking for changes when the budget ran out, and the frame
    ///     is one pass behind what it asked for. Exposed rather than logged because a control that
    ///     does this is a bug in that control, and a number nobody can read is a bug nobody finds.
    /// </remarks>
    public bool Settled { get; private set; } = true;

    /// <summary>How many extra passes the last <see cref="Update" /> ran for its handlers.</summary>
    public int SettlingPasses { get; private set; }

    void Settle() {
        SettlingPasses = 0;
        Settled = true;

        // ⚠ No early return for a document with no handler any more, and that is the container
        // queries' doing. A handler used to be the only thing that could dirty a document after its
        // boxes were final; `Recontain` is a second, it runs inside `Arrange` for every document
        // whose sheets declare a `@container`, and no application registers for it. Returning here
        // would enter the scopes, mark the document dirty and go home — showing every container
        // query's verdict one frame late, which for a dragged panel is a visible resize after the
        // drag. The cost of not returning is one null delegate check and one boolean per frame.
        for (var pass = 0; pass <= SettlePasses; pass++) {
            LayoutFinished?.Invoke(this);

            if (!dirty) {
                return;
            }

            if (pass == SettlePasses) {
                Settled = false;
                return;
            }

            dirty = false;
            SettlingPasses++;

            StylesResolved += Restyle();
            Arrange();
        }
    }

    /// <summary>Resolves every element's style and lays out every surface.</summary>
    /// <remarks>
    ///     ⚠ <b>One style walk and one layout call <i>per surface</i>, and the order matters.</b> The
    ///     style walk is a single descent from the root — a surface root is an ordinary element and
    ///     inherits from what is above it, which is what keeps one theme across every window — while
    ///     layout is one call per surface, because each is sized by its own window and snapped to its
    ///     own display's pixel grid.
    /// </remarks>
    void Arrange() {
        Apply(Root, Viewport.RootFontSize, ComputedText.Initial, Viewport);

        foreach (var surface in surfaces) {
            // ⚠ Written before each call rather than once, because two windows on two displays have
            // two grids. It is a field on the tree rather than an argument, so a surface laid out
            // with the previous surface's factor is a whole window rounded to the wrong grid — half
            // a pixel of seam on every border, which reads as a renderer bug.
            Layout.PointScaleFactor = surface.DpiScale;

            Layout.CalculateLayout(surface.Root.LayoutNode, surface.Width, surface.Height, Direction.Ltr);

            // ⚠ The surface's own metrics and not the primary's, for the same reason the grid factor
            // above is written per surface: `translate: 50vw` in a torn-off window is half of *that*
            // window.
            Accumulate(surface.Root, 0f, 0f, surface.Metrics);
        }

        // ⚠ Last, because it is the one thing here that needs a box rather than producing one — and
        // inside `Arrange` rather than beside its two callers, so that the settle loop's own pass
        // cannot forget it. A scope entered on the first pass and not re-entered on the second is a
        // container answering off a box that has since moved, which shows up only where one
        // container's query decides another one's size. See `Containers.cs`.
        Recontain();
    }

    /// <summary>Lets time pass, for the things that happen because nothing happened.</summary>
    /// <param name="now">The host's clock.</param>
    /// <remarks>
    ///     ⚠ <b>Time arrives from the host rather than from a clock read here</b>, which is the same
    ///     decision <c>GestureRecognizer</c> made and for the same reasons: a framework that calls
    ///     <c>DateTime.Now</c> cannot be tested without sleeping, cannot replay a recorded trace, and
    ///     behaves differently when a breakpoint holds the frame.
    ///
    ///     A long press, a tooltip's delay and a toast's dismissal are all things that must happen
    ///     when <i>no</i> input arrives, and nothing in an input stream can report the absence of
    ///     input. This is the one call a host must make every frame whether anything happened or not.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>And it is what drives every CSS transition and <c>@keyframes</c> animation in the
    ///     document, which is the second thing that makes it not optional.</b> A host that skips it
    ///     does not get instant changes — it gets <i>stuck</i> ones: a transition started at
    ///     <c>t = 0</c> against a clock that never leaves zero has made no progress on any frame, so
    ///     the property holds the value it was leaving for the life of the process. That is a worse
    ///     failure than no transitions at all and it looks like a different bug entirely, which is why
    ///     it is written down here rather than guarded against — the guard would be a clock read, and
    ///     the whole reason time arrives from outside is that this framework does not read clocks.
    /// </remarks>
    public void Tick(TimeSpan now) {
        ThrowIfDisposed();

        Now = now;
        seconds = (float) now.TotalSeconds;
        Gestures.Tick(now);

        // ⚠ Asked *before* the advance, because the advance is what makes the last frame of a fade
        // idle. Reading it after would skip the pass that writes the arrival value, leaving every
        // transition permanently one frame short of where it was going — the interruption logic hides
        // it for anything that moves again and nothing hides it for anything that does not.
        if (!Styles.Animations.IsIdle) {
            Styles.Animations.Advance(seconds);

            // ⚠ `InvalidatePositions` and not `Invalidate`, and the difference is a cold cascade per
            // frame for as long as anything is fading. Nothing an element *declared* has changed —
            // the animator is a tier laid over the finished cascade, not a participant in it — so
            // there is exactly nothing for the resolver to redo and a great deal for `Apply` to.
            InvalidatePositions();
        }

        // ⚠ Before `Ticked` and before the passes, so that a surface which greys a button in its
        // handler has that write picked up by *this* frame's layout rather than the next one. It is
        // also the reason the coalescing point is here at all: see `CommandsInvalidated`.
        RaiseCommandsInvalidated();
        RaiseAccessibilityInvalidated();

        Ticked?.Invoke(this, now);
    }

    /// <summary>The last time <see cref="Tick" /> was given.</summary>
    public TimeSpan Now { get; private set; }

    /// <summary>The same instant as <see cref="Now" />, in the seconds the animator counts in.</summary>
    /// <remarks>
    ///     <para>
    ///         A field written by <see cref="Tick" /> rather than a property over
    ///         <see cref="Now" />, because <c>Apply</c> reads it once per element and <c>TotalSeconds</c>
    ///         is a division. Small either way; the reason to write it down is that the two forms look
    ///         identical at the call site and only one of them is once per frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>float</c>, which is the animator's own currency and loses resolution at large
    ///         values — about a millisecond after three hours of uptime, and about a hundredth of a
    ///         second after a fortnight. Transitions are measured in hundreds of milliseconds, so the
    ///         first is invisible and the second is a stutter nobody will run long enough to see.
    ///         Recorded because "it is a float" is the whole of the reason and is not visible from
    ///         where it is used.
    ///     </para>
    /// </remarks>
    float seconds;

    /// <summary>Raised on every <see cref="Tick" />.</summary>
    /// <remarks>
    ///     A control subscribes in <c>OnCreated</c> and unsubscribes in <c>OnRemoved</c> — which is
    ///     the second thing that hook turned out to be for, and a reminder that it was the missing
    ///     half of a pair rather than a convenience.
    /// </remarks>
    public event Action<UiDocument, TimeSpan>? Ticked;

    /// <summary>Writes each element's resolved style through to the layout store.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A walk of the tree rather than of a list in creation order</b>, which is what
    ///         removal forced and what should have been here anyway. The list version was correct
    ///         only because elements were created parents-first and never removed, so its index order
    ///         happened to be its depth order — an invariant a removal would quietly have broken,
    ///         with children resolved against a parent's font size from the previous frame. The
    ///         property this actually needs is "parents before children", and a descent is that by
    ///         construction rather than by coincidence.
    ///     </para>
    ///     <para>
    ///         It also deletes two arrays. What each element had applied last time is now on the
    ///         element, where removing one takes its bookkeeping with it instead of leaving a hole
    ///         in three parallel lists.
    ///     </para>
    /// </remarks>
    /// <summary>The text properties that are inherited computed rather than as written.</summary>
    /// <param name="LineHeight">
    ///     The ancestor's resolved line height in pixels, or NaN when it was unitless or unset.
    /// </param>
    /// <param name="LineHeightFactor">
    ///     The multiple a unitless <c>line-height</c> named, or NaN. Kept apart from the pixels
    ///     because the difference is the whole point of the unitless form: <c>1.5</c> inherits as the
    ///     number and multiplies each descendant's own font size, where <c>1.5em</c> inherits as the
    ///     length the ancestor resolved once.
    /// </param>
    /// <param name="LetterSpacing">The ancestor's resolved letter spacing in pixels.</param>
    /// <param name="WordSpacing">
    ///     The ancestor's resolved word spacing in pixels. Last of the six because it is the newest,
    ///     and a record struct's positional order is a layout the whole file is written against.
    /// </param>
    /// <param name="TextIndent">
    ///     The ancestor's resolved <c>text-indent</c> in pixels.
    ///     <para>
    ///         ⚠ <b>Here rather than in <c>InheritedProperties</c>, and for
    ///         <see cref="LetterSpacing" />'s reason exactly.</b> The cascade inherits <i>specified</i>
    ///         values, so a <c>text-indent: 2em</c> on a panel would re-resolve against each
    ///         descendant's own font size and a heading inside it would be indented twice as far as
    ///         the author asked.
    ///     </para>
    /// </param>
    /// <param name="Features">
    ///     The OpenType features <c>font-feature-settings</c> and <c>font-variant-numeric</c> between
    ///     them asked for.
    ///     <para>
    ///         ⚠ <b>Resolved once per style pass rather than read in <c>UiElement.Block</c>, and that
    ///         is not the reason the other three are here.</b> Neither property takes a relative unit,
    ///         so specified-value inheritance through <c>InheritedProperties</c> would have been
    ///         correct — and would have made every <c>Block()</c> call parse a feature list, twice a
    ///         frame per element, to produce the same answer. The parse happens where the cascade
    ///         changes instead, and what travels is the finished set.
    ///     </para>
    /// </param>
    readonly record struct ComputedText(
        float LineHeight,
        float LineHeightFactor,
        float LetterSpacing,
        float TextIndent,
        FontFeatureSet Features,
        float WordSpacing
    ) {
        /// <summary>What the root starts with: the font's own line height, no tracking, no indent.</summary>
        public static ComputedText Initial => new(float.NaN, float.NaN, 0f, 0f, FontFeatureSet.None, 0f);
    }

    void Apply(UiElement element, float parentFontSize, ComputedText parentText, LengthContext metrics) {
        // ⚠ The surface's own lengths from here down. `50vw` inside a torn-off inspector means half
        // of that window, and resolving it against the main one would size a 400-pixel palette
        // against a 3840-pixel display. Everything else — the cascade, inheritance, the font size —
        // crosses the boundary unchanged, because a second window is a second *rectangle* and not a
        // second theme.
        if (element.SurfaceRoot is { } own) {
            metrics = own.Metrics;
        }

        // ⚠ <b>The transition tier, laid over the finished cascade and read by everything below.</b>
        // CSS Cascading 5 § 6.2 puts a transitioning value above every origin and above
        // `!important`, which is not a subtlety here but the only arrangement that works: a fade that
        // could be outvoted would stutter whenever anything else on the element changed. So it is
        // applied once, here, and the layout style, the draw list, the cursor and the hit test all
        // read the overlaid style without any of them knowing time is passing.
        //
        // Free when nothing is running — `Animator.Apply` returns the same instance — which is what
        // lets it sit in the hot walk of every element of every frame.
        var style = Styles.Animations.Apply(element.StyleNode, Restyler.StyleOf(element.StyleNode), seconds);

        element.Style = style;
        element.FontSize = Builder.ResolveFontSize(style, parentFontSize, metrics);

        // ⚠ After the font size and before the children, because both of these resolve against *this*
        // element's size and both are handed down in the form they came out as.
        var text = ResolveText(style, element.FontSize, parentText, metrics);

        element.LineHeight = float.IsNaN(text.LineHeightFactor)
            ? text.LineHeight
            : text.LineHeightFactor * element.FontSize;

        element.LetterSpacing = text.LetterSpacing;
        element.WordSpacing = text.WordSpacing;
        element.TextIndent = text.TextIndent;
        element.FontFeatures = text.Features;

        // ⚠ Read straight from the style rather than carried down through `ComputedText`, because
        // `direction` is an inherited *CSS* property and the cascade has already handed it to every
        // descendant — the same way `ScrollView` and `DrawListBuilder` read it. Threading it through
        // the computed-text struct as well would be a second copy of the inheritance that could
        // disagree with the first.
        element.ParagraphDirection = DirectionOf(style);

        // Resolved here rather than read in the draw list, because hit testing needs the same answer
        // and reaching it would mean parsing the same declaration twice per frame from two places
        // that could disagree. The setter invalidates the parent's paint order when it changes.
        element.ZIndex = ZIndexOf(style);

        // ⚠ Reference equality, which is the whole reason ComputedStyle is interned. Two elements
        // that resolved alike hold the same object, so this is one pointer comparison rather than a
        // walk of a property table — and a table of ten thousand identical cells rebuilds nothing.
        //
        // The font size has to be part of the test as well as the style: an element whose own
        // declarations did not change still needs rebuilding if an ancestor's font size did, because
        // every `em` on it measures against a different number now.
        //
        if (!ReferenceEquals(element.AppliedStyle, style) || !element.AppliedFontSize.Equals(element.FontSize)) {
            element.AppliedStyle = style;
            element.AppliedFontSize = element.FontSize;
            StylesApplied++;

            var layoutStyle = Builder.Build(style, metrics.WithFontSize(element.FontSize));

            Layout.SetStyle(element.LayoutNode, layoutStyle);

            // ⚠ The variable-length half of the same style, and it has to be a second call: a track
            // list lives in the tree's `TrackArena` behind a handle owned by the node, so `Build`
            // — which returns a value and never sees a node — has nowhere to put one. After
            // `SetStyle` rather than before, because `SetStyle` compares the whole struct to decide
            // whether the node changed and deliberately carries the four arena handles across that
            // write; going first would hand it a style that already had what it was about to
            // preserve. See `LayoutStyleBuilder.ApplyVariableLength`.
            Builder.ApplyVariableLength(style, Layout, element.LayoutNode);

            // ⚠ `order` is the one layout property the draw list also has to know, because CSS
            // Flexbox §5.4 makes it modify *painting* order as well as layout order. Taken from the
            // style that was just handed to the layout tree rather than resolved a second time, so
            // the two can never disagree about which frame's value they are using.
            element.FlexOrder = layoutStyle.Order;
        }

        // ⚠ Separately, because these change what the element *measures* rather than what its box is
        // — and the layout tree finds out about a changed measurement only by being told. They are
        // also inherited outside the cascade, so a label whose *parent* changed `line-height` has an
        // unchanged ComputedStyle: the reference test above passes, `SetStyle` is never reached, and
        // the label would keep measuring itself at the old height for the rest of its life.
        //
        // `.Equals` rather than `==`, because NaN is a legitimate value here and NaN == NaN is false.
        if (!element.AppliedLineHeight.Equals(element.LineHeight)
            || !element.AppliedLetterSpacing.Equals(element.LetterSpacing)
            || !element.AppliedTextIndent.Equals(element.TextIndent)
            || !ReferenceEquals(element.AppliedFontFeatures, element.FontFeatures)
            || element.AppliedParagraphDirection != element.ParagraphDirection) {
            element.AppliedLineHeight = element.LineHeight;
            element.AppliedLetterSpacing = element.LetterSpacing;
            element.AppliedTextIndent = element.TextIndent;
            element.AppliedFontFeatures = element.FontFeatures;
            element.AppliedParagraphDirection = element.ParagraphDirection;

            // Only a node that measures itself, which is what having text means — and what
            // `MarkDirty` insists on, on the grounds that nothing else about a node can change
            // without a style or a child changing and both of those already mark it. An element
            // with no text has no measurement for these to have changed, only descendants that do.
            if (!string.IsNullOrEmpty(element.Text)) {
                Layout.MarkDirty(element.LayoutNode);
            }
        }

        // ⚠ `ChildList` rather than `Children`, here and in `Accumulate`, and it is worth forty bytes
        // per element with children per frame. See the remarks on it.
        foreach (var child in element.ChildList) {
            Apply(child, element.FontSize, text, metrics);
        }
    }

    /// <summary>Computes the text properties that are inherited resolved rather than as written.</summary>
    /// <param name="style">The element's computed style.</param>
    /// <param name="fontSize">Its own font size, which every relative unit here measures against.</param>
    /// <param name="parent">What its parent came out with.</param>
    /// <param name="metrics">The surface's lengths, which the relative units resolve against.</param>
    /// <returns>What it comes out with, and what its children inherit.</returns>
    /// <remarks>
    ///     An element that declares nothing passes its parent's answer straight through, which is
    ///     what makes this inheritance rather than a default — and passes the <i>factor</i> through
    ///     as a factor, so a unitless <c>1.5</c> on a panel is one and a half times each descendant's
    ///     own size rather than one and a half times the panel's.
    /// </remarks>
    ComputedText ResolveText(ComputedStyle style, float fontSize, ComputedText parent, LengthContext metrics) {
        var lineHeight = parent.LineHeight;
        var factor = parent.LineHeightFactor;
        var tracking = parent.LetterSpacing;
        var indent = parent.TextIndent;
        var features = parent.Features;
        var words = parent.WordSpacing;

        if (style.TryGet(this.lineHeight, out var declared)) {
            var value = reader.Parse(declared);

            switch (value.Kind) {
                // Unitless, and the one that stays a number. `line-height: 1.5` is a ratio every
                // descendant applies to itself.
                case StyleValueKind.Number:
                    lineHeight = float.NaN;
                    factor = value.Number;
                    break;

                // ⚠ A percentage is *not* the unitless form. `150%` resolves against this element's
                // font size once and inherits as that length, which is precisely the trap the
                // unitless form exists to avoid. Handled apart from the other units because
                // `LengthContext` deliberately refuses to resolve a percentage — there it means the
                // containing block, which only layout knows. On `line-height` it means the font size,
                // and that is known right here.
                case StyleValueKind.Length when value.Unit == StyleUnit.Percent:
                    lineHeight = value.Number / 100f * fontSize;
                    factor = float.NaN;
                    break;

                // ⚠ <b>Resolved through `LengthContext.ToLength` rather than `PixelsPer`.</b> That
                // method answers zero for a unit that measures no distance, so `line-height: 200ms`
                // used to compute a line height of *nothing* — every line of the element and of
                // everything under it stacked on one baseline, with no diagnostic and no clamp. The
                // percentage case above has already taken the one unit `ToLength` refuses that this
                // property does accept, so what reaches here is a distance or a mistake.
                case StyleValueKind.Length
                    when metrics.WithFontSize(fontSize).ToLength(value) is { Unit: LayoutUnit.Point } resolved:
                    lineHeight = resolved.Value;
                    factor = float.NaN;
                    break;

                // ⚠ A length-shaped value in a unit that is not a distance, which is an invalid
                // declaration. Left inherited rather than zeroed or reset to `normal`: a browser
                // drops the declaration at parse time, and what an element with no `line-height` of
                // its own gets is its parent's — which is what `lineHeight` and `factor` still hold.
                case StyleValueKind.Length:
                    break;

                // `normal`, and anything else with no reading — the font's own recommendation.
                default:
                    lineHeight = float.NaN;
                    factor = float.NaN;
                    break;
            }
        }

        // ⚠ <b>Three outcomes and not two, which is what `PixelsPer` could not express here.</b> A
        // distance resolves; anything that is not length-shaped at all — `normal`, and every other
        // keyword, which is the reading this property already gave them — is zero tracking; and a
        // length-shaped value in a unit that measures no distance is an invalid declaration and is
        // left inherited. Read through `PixelsPer` the third collapsed into the second:
        // `letter-spacing: 2deg` was `normal` exactly, which is the initial value, so nothing about
        // the frame could tell the declaration had been thrown away. That is the worst shape this
        // bug takes anywhere in the file — the silent answer is not merely plausible, it is the
        // default.
        if (style.TryGet(letterSpacing, out var spacing)) {
            var value = reader.Parse(spacing);

            if (metrics.WithFontSize(fontSize).ToLength(value) is { Unit: LayoutUnit.Point } resolved) {
                tracking = resolved.Value;
            } else if (value.Kind != StyleValueKind.Length) {
                tracking = 0f;
            }
        }

        // ⚠ <b>The same three outcomes as `letter-spacing`, because it is the same kind of property
        // and the day it stopped being inert was the day that mattered.</b> `word-spacing` sat in
        // `InheritedProperties` — specified-value inheritance — for as long as nothing read it,
        // under a comment saying it could join the others when something wanted it. Nothing about
        // that arrangement announces itself: the value inherits, it resolves, and it is only wrong
        // by a factor of the font-size ratio, so `word-spacing: 0.5em` on a panel would have
        // compounded down every descendant and looked plausible at each step. The move out of that
        // list is not tidying beside this block; it is the half of this change that cannot be seen.
        if (style.TryGet(wordSpacing, out var between)) {
            var value = reader.Parse(between);

            if (metrics.WithFontSize(fontSize).ToLength(value) is { Unit: LayoutUnit.Point } resolved) {
                words = resolved.Value;
            } else if (value.Kind != StyleValueKind.Length) {
                words = 0f;
            }
        }

        // ⚠ <b>A percentage is refused rather than resolved, and it is the one value of this
        // property Vixen cannot answer.</b> CSS resolves a `text-indent` percentage against the
        // *containing block's* width, which is a layout result and is not known in the style pass —
        // and this pass is where the value has to be computed, because `em` on it has to measure
        // against the element that wrote it. `LayoutStyleBuilder.TryTextLength` made the same
        // refusal for the same reason before anything read the property. No utility can emit one:
        // `indent-*` is the spacing scale and `indent-px` is a pixel.
        //
        // ⚠ <b>A unit that is not a distance is a third case, and it is not the percentage's.</b> A
        // percentage is a value this engine understands and cannot resolve here, so it lands on the
        // initial value deliberately; `text-indent: 200ms` is not a value at all, and used to reach
        // the same zero through `PixelsPer` — the declaration thrown away and the element indented by
        // nothing, which is exactly what an element with no `text-indent` looks like. Left inherited
        // instead, which is what a dropped declaration means.
        if (style.TryGet(textIndent, out var declared_indent)) {
            var value = reader.Parse(declared_indent);

            if (metrics.WithFontSize(fontSize).ToLength(value) is { Unit: LayoutUnit.Point } resolved) {
                indent = resolved.Value;
            } else if (value.Kind != StyleValueKind.Length || value.Unit == StyleUnit.Percent) {
                indent = 0f;
            }
        }

        // ⚠ <b>Both are read off this element's own computed style rather than from the parent's
        // answer, and that is the difference between them and the three above.</b> Neither takes a
        // relative unit, so both are in `InheritedProperties` and the cascade has already handed
        // them down — which is what lets a child declare one of the two and keep the other. Building
        // the set from `parent.Features` instead would make `font-feature-settings` on a child erase
        // a `font-variant-numeric` it inherited, because there is one slot and two properties.
        //
        // ⚠ <b>The order the two are added in is CSS Fonts 4 § 6.4's rather than a choice.</b>
        // `font-variant-numeric` says what the text *is*; `font-feature-settings` is the low-level
        // escape hatch and says what the shaper is *told*, so it goes second and a hand-written
        // `"tnum" 0` can switch off what `tabular-nums` asked for — `FontFeatureSet.Of` keeps the
        // later of two entries for one tag.
        var hasNumeric = style.TryGet(fontVariantNumeric, out var numeric);
        var hasSettings = style.TryGet(fontFeatureSettings, out var settings);

        if (hasNumeric || hasSettings) {
            var wanted = new List<FontFeature>();

            if (hasNumeric) {
                NumericFeatures(Styles.Values.NameOf(numeric), wanted);
            }

            if (hasSettings) {
                SettingsFeatures(Styles.Values.NameOf(settings), wanted);
            }

            features = FontFeatureSet.Of(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wanted));
        } else {
            features = FontFeatureSet.None;
        }

        return new ComputedText(lineHeight, factor, tracking, indent, features, words);
    }

    /// <summary>The OpenType features CSS Fonts 4 § 6.6 gives each <c>font-variant-numeric</c> keyword.</summary>
    /// <remarks>
    ///     ⚠ <b>Every keyword of this property is one OpenType feature, which is why it and
    ///     <c>font-feature-settings</c> are one item and not two.</b> The property is a friendlier
    ///     spelling of the escape hatch, and once the shaper is being handed an array at all, both
    ///     are the same change. <c>normal</c>, and anything else with no reading, contributes
    ///     nothing — which is the property's initial value and the correct answer for a typo.
    /// </remarks>
    static void NumericFeatures(string keywords, List<FontFeature> into) {
        foreach (var range in keywords.AsSpan().Split(' ')) {
            var keyword = keywords.AsSpan()[range].Trim();

            var tag = keyword switch {
                "ordinal" => "ordn",
                "slashed-zero" => "zero",
                "lining-nums" => "lnum",
                "oldstyle-nums" => "onum",
                "proportional-nums" => "pnum",
                "tabular-nums" => "tnum",
                "diagonal-fractions" => "frac",
                "stacked-fractions" => "afrc",
                _ => null
            };

            if (tag is not null) {
                into.Add(new FontFeature(FontFeature.Pack(tag), 1u));
            }
        }
    }

    /// <summary>Reads a <c>font-feature-settings</c> list.</summary>
    /// <remarks>
    ///     ⚠ A malformed entry is dropped and the ones beside it are kept, rather than the whole
    ///     declaration being refused. CSS would throw the declaration away at parse time; ExCSS has
    ///     already accepted it by the time it reaches here, so the choice is between honouring what
    ///     parses and honouring nothing — and a stylesheet with one bad tag in a list of four should
    ///     not silently lose the other three.
    /// </remarks>
    static void SettingsFeatures(string settings, List<FontFeature> into) {
        foreach (var range in settings.AsSpan().Split(',')) {
            if (FontFeature.TryParse(settings.AsSpan()[range], out var feature)) {
                into.Add(feature);
            }
        }
    }

    /// <summary>Rebuilds the draw list from the current layout and styles.</summary>
    /// <returns>Whether the drawing differs from the previous frame's.</returns>
    /// <remarks>
    ///     Separate from <see cref="Update" /> because they answer different questions and a caller
    ///     may want one without the other — a hit test needs layout and no drawing, and a window
    ///     that was merely uncovered needs the drawing and no layout.
    /// </remarks>
    public bool Draw() {
        var changed = false;

        foreach (var surface in surfaces) {
            changed |= Draw(surface);
        }

        return changed;
    }

    /// <summary>Rebuilds one surface's draw list.</summary>
    /// <param name="surface">The surface.</param>
    /// <returns>Whether its drawing differs from the previous frame's.</returns>
    /// <remarks>
    ///     A host with two windows draws two frames and may well skip one — a minimised window is
    ///     worth laying out and not worth drawing — so the per-surface call is the one that exists
    ///     and <see cref="Draw()" /> is the loop over it.
    /// </remarks>
    public bool Draw(UiSurface surface) {
        // ⚠ Here and not in `Draw()`, which is the loop over this. One check per window per frame is
        // the same order as one per frame and it catches the caller that names its own surface.
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(surface);
        return drawings.Build(this, surface.Root, surface.Drawing);
    }

    /// <summary>The element a pointer would land on.</summary>
    /// <param name="x">Its x, in document space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The deepest element under the point, or <c>null</c> if none is.</returns>
    /// <remarks>
    ///     <para>
    ///         Front to back, which for children drawn in document order means <b>last child
    ///         first</b>. A later sibling is painted over an earlier one, so it is the one a click
    ///         lands on, and testing in document order would return whatever happens to be
    ///         underneath.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>pointer-events: none</c> makes an element transparent to the pointer <i>without
    ///         making its children so</i> — that asymmetry is what makes an overlay usable, and
    ///         treating the subtree as one unit would either block everything under a full-screen
    ///         layer or let clicks through a modal.
    ///     </para>
    ///     <para>
    ///         Doc 09 asks for a quadtree over the top level. This descends the tree instead, which
    ///         only enters subtrees that contain the point, so it is O(depth × siblings) rather than
    ///         O(elements). The quadtree is owed and should be measured against this before it is
    ///         written — the doc says "measured to be sufficient" about the simple version and that
    ///         measurement has not been taken.
    ///     </para>
    /// </remarks>
    public UiElement? HitTest(float x, float y) => HitTest(Primary, x, y);

    /// <summary>The element a pointer would land on in one surface.</summary>
    /// <param name="surface">The surface, which is to say the window.</param>
    /// <param name="x">Its x, in that surface's space.</param>
    /// <param name="y">Its y.</param>
    /// <returns>The deepest element under the point, or <c>null</c> if none is.</returns>
    public UiElement? HitTest(UiSurface surface, float x, float y) {
        ArgumentNullException.ThrowIfNull(surface);
        return HitTest(surface.Root, x, y);
    }

    /// <summary>Sends a pointer event to whatever is under it.</summary>
    /// <param name="args">The event, positioned in document space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    /// <remarks>
    ///     ⚠ A captured pointer goes to the capturing element wherever it is, which is the whole
    ///     point of capture: a drag that leaves the scrollbar it started on must keep reaching the
    ///     scrollbar. Hit testing during a drag is exactly the bug capture exists to prevent.
    /// </remarks>
    public UiElement? Dispatch(PointerEvent args) => Dispatch(Primary, args);

    /// <summary>Sends a pointer event to whatever is under it in one surface.</summary>
    /// <param name="surface">Which window it happened in.</param>
    /// <param name="args">The event, positioned in that surface's space.</param>
    /// <returns>The element it went to, or <c>null</c> if nothing was under it.</returns>
    /// <remarks>
    ///     ⚠ <b>The capture is the document's and not the surface's, which is what makes a drag
    ///     between windows work at all.</b> A tab dragged out of the main window keeps receiving
    ///     moves once the pointer is over the torn-off one, because the capturing element is asked
    ///     before anything is hit-tested — and hit testing is what could not answer, since the two
    ///     windows do not share a coordinate space.
    /// </remarks>
    public UiElement? Dispatch(UiSurface surface, PointerEvent args) {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(args);

        PointerSurface = surface;

        // Before the event rather than after it. `:hover` and `:active` are what a handler reads to
        // find out what it is being asked about — a menu deciding whether the release it just got
        // belongs to the item under the cursor asks the item — and state brought up to date
        // afterwards would answer every handler with the previous frame's arrangement.
        Track(surface, args);

        var captured = Captured;
        var target = captured ?? HitTest(surface, args.X, args.Y);

        // ⚠ Read before the route rather than after it. What decides whether a press clicked *away*
        // from the focus is where the focus was when the press landed, and by the time the route has
        // finished a control may have moved it.
        var focused = Focused;

        target?.Raise(args);

        // After the route, so that a control which focuses itself on the press has already done so
        // and this can tell that it did; and only when nothing was already captured, because a
        // pointer in the middle of a gesture is not a click on whatever it is passing over.
        if (args.Action == PointerAction.Pressed && captured is null) {
            Defocus(target, focused);
        }

        // After the raw event rather than instead of it. A gesture is a reading of the pointer
        // stream, not a replacement for it, and a control that wants presses and a control that
        // wants taps are both entitled to what they asked for.
        Gestures.Process(args, target);
        return target;
    }

    /// <summary>Taps, long presses and drags read out of the pointer stream.</summary>
    /// <remarks>
    ///     Exposed rather than hidden behind the document because it needs telling what time it is —
    ///     see <see cref="GestureRecognizer.Tick" /> — and because its thresholds are an
    ///     application's decision.
    /// </remarks>
    public GestureRecognizer Gestures { get; } = new();

    /// <summary>Which surface the last pointer event was dispatched into.</summary>
    /// <remarks>
    ///     ⚠ <b>Which window the pointer's coordinates are <i>in</i>, which is not the same as which
    ///     window it is over.</b> While an element has the pointer captured every platform keeps
    ///     reporting positions relative to the window the press happened in, even once the cursor has
    ///     left it — so a drag out of the main window arrives as coordinates past its right edge
    ///     rather than as coordinates in the window underneath. Anything that has to place a drag in
    ///     the world, rather than in a tree, needs this to know what space it has been handed.
    /// </remarks>
    public UiSurface? PointerSurface { get; private set; }

    /// <summary>The element currently receiving every pointer event, if any.</summary>
    public UiElement? Captured { get; private set; }

    /// <summary>Sends every pointer event to one element until it is released.</summary>
    /// <param name="element">The element.</param>
    public void CapturePointer(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);
        Captured = element;
    }

    /// <summary>Stops sending every pointer event to one element.</summary>
    public void ReleasePointer() => Captured = null;

    /// <summary>The faces a <c>font-family</c> declaration can name.</summary>
    public FontRegistry Fonts { get; } = new();

    /// <summary>The shaping every element's text goes through.</summary>
    /// <remarks>
    ///     Shared across the document because it is keyed on the font and the string and not on the
    ///     element — ten thousand list rows saying the same word shape once between them, and the
    ///     measure pass and the draw pass shape once between them too.
    /// </remarks>
    public ShapingCache Shaping { get; } = new();

    internal bool PointerEventsNone(ComputedStyle style) =>
        style.TryGet(pointerEvents, out var value) && value == none;

    /// <summary>Whether <c>visibility</c> takes this element out of the pointer's reach.</summary>
    /// <remarks>
    ///     ⚠ <b>CSS UI §5.2 makes an invisible box untargetable, and this used to be the half of
    ///     <c>visibility</c> that was missing.</b> The paint walk has always honoured the property;
    ///     hit testing did not, so a hidden element went on swallowing the clicks meant for whatever
    ///     was behind it. <c>AdvancedTheme.vcss</c> has three of them, and the one that shows what
    ///     the gap cost is <c>code-metrics</c> — an absolutely positioned measurement probe pinned at
    ///     the origin, invisible, and until now the first thing a click in the top-left corner of a
    ///     code editor ever reached.
    ///     <para>
    ///         Read per element rather than by walking ancestors, exactly as <c>DrawListBuilder</c>
    ///         reads it: the property inherits, so a descendant of a hidden box has already been
    ///         given the value, and one that declares <c>visible</c> is targetable again. Checking an
    ///         ancestor chain here would break that second case and disagree with what is painted.
    ///     </para>
    /// </remarks>
    internal bool Invisible(ComputedStyle style) =>
        style.TryGet(visibility, out var value) && (value == visibilityHidden || value == visibilityCollapse);

    /// <summary>The base bidi level an element's text is laid out at, from its <c>direction</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>direction</c> states the paragraph level, and stating it is the whole point of
    ///         the property.</b> UAX#9's P2/P3 guess a level from the first strong character, which is
    ///         right for a string with no styling around it and wrong for a paragraph an author has
    ///         declared the direction of: an Arabic sentence that happens to begin with a Latin
    ///         product name is still an Arabic sentence, and the guess reads it left to right.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Anything that is not <c>rtl</c> is <see cref="ParagraphDirection.Auto" /> rather
    ///         than <see cref="ParagraphDirection.LeftToRight" />.</b> CSS's initial value is
    ///         <c>ltr</c>, so reading it as such would pin every unstyled label in the engine to level
    ///         0 — and a label showing a user's name, a file path or a chat message has no styling and
    ///         is exactly where the first-strong guess is the right answer. So a stated <c>rtl</c>
    ///         overrides the guess, a stated <c>ltr</c> overrides it the other way, and the absence of
    ///         the property leaves it alone. The cost is that <c>direction: ltr</c> has to be written
    ///         to *force* level 0 on Arabic text; the alternative is an engine in which no unstyled
    ///         text can ever lay out right to left, which is the defect this fixes turned inside out.
    ///     </para>
    /// </remarks>
    internal ParagraphDirection DirectionOf(ComputedStyle style) {
        if (!style.TryGet(direction, out var value)) {
            return ParagraphDirection.Auto;
        }

        return value == directionRtl ? ParagraphDirection.RightToLeft : ParagraphDirection.LeftToRight;
    }

    /// <summary>An element's <c>z-index</c>, which is zero when it has none.</summary>
    /// <remarks>
    ///     <c>auto</c> is a keyword rather than a number and so reads as zero, which is right here:
    ///     what <c>auto</c> means in CSS is "take the stacking context's own level", and sibling
    ///     ordering has no stacking context to take a level from.
    /// </remarks>
    int ZIndexOf(ComputedStyle style) =>
        style.TryGet(zIndex, out var id) && reader.Parse(id) is { Kind: StyleValueKind.Number } value
            ? (int) value.Number
            : 0;

    /// <summary>Whether an element's text may be broken across lines at all.</summary>
    /// <remarks>
    ///     ⚠ <b><c>pre</c> is treated as wrapping, and that is a stated gap rather than a reading of
    ///     the specification.</b> <c>white-space</c> conflates three questions — whether to collapse
    ///     runs of space, whether to keep newlines, and whether to wrap — and only the third is
    ///     answered here. <c>nowrap</c> and <c>pre</c> agree about wrapping and disagree about the
    ///     other two, so honouring <c>pre</c> for wrapping alone would be honouring a third of it.
    /// </remarks>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>text-wrap</c> is read beside it, and it is the property CSS Text 4 moved this
    ///         question to.</b> § 4 redefines <c>white-space</c> as a shorthand for
    ///         <c>white-space-collapse</c> and <c>text-wrap-mode</c>, and <c>text-wrap</c> is the one
    ///         that decides wrapping — which is precisely the third of <c>white-space</c> this method
    ///         was already answering and the other two thirds it was refusing to. So the two are read
    ///         together rather than one shadowing the other.
    ///     </para>
    ///     <para>
    ///         <b>Either saying <c>nowrap</c> stops the wrapping, and that is a choice rather than the
    ///         specification.</b> A cascade that expanded the shorthand could let the later
    ///         declaration win; this one inherits <i>specified</i> values and does not expand
    ///         <c>white-space</c>, so there is no order to appeal to and an <c>or</c> is the only
    ///         reading that does not silently drop one of the two. What it costs is that
    ///         <c>text-wrap: wrap</c> cannot re-enable wrapping under a <c>white-space: nowrap</c> on
    ///         the same element — it can under an inherited <c>text-wrap: nowrap</c>, which is the
    ///         case anybody writes, and <c>whitespace-normal</c> is the opt-out for the other.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>balance</c> and <c>pretty</c> are values of this property that fall through to
    ///         "wraps", which is the honest answer rather than a gap: both ask for a *better* set of
    ///         line breaks, <c>LineWrapper</c> is greedy first-fit on purpose, and neither utility is
    ///         registered — so no class can put either value here. See docs/plan/43's Typography
    ///         section for what a balancing pass would cost.
    ///     </para>
    /// </remarks>
    internal bool WrapsOf(ComputedStyle style) =>
        (!style.TryGet(whiteSpace, out var collapsing) || collapsing != nowrap)
        && (!style.TryGet(textWrap, out var wrapping) || wrapping != nowrap);

    /// <summary>Whether a line too wide for its box ends in an ellipsis rather than being cut.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This property inherits here and does not in CSS, and the deviation is deliberate
    ///         and load-bearing rather than an oversight.</b> CSS Overflow 3 § 5.1 applies
    ///         <c>text-overflow</c> to a <i>block container</i>, where it ellipsises the inline
    ///         content of the line boxes that container establishes — so
    ///         <c>div { text-overflow: ellipsis } &gt; span</c> truncates the span's text without the
    ///         property ever inheriting, because the span's glyphs are <i>on the div's own line
    ///         box</i>.
    ///     </para>
    ///     <para>
    ///         Vixen has no shared line box to put them on. Every element measures and draws its own
    ///         text independently — see <c>UiElement.Block</c> — and
    ///         <c>Core/Vixen.Ui.Layout.Tests/InlineKnownGaps.txt</c> records why: one node produces
    ///         one box, and a line box spanning several elements is the fragmentation work that
    ///         invariant forbids. With no shared line, the only way a container's declaration can
    ///         reach the glyphs CSS says it governs is to inherit to the element that owns them.
    ///     </para>
    ///     <para>
    ///         What that buys and what it costs: <c>class="truncate"</c> on a row whose text sits in
    ///         a child span does what its author plainly meant, which is the overwhelmingly common
    ///         shape and the one <c>TaskCenter.vxml</c> writes. What it over-applies is a
    ///         <i>nested block container's</i> text, which CSS would leave alone. That case needs a
    ///         real block-container model to distinguish, it is rare, and an ellipsis on clipped text
    ///         is a far smaller wrong than silently drawing nothing — which is what this property did
    ///         before. Written down rather than discovered.
    ///     </para>
    /// </remarks>
    internal bool EllipsisOf(ComputedStyle style) =>
        style.TryGet(textOverflow, out var value) && value == ellipsis;

    /// <summary>How many lines the block may have before the rest are dropped, or zero for all.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>-webkit-line-clamp</c>, which is the only spelling: CSS Overflow 4's unprefixed
    ///         <c>line-clamp</c> is not shipped anywhere and Tailwind emits the prefixed name.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read in <see cref="UiElement.Block(float)" /> and not at paint, which is the one
    ///         place this differs from <see cref="EllipsisOf" />.</b> An ellipsis changes the picture
    ///         and nothing else — the element is as wide as it always was, which is what makes its
    ///         parent shrink it in the first place. A clamp changes <i>how many lines there are</i>,
    ///         so it changes the element's height, so it has to happen before the height is reported.
    ///         The two are otherwise the same machinery and the marker on the last kept line is
    ///         literally <see cref="EllipsisOf" />'s.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Vixen drops the lines rather than clipping them, and that is why the utility
    ///         emits no <c>overflow</c>.</b> A browser lays out every line and hides the ones past
    ///         the clamp, so it needs <c>overflow: hidden</c> to do the hiding; here the block
    ///         genuinely has N lines and there is nothing left to clip. Emitting the declaration
    ///         anyway would be a class asking for a mechanism this engine does not use.
    ///     </para>
    /// </remarks>
    internal int LineClampOf(ComputedStyle style) {
        if (!style.TryGet(lineClamp, out var id)) {
            return 0;
        }

        var value = reader.Parse(id);

        // `none` — and anything else that is not a positive count — is no clamp. Zero is the same
        // answer as absent rather than "no lines at all", which is what a browser does with it.
        return value.Kind == StyleValueKind.Number ? Math.Max((int) value.Number, 0) : 0;
    }

    /// <summary>What to do with a word wider than the line it has to fit in.</summary>
    internal TextWrapMode WrapModeOf(ComputedStyle style) =>
        style.TryGet(overflowWrap, out var value) && (value == anywhere || value == breakWord)
            ? TextWrapMode.Anywhere
            : TextWrapMode.Word;

    /// <summary>Whether a line may end inside a word. CSS Text 3 § 5.2's <c>word-break</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second reader beside <see cref="WrapModeOf" /> rather than more values on it,
    ///         and the two are not the same question however alike the class names look.</b>
    ///         <c>overflow-wrap</c> is consulted only in the branch where <i>nothing</i> fits, so it
    ///         cannot move a break that had somewhere else to go; <c>word-break: break-all</c> changes
    ///         which breaks exist, so a word that would have fitted on the next line is split at the
    ///         end of this one. Merging them into one enum would have forced a winner for
    ///         <c>keep-all</c> with <c>anywhere</c> — no break between two Han characters, but still a
    ///         squeeze when one run is wider than the column — which is a combination CSS defines and
    ///         a narrow CJK column actually wants.
    ///     </para>
    ///     <para>
    ///         <c>normal</c> is the initial value and needs no test: anything that is not one of the
    ///         two keywords is it.
    ///     </para>
    /// </remarks>
    internal WordBreakMode WordBreakOf(ComputedStyle style) {
        if (!style.TryGet(wordBreak, out var value)) {
            return WordBreakMode.Normal;
        }

        return value == breakAll ? WordBreakMode.BreakAll :
            value == keepAll ? WordBreakMode.KeepAll : WordBreakMode.Normal;
    }

    /// <summary>What to do to the characters before they are shaped. CSS Text 3 § 2.1.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Read here and applied in <c>UiElement.Block</c>, which is <i>before</i> the
    ///         shaping and the wrapping and the measuring</b> — a case mapping changes how wide the
    ///         text is, so a transform applied at paint would draw a paragraph the layout had
    ///         measured at the other width.
    ///     </para>
    ///     <para>
    ///         <c>none</c> is the initial value and needs no test: anything that is not one of the
    ///         three keywords is it. <c>full-width</c> and <c>full-size-kana</c> are not registered
    ///         and therefore cannot arrive here.
    ///     </para>
    /// </remarks>
    internal TextTransform TextTransformOf(ComputedStyle style) {
        if (!style.TryGet(textTransform, out var value)) {
            return TextTransform.None;
        }

        return value == uppercase ? TextTransform.Uppercase :
            value == lowercase ? TextTransform.Lowercase :
            value == capitalize ? TextTransform.Capitalize : TextTransform.None;
    }

    /// <summary>How many spaces wide a tab stop is. CSS Text 3 § 6.1's <c>tab-size</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A count of spaces and not a distance, which is why this returns a number and the
    ///         pixels are worked out where a font is in scope.</b> CSS defines the <c>&lt;number&gt;</c>
    ///         form as that many advances of the element's own space character — so the same
    ///         declaration is a different width in a different face, and resolving it here would
    ///         resolve it against nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <c>&lt;length&gt;</c> form is refused rather than resolved, and it is a value
    ///         gap rather than an oversight.</b> A length on this property takes relative units, so it
    ///         would have to be computed and inherited beside <c>line-height</c> instead of living in
    ///         <c>InheritedProperties</c> — a second computed text property, a field on
    ///         <c>ComputedText</c> and a second reading of the same property, for a form no utility
    ///         emits: Tailwind's <c>tab-*</c> is a bare count. An element that writes one keeps the
    ///         initial eight, which is what a browser does with a declaration it drops.
    ///     </para>
    ///     <para>
    ///         The initial value is <b>8</b>, and it applies to text nobody styled — so a tab in a
    ///         label is eight spaces wide by default rather than the .notdef box it used to draw.
    ///     </para>
    /// </remarks>
    internal float TabSizeOf(ComputedStyle style) {
        if (!style.TryGet(tabSize, out var id)) {
            return DefaultTabSize;
        }

        var value = reader.Parse(id);

        // Zero is a real value — CSS says a tab is then no wider than nothing — and negative is not,
        // so it clamps rather than falling back to the initial value.
        return value.Kind == StyleValueKind.Number ? MathF.Max(value.Number, 0f) : DefaultTabSize;
    }

    /// <summary>CSS Text 3 § 6.1's initial <c>tab-size</c>.</summary>
    internal const float DefaultTabSize = 8f;

    internal string? FontFamilyOf(ComputedStyle style) =>
        style.TryGet(fontFamily, out var value) ? Styles.Values.NameOf(value) : null;

    /// <summary>An element's <c>font-weight</c> on CSS's 1–1000 scale.</summary>
    /// <remarks>
    ///     ⚠ <c>lighter</c> and <c>bolder</c> are <b>not</b> read, and fall through to regular. They
    ///     are relative to the <i>parent's computed</i> weight, which this cascade does not have —
    ///     it inherits specified values, so the parent's declaration might itself be <c>bolder</c>
    ///     and the chain has no bottom. Owed with the computed-value stage, alongside
    ///     <c>line-height</c>, and left out rather than approximated as "one step from 400", which
    ///     would be right only for an element whose parent said nothing.
    /// </remarks>
    internal int FontWeightOf(ComputedStyle style) {
        if (!style.TryGet(fontWeight, out var id)) {
            return FontRegistry.RegularWeight;
        }

        var value = reader.Parse(id);

        if (value.Kind == StyleValueKind.Number) {
            return Math.Clamp((int) value.Number, 1, 1000);
        }

        return id == bold ? FontRegistry.BoldWeight : FontRegistry.RegularWeight;
    }

    /// <summary>An element's <c>font-style</c>.</summary>
    internal FontStyle FontStyleOf(ComputedStyle style) {
        if (!style.TryGet(fontStyle, out var id)) {
            return FontStyle.Normal;
        }

        return id == italic ? FontStyle.Italic : id == oblique ? FontStyle.Oblique : FontStyle.Normal;
    }

    UiElement? HitTest(UiElement element, float x, float y) {
        // ⚠ <b>The pointer is moved into the element's own space before anything else looks at it,
        // and that one line is the whole of hit testing a transform.</b> A `rotate` or a `scale`
        // paints this element and its subtree through a matrix; the untransformed geometry is still
        // exactly where `Accumulate` left it, so mapping the point back through the inverse puts it
        // in the space every rectangle below this line is already expressed in. `Contains`, `Cut` and
        // the whole recursion are untouched — they go on comparing absolute rectangles, because after
        // this line the point is absolute too.
        //
        // ⚠ <b>Nested transforms compose because the recursion does.</b> A rotated child of a scaled
        // parent has its point mapped by the parent's inverse on the way in and by its own below
        // that, which is the inverse of the composition the geometry builder paints with — the outer
        // group's surface holds the inner group's already-transformed composite quad. Neither side
        // knows about the other; they agree because both are the same matrix applied at the same
        // point in the same walk.
        //
        // ⚠ <b>A degenerate transform swallows the subtree, and that is the correct answer rather
        // than a guard.</b> `scale: 0` paints zero pixels, so no point on the screen is a click on it;
        // `UiTransform.Invert` returns null and the walk stops here. Falling through to the
        // untransformed box instead would leave a control that cannot be seen still taking the
        // pointer, which is the invisible-hit-target bug from the other direction.
        if (element.Transform is { } placed) {
            if (placed.Invert() is not { } undo) {
                return null;
            }

            var local = undo.Apply(new Vector2(x, y));
            x = local.X;
            y = local.Y;
        }

        var inside = Contains(element, x, y);

        // ⚠ Being outside an element is not a reason to skip its children. `overflow: visible` is
        // CSS's default and means exactly that a child may hang outside its parent and still be
        // drawn — so it must still be clickable. Returning early on `!inside` would make every
        // overflowing element, every dropdown and every tooltip unhittable, and the bug would look
        // like the click landing on whatever is behind them.
        //
        // ⚠ And it is outside *on a clipped axis* rather than outside at all, which are different
        // questions the moment one axis clips and the other does not. A point beside an
        // `overflow-y: hidden` panel is drawn — the clip rectangle's left and right edges are past
        // any viewport — so it has to be clickable too, or the panel is a control you can see and
        // cannot press. The same reading the draw list uses, out of the same object.
        //
        // The `!inside` stays in front of it as the fast path it always was: a point within the box
        // is within it on both axes, so no clip of any shape can cut it, and a pointer move that asks
        // this of every element in the tree should not read three properties to be told so.
        if (!inside && Cut(element, x, y)) {
            return null;
        }

        // Backwards through the *paint* order, so the element on top is the one a click lands on. In
        // document order these are the same walk; with a `z-index` in play they are not, and a hit
        // test that kept its own opinion would send the click to whatever the lifted child covers.
        var order = element.PaintOrder;

        for (var i = order.Count - 1; i >= 0; i--) {
            // Another surface is another window, and its coordinates are not these. A point in this
            // one can never be in that one, whatever the two rectangles happen to overlap.
            if (order[i].SurfaceRoot is not null) {
                continue;
            }

            if (HitTest(order[i], x, y) is { } hit) {
                return hit;
            }
        }

        return inside && element.IsHitTestVisible ? element : null;
    }

    /// <summary>Whether an element's clip cuts a point away from its subtree.</summary>
    /// <remarks>
    ///     The clip is asked about on the <i>parent</i>, because it is the parent that clips and the
    ///     child has no idea it is being cut. Per axis, so a point beside a vertically clipped panel is
    ///     still inside the part of the plane that panel draws in.
    /// </remarks>
    bool Cut(UiElement element, float x, float y) {
        var axes = overflow.Of(element.Style);

        if (!axes.Any) {
            return false;
        }

        if (axes.Horizontal && (x < element.AbsoluteLeft || x >= element.AbsoluteLeft + element.Width)) {
            return true;
        }

        return axes.Vertical && (y < element.AbsoluteTop || y >= element.AbsoluteTop + element.Height);
    }

    static bool Contains(UiElement element, float x, float y) =>
        x >= element.AbsoluteLeft
        && y >= element.AbsoluteTop
        && x < element.AbsoluteLeft + element.Width
        && y < element.AbsoluteTop + element.Height;

    /// <summary>Turns the parent-relative layout results into document-space rectangles.</summary>
    /// <remarks>
    ///     Accumulated once per pass rather than walked per query. Hit testing asks for absolute
    ///     bounds several times per pointer move, and the draw list will ask for every element's
    ///     every frame; a walk to the root per read is the same arithmetic done depth times over.
    /// </remarks>
    void Accumulate(UiElement element, float x, float y, LengthContext metrics) {
        // ⚠ The offset lands here and nowhere else, which is what makes it free. Every consumer of a
        // position — hit testing, the draw list, arrow navigation — reads the accumulated value, so
        // a shifted element is drawn, clicked and navigated to in its shifted place without any of
        // them being told that shifting is a thing that can happen.
        //
        // ⚠ <b>And `translate` lands in the same sum, which is the whole of doc 43 § A7's first
        // third.</b> It is the answer to "where does a transform live": not in `UiShape`, not in a
        // matrix threaded through the draw list, but in the one place a position is already assembled
        // from more than one contribution. Both consumers of a position read the *result*, so a
        // translated element cannot draw in the new place and be clickable in the old one — the
        // classic failure of a transform bolted onto a renderer, and the shape of the bug the
        // per-axis clip work had to fix. It is not prevented here so much as unstateable: there is no
        // second copy of the arithmetic to get wrong. `TransformTests` sabotages exactly this, and the
        // sabotage has to reach into the hit test on purpose to make the two disagree.
        //
        // ⚠ <b>Beside `OffsetX` rather than into it.</b> The two mean different things and have
        // different owners: `OffsetX` is imperative, is what `ScrollView` and `DockingHost` slide
        // their content with, and survives a restyle; a translation is declarative and is whatever the
        // cascade last computed. Folding the second into the first would make a stylesheet silently
        // erase a scroll position, which reads as the panel jumping home on an unrelated theme change.
        translation.Of(element, metrics, out var dx, out var dy);

        element.AbsoluteLeft = x + element.Left + element.OffsetX + dx;
        element.AbsoluteTop = y + element.Top + element.OffsetY + dy;

        // ⚠ <b>`rotate` and `scale` land here too, and pointedly <i>not</i> in the sum above.</b> A
        // translation is a position and can be added to one; a rotation and a scale change the box's
        // shape, so there is no pair of numbers that could carry them and no accumulated rectangle
        // that could describe the result. What the paragraphs above buy for `translate` — one value,
        // two consumers, nothing to get out of step — is bought here a different way: one *matrix*,
        // composed once with its origin already folded in, which the geometry builder applies to a
        // composited group's four composite vertices and the hit test applies inverted to the
        // pointer. Two applications of one matrix rather than two readings of one property.
        //
        // ⚠ <b>Read after the position is assembled, because the origin needs it.</b>
        // `transform-origin` defaults to the border box's centre and the matrix is absolute, so
        // `TransformReader.Of` reads `AbsoluteLeft`/`AbsoluteTop` — which are one line old at this
        // point and would be a frame stale one line up.
        //
        // ⚠ <b>And deliberately not accumulated into the children.</b> The recursion below passes the
        // *untransformed* position on, so a rotated panel's children lay out and hit-test where they
        // always did and are carried along by the parent's group instead. That is what makes nested
        // transforms compose for nothing — the inner group's composite quad is transformed by the
        // inner matrix and then rasterised into the outer group's surface, which the outer matrix
        // transforms in turn — and it is what stops a transform leaking into layout.
        element.Transform = transform.Of(element, metrics);

        // ⚠ The children accumulate from the translated parent, so a transform moves the subtree —
        // CSS Transforms 1 §3, and the reason a translated panel takes its contents with it rather
        // than sliding out from under them. Nothing extra is needed for it: the recursion below
        // already passes the parent's accumulated position, and the translation is now part of that.
        foreach (var child in element.ChildList) {
            // ⚠ Another surface's coordinates are its own window's, starting at its top-left, and
            // walking into one from here would offset a torn-off window by wherever its root
            // happened to sit in the main one. `Arrange` accumulates each surface from zero.
            if (child.SurfaceRoot is not null) {
                continue;
            }

            Accumulate(child, element.AbsoluteLeft, element.AbsoluteTop, metrics);
        }
    }

    /// <summary>Whether <see cref="Dispose" /> has run.</summary>
    /// <remarks>See <see cref="ThrowIfDisposed" /> for why it is worth a field.</remarks>
    bool disposed;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Idempotent, and the second call was not free before.</b>
    ///     <c>LayoutTree.Dispose</c> used to free four <c>NativeArray</c>s and leave the struct
    ///     fields holding the freed pointers, so disposing a document twice handed the same
    ///     addresses to <c>NativeMemory.AlignedFree</c> twice and the allocator aborted the process.
    ///     It clears the fields now and is idempotent in its own right, so this field is no longer
    ///     the only thing standing between a host and a <c>SIGABRT</c> — it is kept because a
    ///     document inside a <c>using</c> that is also disposed by the host it was handed to is an
    ///     ordinary arrangement, and because <see cref="disposed" /> is what the guard below reads.
    /// </remarks>
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;

        // Before the layout, because this one is about what stays reachable rather than about
        // native memory, and a throw from the store below should not be what decides whether the
        // graph was let go.
        ReleaseCommandResponders();
        ReleaseAccessibilitySubscribers();

        Layout.Dispose();
    }

    /// <summary>Refuses a call on a document whose stores have been released.</summary>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>It was written because the alternative was not an exception but the process going
    ///         away, a minute later, with nothing said.</b> <see cref="Layout" /> is a
    ///         <c>LayoutTree</c>, and a <c>LayoutTree</c> is four <c>NativeArray</c>s. Disposing it
    ///         freed them and set its capacity to nought but left the struct fields pointing at the
    ///         freed blocks — so the next <c>CreateNode</c> grew from a capacity of nought, found the
    ///         arrays non-empty, copied out of memory that was no longer ours and freed it a second
    ///         time. The allocator aborted: no managed exception, no message, no stack, and then
    ///         <c>xunit.runner.visualstudio</c>'s 60-second
    ///         <c>TestProjectConfiguration.CrashDetectionSinkTimeout</c> before the adapter even said
    ///         so, which reads exactly like a deadlock.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That is history now, and the guard is worth keeping anyway.</b>
    ///         <c>LayoutTree.Dispose</c> clears its four fields, so a disposed store grows a fresh
    ///         set and no caller of it can reach the abort any more — this guard is no longer load
    ///         bearing against <c>SIGABRT</c>. What it still buys is the difference between an
    ///         <c>ObjectDisposedException</c> that names the document and a call that quietly
    ///         succeeds against an empty store: an <c>Update</c> on a released document laying out
    ///         nothing, returning cleanly, and leaving the panel that asked for it blank. Silence
    ///         with a plausible result is the harder of the two to find.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>At the entry points and nowhere below them.</b> A pass walks every element in the
    ///         document several times over; a check inside one of those walks would be a branch per
    ///         element per frame to catch a mistake that can only be made once, at the top. So it
    ///         guards what an outside caller can reach — the loads, the passes, the tick, the surface
    ///         calls and the four tree mutations — and the inner loops are untouched.
    ///     </para>
    ///     <para>
    ///         It is only ever going to matter more. Hot reload disposes a document and builds
    ///         another, <c>HotReloadHost</c> rolls back to the previous one when a build fails, and
    ///         panels are moving to <c>.vxml</c>, where a document's lifetime is managed by something
    ///         other than the code that calls into it.
    ///     </para>
    /// </remarks>
    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
