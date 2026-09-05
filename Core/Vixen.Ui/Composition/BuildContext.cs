// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vixen.Ui.Reactive;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Composition;

/// <summary>How an <c>on:</c> binding attaches itself, for the table that decides what a name means.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A pair rather than a <see cref="RoutingStrategy" />, and the second half is why.</b>
///         Everything else an <c>on:</c> modifier says — <c>stop</c>, <c>once</c>, <c>self</c> — is a
///         filter <see cref="BuildContext.On{TEvent}" /> can apply around a handler it already owns.
///         Whether the handler is called <i>at all</i> once something downstream has marked the
///         event handled is <see cref="UiElement.AddHandler{T}" />'s third argument, which only the
///         subscription itself can pass. So the entry has to be told, and a table whose entries took
///         a bare strategy could never spell <c>on:pointerdown.handled</c>.
///     </para>
///     <para>
///         <b>Use <see cref="Listen{T}" /> rather than calling <c>AddHandler</c>.</b> An entry that
///         passed the strategy and forgot the flag would compile, work for every binding without the
///         modifier, and silently ignore it for the ones that asked — which is the failure mode this
///         whole seam exists to avoid. One call carries both.
///     </para>
/// </remarks>
public readonly record struct EventSubscription {
    /// <summary>Which leg of the route to listen on.</summary>
    public RoutingStrategy Strategy { get; init; }

    /// <summary>Whether to run even after something has marked the event handled.</summary>
    public bool HandledEventsToo { get; init; }

    /// <summary>How to undo whatever <see cref="Listen{T}" /> did, collected as it does it.</summary>
    /// <remarks>
    ///     ⚠ <b>A list rather than one action, because an entry may subscribe more than once.</b>
    ///     <c>click</c> listens for a <c>ClickEvent</c> <i>and</i> a <see cref="TapEvent" />, so a
    ///     single undo would leave one of them attached — which is worse than none, being the half
    ///     that still fires.
    ///
    ///     ⚠ And a reference field on a struct that is copied by value on purpose: an entry is
    ///     handed a copy and every copy appends to the same list.
    /// </remarks>
    internal List<Action>? Undo { get; init; }

    /// <summary>Subscribes a handler the way this says to.</summary>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="element">The element to listen on.</param>
    /// <param name="handler">What to run.</param>
    public void Listen<T>(UiElement element, Action<UiElement, T> handler) where T : UiEvent {
        ArgumentNullException.ThrowIfNull(element);
        element.AddHandler(handler, Strategy, HandledEventsToo);
        Undo?.Add(() => element.RemoveHandler(handler));
    }
}

/// <summary>
///     What a <see cref="Component" />'s <c>Build</c> constructs with, and what
///     <c>Vixen.Ui.Markup</c> emits calls to.
/// </summary>
/// <remarks>
///     <para>
///         Per ADR-010 every method here does one thing once. <see cref="Element" /> creates an
///         element; <see cref="Attribute" /> sets a value that will never change; <see cref="Bind(System.Action)" />
///         registers one effect that assigns one property. Nothing walks a tree, nothing diffs, and
///         a steady-state interface allocates nothing because nothing runs.
///     </para>
///     <para>
///         The two exceptions are <see cref="Switch" /> and <see cref="For" />, which is the point:
///         those are the only two places where the <i>shape</i> of the tree depends on state, so
///         those are the only two places that add and remove elements.
///     </para>
/// </remarks>
public sealed class BuildContext {
    /// <summary>The name of the slot a component's children go to when they name none.</summary>
    public const string DefaultSlot = "default";

    /// <summary>
    ///     How each event name subscribes. A table rather than a type switch, because a control
    ///     library has to be able to add to it — and rather than reflection, because
    ///     <c>Core</c> is AOT-compatible and a name-to-type lookup that ends in
    ///     <c>MakeGenericMethod</c> is not.
    /// </summary>
    /// <remarks>
    ///     Concurrent because <see cref="Subscribe" /> writes to it from a module initializer while
    ///     another thread may be building a document — two test collections, or an editor with a
    ///     background document graph. Registration is rare and reads are one lookup per markup
    ///     handler, so the cheap thing to make safe is the write.
    /// </remarks>
    static readonly ConcurrentDictionary<string, Action<UiElement, Action<UiEvent>, EventSubscription>> Subscriptions =
        new(StringComparer.Ordinal) {
            ["tap"] = (element, handler, how) =>
                how.Listen<TapEvent>(element, (_, args) => handler(args)),
            ["click"] = (element, handler, how) =>
                how.Listen<TapEvent>(element, (_, args) => handler(args)),
            ["dblclick"] = (element, handler, how) =>
                how.Listen<TapEvent>(element, (_, args) => { if (args.Count >= 2) { handler(args); } }),
            ["longpress"] = (element, handler, how) =>
                how.Listen<LongPressEvent>(element, (_, args) => handler(args)),
            ["pointerdown"] = (element, handler, how) =>
                how.Listen<PointerEvent>(
                    element,
                    (_, args) => { if (args.Action == PointerAction.Pressed) { handler(args); } }
                ),
            ["pointerup"] = (element, handler, how) =>
                how.Listen<PointerEvent>(
                    element,
                    (_, args) => { if (args.Action == PointerAction.Released) { handler(args); } }
                ),
            ["pointermove"] = (element, handler, how) =>
                how.Listen<PointerEvent>(
                    element,
                    (_, args) => { if (args.Action == PointerAction.Moved) { handler(args); } }
                ),
            ["dragstart"] = (element, handler, how) =>
                how.Listen<DragEvent>(
                    element,
                    (_, args) => { if (args.Stage == DragStage.Started) { handler(args); } }
                ),
            ["drag"] = (element, handler, how) =>
                how.Listen<DragEvent>(
                    element,
                    (_, args) => { if (args.Stage == DragStage.Moved) { handler(args); } }
                ),
            ["dragend"] = (element, handler, how) =>
                how.Listen<DragEvent>(
                    element,
                    (_, args) => { if (args.Stage is DragStage.Completed or DragStage.Cancelled) { handler(args); } }
                ),

            // ⚠ The drop side of the drag, and it was missing for as long as `DropEvent` existed: a
            // file dragged in from Finder was routed to an element and bubbled correctly, and no
            // `.vxml` in the tree could subscribe to it, because a name absent from this table is
            // an `on:` the binder rejects. `dragstart`/`drag`/`dragend` above are the *source* half
            // and always were; these three are the target's.
            ["dragenter"] = (element, handler, how) =>
                how.Listen<DragOverEvent>(
                    element,
                    (_, args) => { if (args.Stage == DragOverStage.Entered) { handler(args); } }
                ),
            ["dragover"] = (element, handler, how) =>
                how.Listen<DragOverEvent>(
                    element,
                    (_, args) => { if (args.Stage == DragOverStage.Moved) { handler(args); } }
                ),
            ["dragleave"] = (element, handler, how) =>
                how.Listen<DragOverEvent>(
                    element,
                    (_, args) => { if (args.Stage == DragOverStage.Left) { handler(args); } }
                ),
            ["drop"] = (element, handler, how) =>
                how.Listen<DropEvent>(element, (_, args) => handler(args)),

            // ⚠ Two names over one event type, the shape `pointerdown`/`pointerup` already has and
            // for the same reason: a handler that had to test `args.Action` itself would be a
            // handler that fires twice per keystroke until somebody notices. `KeyAction` is the only
            // thing separating them, so the table is where the test belongs.
            ["keydown"] = (element, handler, how) =>
                how.Listen<KeyEvent>(
                    element,
                    (_, args) => { if (args.Action == KeyAction.Pressed) { handler(args); } }
                ),
            ["keyup"] = (element, handler, how) =>
                how.Listen<KeyEvent>(
                    element,
                    (_, args) => { if (args.Action == KeyAction.Released) { handler(args); } }
                ),

            // ⚠ Registered in the same breath as the two above, because the alternative is the
            // France bug. `KeyEvent.Key` is a physical position by its US-QWERTY legend, so
            // `on:keydown` read for a letter is a text box that types `q` on an AZERTY keyboard —
            // and an author who cannot name the event that carries characters reaches for the one
            // that is there. The two exist together so the right one is always available.
            ["textinput"] = (element, handler, how) =>
                how.Listen<TextInputEvent>(element, (_, args) => handler(args)),

            // ⚠ Two names over one event again, and the pair was *promised* long before it existed:
            // the binder's alias list has accepted `onfocus` and `onblur` since it was written, so
            // both bound happily and then threw "'blur' is not an event" when the element was built.
            // `blur` is also the moment a two-way binding most often wants to commit — see
            // <see cref="TwoWay{T}" /> — which is what turned a latent alias into a missing feature.
            ["focus"] = (element, handler, how) =>
                how.Listen<FocusEvent>(element, (_, args) => { if (args.Gained) { handler(args); } }),
            ["blur"] = (element, handler, how) =>
                how.Listen<FocusEvent>(element, (_, args) => { if (!args.Gained) { handler(args); } })
        };

    /// <summary>The region currently being built into, per parent element.</summary>
    /// <remarks>
    ///     ⚠ <b>Strong, unlike <see cref="classes" />, because a region owns subscriptions and an
    ///     entry that is collected ends nothing.</b> An effect is held by every signal it read, so
    ///     dropping the only reference to the region that tracked it does not stop it running — it
    ///     stops anything being <i>able</i> to. The entries are taken out instead, by
    ///     <c>Region.Clear</c> and <c>Region.Stop</c>, which is what the region's <c>forget</c> is
    ///     for.
    /// </remarks>
    readonly Dictionary<UiElement, Region> regions = [];

    /// <summary>What a <c>class</c> attribute last wrote to an element, so it can take it back.</summary>
    /// <remarks>
    ///     ⚠ <b>Weak, unlike <see cref="regions" />, because nothing ever takes an entry out and
    ///     nothing needs to.</b> A region is taken out when it ends, and it has to be: it owns
    ///     subscriptions, and an effect nobody disposed goes on running whether or not the table
    ///     that found it was collected. What is here is a memory of the last string written, which
    ///     ends when its element does and can be dropped without telling anyone — so the key is
    ///     weak, and a <c>@for</c> over a list that churns does not hold every row it ever built for
    ///     as long as the document lives.
    /// </remarks>
    readonly ConditionalWeakTable<UiElement, string[]> classes = new();

    /// <summary>What a <c>style</c> attribute last wrote to an element, so it can take it back.</summary>
    /// <remarks>Weak for the reason <see cref="classes" /> is, and holding property names for the same one.</remarks>
    readonly ConditionalWeakTable<UiElement, string[]> styles = new();

    /// <summary>The characters those names came from, so an unchanged binding costs no parse.</summary>
    readonly ConditionalWeakTable<UiElement, string> styleText = new();

    /// <summary>Where <see cref="SetInlineStyle" /> reads a declaration list into.</summary>
    /// <remarks>
    ///     One list on the context rather than one per call. Nothing re-enters this — a declaration is
    ///     applied and forgotten before the next is read — and a <c>@for</c> that positions two hundred
    ///     rows would otherwise allocate two hundred lists per pass.
    /// </remarks>
    readonly List<InlineDeclaration> styleScratch = [];

    /// <summary>Where subscriptions go, so that clearing a branch stops everything inside it.</summary>
    Region building;

    /// <summary>The key of the <c>@for</c> row being built, for <see cref="Refs{TElement}" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Taken from the loop rather than recomputed at the tag, and that is the whole
    ///     correctness argument for <c>refs</c>.</b> A handle keyed on anything but the identity
    ///     <see cref="For{T}" /> reconciled on is a handle that disagrees with the reconciler the
    ///     first time a key expression is not what somebody assumed — and disagrees silently,
    ///     because both sides still answer.
    /// </remarks>
    object? iteration;

    /// <summary>The component whose <c>Build</c> is running, so a <c>&lt;slot&gt;</c> knows whose it is.</summary>
    Component? owner;

    /// <summary>The scope class of a markup-authored element, when this context is composing one.</summary>
    /// <remarks>
    ///     ⚠ <b>Beside <see cref="owner" /> rather than inside it, because the other thing markup
    ///     compiles to is not a <see cref="Component" />.</b> A <c>.vxml</c> with <c>@inherits</c>
    ///     produces a <see cref="UiElement" />, which has no <c>Scope</c> to ask for — and the
    ///     alternative, an interface both implement, would put two internal members of
    ///     <see cref="Component" /> on the public surface to say something a nullable string already
    ///     says. <see cref="Element" /> reads whichever of the two is set; they are never both.
    /// </remarks>
    string? scope;

    BuildContext(UiDocument document, UiElement mount) {
        Document = document;
        Mount = mount;
        Anchor = mount;

        // ⚠ `Rooted` rather than `RegionOf`, which links what it creates into the region being
        // built — and there is not one yet. See both.
        building = Rooted(mount);
    }

    /// <summary>The document being built into.</summary>
    public UiDocument Document { get; }

    /// <summary>The element the root component hangs from.</summary>
    public UiElement Mount { get; }

    /// <summary>What a null parent means right now.</summary>
    /// <remarks>
    ///     ⚠ The <i>running component's</i> root, not the document's mount point. A component's
    ///     top-level markup belongs to that component: if a null parent meant the mount, every
    ///     component in the tree would build into the same element and the nesting the markup drew
    ///     would exist nowhere.
    /// </remarks>
    public UiElement Anchor { get; private set; }

    /// <summary>Builds a component into a document.</summary>
    /// <typeparam name="T">The component type.</typeparam>
    /// <param name="document">The document.</param>
    /// <param name="mount">The element it hangs from.</param>
    /// <returns>The component, already built.</returns>
    public static T Build<T>(UiDocument document, UiElement mount) where T : Component, new() {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mount);

        var context = new BuildContext(document, mount);
        var component = context.Child<T>(mount);
        return component;
    }

    /// <summary>Builds an already-created component, so a caller can choose how it was made.</summary>
    /// <param name="component">The component.</param>
    /// <param name="document">The document.</param>
    /// <param name="mount">The element it hangs from.</param>
    /// <returns>The context that built it, which is what can rebuild it.</returns>
    /// <remarks>
    ///     The <see cref="Build{T}" /> overload constructs the component itself, which is what
    ///     markup wants and what a reload cannot use: replacing an instance means carrying state
    ///     into the new one before it builds anything.
    /// </remarks>
    public static BuildContext BuildInto(Component component, UiDocument document, UiElement mount) {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(mount);

        var context = new BuildContext(document, mount);
        context.Adopt(component, mount);
        return context;
    }

    /// <summary>Builds a markup-authored element's own tree into itself.</summary>
    /// <param name="host">The element, which is both the owner of the build and its anchor.</param>
    /// <param name="build">Its <c>Build</c> body, as the emitter wrote it.</param>
    /// <param name="scope">
    ///     The class its scoped stylesheet welds onto every selector, or null when it declared none.
    /// </param>
    /// <returns>
    ///     What stops it. The caller — generated code, in <c>OnRemoved</c> — disposes it when the
    ///     element leaves the tree.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The whole of what a <c>@inherits</c> component needs from the runtime, and it is
    ///         deliberately one method.</b> A <c>.vxml</c> whose class is a <see cref="UiElement" />
    ///         gets the <i>same</i> <see cref="BuildContext" /> a <see cref="Component" /> does, so it
    ///         gets the same <see cref="Bind(System.Action)" />, the same <see cref="Switch" />, the
    ///         same keyed <see cref="For{T}" /> reconciliation and the same region discipline. That
    ///         equality is the point: a second, weaker way to build a tree from markup would make the
    ///         markup a worse way to write the imperative code it replaced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Called from <c>OnCreated</c>, which is why the element is already in a
    ///         document.</b> <c>UiDocument.Adopt</c> binds, attaches and then calls the hook,
    ///         in that order and for this reason — a part added to an unattached parent would be laid
    ///         out relative to nothing. So <c>host.Document</c> is answerable here and the anchor is
    ///         the element itself.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Disposal stops the effects and does not remove the elements.</b> The one caller
    ///         disposes from <c>OnRemoved</c>, which the document raises top-down <i>before</i> it
    ///         detaches anything — so the subtree is already going, and removing it again from inside
    ///         the walk that is removing it would be a nested <c>Document.Remove</c> per element. See
    ///         <c>Region.Stop</c>.
    ///     </para>
    /// </remarks>
    public static IDisposable Compose(UiElement host, Action<BuildContext> build, string? scope = null) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(build);

        var context = new BuildContext(host.Document, host) { scope = scope };

        // ⚠ Before the build, exactly as `Component.Mount` does it and for the same reason: `Element`
        // reads the scope for every element the body makes, and a class added afterwards would leave
        // the element's own children unscoped for the first pass — which is not a race, it is simply
        // wrong, because nothing goes back over them.
        if (scope is { } named) {
            host.AddClass(named);
        }

        build(context);
        return new Unsubscribe(context.StopEverything);
    }

    /// <summary>Stops every subscription this context made, wherever it hung it.</summary>
    /// <remarks>
    ///     ⚠ <b>The host's region, which reaches the rest because every region is linked into the
    ///     one that built its element.</b> An <c>@for</c> written inside a nested <c>&lt;div&gt;</c>
    ///     opens its region against that div — see <see cref="Open" /> and <see cref="RegionOf" /> —
    ///     so the host's <i>slots</i> never contained it. This walked the whole table instead, which
    ///     was right for a composed element and could not be copied to a <see cref="Component" />,
    ///     because a component shares the document's context with every other component in it and
    ///     "every region in the table" is not "every region I made". Linking answers the question
    ///     for both, so the special case is gone and so is the gap it could not close.
    /// </remarks>
    void StopEverything() => Rooted(Mount).Stop();

    void Adopt(Component component, UiElement parent) {
        var host = Element(parent, component.TagName);

        // Registered with the document and not tracked anywhere here, because nothing above a
        // component mounted this way ever clears: `BuildInto` is what a reload host and a panel use,
        // and removing the host is the only end either of them has.
        Teardown(host);

        owner = component;
        Anchor = host;
        building = Rooted(host);

        component.Mount(this, host);
    }

    /// <summary>Throws away what a component built and builds it again.</summary>
    /// <param name="component">The component, which keeps its identity and its fields.</param>
    /// <remarks>
    ///     <para>
    ///         What a hot reload calls once the method body behind <c>Build</c> has been replaced.
    ///         The component object survives, so everything it holds survives with it — its signals
    ///         above all, which is most of what "state was preserved" means in practice.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The elements do not survive, and cannot.</b> Two <c>Build</c> bodies are two
    ///         different programs; there is no identity an element from the first shares with one
    ///         from the second beyond its position, and reconciling on position alone would move
    ///         state onto whatever happened to be in the same slot. What is carried across is
    ///         carried deliberately, by name, by whoever asked for the reload.
    ///     </para>
    /// </remarks>
    public void Rebuild(Component component) {
        ArgumentNullException.ThrowIfNull(component);

        var root = component.Root;

        // Cleared rather than stopped, which is the one place the two differ for a component: the
        // host stays and everything under it has to go, because a second `Build` is about to fill
        // it again. `Region.Clear` takes the entry out of `regions`, so the fetch below opens a new
        // one — which is also why `Ended` looks its region up rather than holding it.
        Rooted(root).Clear();

        var previousOwner = owner;
        var previousAnchor = Anchor;
        var previousBuilding = building;

        owner = component;
        Anchor = root;
        building = Rooted(root);

        try {
            component.Mount(this, root);
        } finally {
            owner = previousOwner;
            Anchor = previousAnchor;
            building = previousBuilding;
        }
    }

    // ================================================================== Elements

    /// <summary>Creates an intrinsic element.</summary>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="tag">Its element name.</param>
    /// <returns>The element.</returns>
    public UiElement Element(UiElement? parent, string tag) {
        var target = parent ?? Anchor;
        var element = Document.Create(tag, target);
        RegionOf(target).Add(element);

        // ⚠ The *owner's* scope, which is what makes `scoped` mean anything: an element created
        // while building a component belongs to that component, and a caller's element projected
        // into one of its slots was created while building the caller and does not get it. That
        // distinction is the whole feature, and it falls out of `owner` rather than being decided
        // here.
        if (Scope is { } named) {
            element.AddClass(named);
        }

        return element;
    }

    /// <summary>The scope class the elements being built carry, or null when they are not scoped.</summary>
    /// <remarks>
    ///     The running <see cref="Component" />'s, or the markup-authored element's when the context
    ///     is composing one. Never both: <see cref="Child{T}(UiElement)" /> saves and restores
    ///     <see cref="owner" /> around a nested component, and a composed element gets a context of
    ///     its own.
    /// </remarks>
    string? Scope => owner is { } running ? running.Scope : scope;

    /// <summary>Creates whatever a capitalised tag named, and builds it if it is a component.</summary>
    /// <typeparam name="T">The component type or the element type.</typeparam>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <returns>What was created.</returns>
    /// <exception cref="ArgumentException">
    ///     <typeparamref name="T" /> implements <see cref="IComposable" /> and is neither, which
    ///     nothing in the framework does and no generated code can produce.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         <b>One method for two kinds of tag</b>, because the markup compiler cannot tell them
    ///         apart and deliberately does not try: <c>&lt;Callout /&gt;</c> names a
    ///         <see cref="Component" /> and <c>&lt;ProgressBar /&gt;</c> names a
    ///         <see cref="UiElement" />, and both are written the same way. The type argument is
    ///         what settles it, which puts the decision in the C# compiler where the rest of the
    ///         markup channel's checking already lives.
    ///     </para>
    ///     <para>
    ///         Either way the host element's tag is the one the type answers to, so
    ///         <c>&lt;Callout /&gt;</c> is styled by <c>callout { … }</c> and
    ///         <c>&lt;ProgressBar /&gt;</c> by <c>progress-bar { … }</c> — the same rule the control
    ///         library already follows for a control built by hand. The overload taking a
    ///         <c>tag</c> is markup's <c>tag="…"</c> attribute and says otherwise.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An element is not entered, and a component is.</b> A component's markup builds
    ///         into its host and belongs to it, so <see cref="Anchor" /> and the scope move; a
    ///         control builds its own parts in <c>OnCreated</c> and what the markup wrote inside its
    ///         tag is the caller's content, projected in. Moving the anchor for a control would make
    ///         its stylesheet's scope class land on elements the caller wrote.
    ///     </para>
    /// </remarks>
    public T Child<T>(UiElement? parent) where T : IComposable, new() => Child<T>(parent, null);

    /// <summary>The same, under a tag of the caller's choosing.</summary>
    /// <typeparam name="T">The component type or the element type.</typeparam>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="tag">
    ///     The element name to create it under, or null to take the one the type answers to.
    /// </param>
    /// <returns>What was created.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>What markup's <c>tag="…"</c> attribute emits, and the runtime has always had it:
    ///         <see cref="UiDocument.Adopt(UiElement, string, UiElement, string, System.ReadOnlySpan{string})" /> takes the tag and only falls back to
    ///         <see cref="UiElement.TagName" />, so <c>panel.Add&lt;WaterZoneFacts&gt;("water-facts")</c>
    ///         was already legal C#.</b> What did not exist was a spelling of it in a <c>.vxml</c>,
    ///         which is why <c>Part&lt;ScrollView&gt;("add-component-list")</c> — a control under the
    ///         tag a stylesheet names — was a shape markup could not write and a sealed control could
    ///         not be subclassed into.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read once, at creation, and never again.</b> An element's tag is fixed by
    ///         <see cref="UiElement.Bind" /> — the style tree interns it into the node — so this is
    ///         not a binding and a later change to whatever the expression read does nothing. Inside
    ///         an <c>@for</c> that is exactly right and is the same rule keys already follow: a row
    ///         whose tag depends on the data has to put that data in its <c>key</c>, because a
    ///         surviving key keeps its element and an element keeps its tag.
    ///     </para>
    /// </remarks>
    public T Child<T>(UiElement? parent, string? tag) where T : IComposable, new() {
        var created = Create<T>(parent, tag);
        Compose(created);

        return created;
    }

    /// <summary>Creates a child without building it, so its parameters can be assigned first.</summary>
    /// <typeparam name="T">The component type or the element type.</typeparam>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <returns>What was created, not yet built. <see cref="Compose{T}" /> builds it.</returns>
    public T Create<T>(UiElement? parent) where T : IComposable, new() => Create<T>(parent, null);

    /// <summary>The same, under a tag of the caller's choosing.</summary>
    /// <typeparam name="T">The component type or the element type.</typeparam>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="tag">
    ///     The element name to create it under, or null to take the one the type answers to.
    /// </param>
    /// <returns>What was created, not yet built. <see cref="Compose{T}" /> builds it.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because a component's parameters used to be assigned after its
    ///         <c>Build</c> had already run.</b> <c>&lt;Panel Model="@Model" /&gt;</c> emitted the
    ///         construction, the mount and then the assignment, so every effect the child made had
    ///         already read <c>Model</c> once at its default — and a plain C# property assigned later
    ///         notifies nobody, so the child drew the empty model for ever. Signal-backing the
    ///         property was the only escape, by convention, with nothing enforcing it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This fixes the first read and not the tracking.</b> A prop that is expected to
    ///         <i>keep</i> following its source still has to be signal-backed: an effect inside the
    ///         child subscribes to what it reads, and a plain property is not something to subscribe
    ///         to. What changes is that the value it reads first is now the caller's rather than the
    ///         default — which is what SwiftUI gets structurally, by initialising a view with its
    ///         values.
    ///     </para>
    ///     <para>
    ///         An element has nothing deferred about it: it is adopted here and
    ///         <see cref="Compose{T}" /> does nothing to it. The split exists for the
    ///         <see cref="Component" /> half, and the markup emitter writes the same pair for both
    ///         because it cannot tell which a capitalised tag is.
    ///     </para>
    /// </remarks>
    public T Create<T>(UiElement? parent, string? tag) where T : IComposable, new() {
        var created = new T();

        switch (created) {
            case UiElement element: {
                var target = parent ?? Anchor;
                Document.Adopt(element, tag, target);
                RegionOf(target).Add(element);

                if (Scope is { } elementScope) {
                    element.AddClass(elementScope);
                }

                return created;
            }

            case Component component: {
                var host = Element(parent, tag ?? component.TagName);

                // ⚠ **The region a component builds into hangs off its host, not off the region
                // being built** — so clearing the enclosing branch removes the host element and
                // would never reach what the component put inside it. Its effects went on running
                // against detached elements, which is the exact failure regions exist to prevent
                // and which `A_branch_that_leaves_takes_its_effects_with_it` only ever tested for
                // plain ones.
                //
                // A subscription rather than a slot: slot order is how a region computes its
                // indices within *one* parent element, and this region's parent is the host.
                // `Region.Clear` disposes subscriptions before it removes elements, so a
                // component's effects stop before anything it built goes.
                building.Track(Teardown(host));
                component.Attach(this, host);

                return created;
            }

            default:
                throw new InvalidOperationException(
                    $"'{typeof(T).Name}' is neither a component nor an element, so it cannot be a tag."
                );
        }
    }

    /// <summary>Runs the <c>Build</c> of what <see cref="Create{T}(Vixen.Ui.UiElement,string)" /> made.</summary>
    /// <typeparam name="T">The component type or the element type.</typeparam>
    /// <param name="created">What <see cref="Create{T}(Vixen.Ui.UiElement,string)" /> returned.</param>
    /// <remarks>
    ///     ⚠ <b>A no-op for an element, and that is the point.</b> A capitalised tag names a
    ///     <see cref="Component" /> or a <see cref="UiElement" /> and the markup compiler resolves
    ///     neither — so the pair it emits has to be writable for both, and which of the two this is
    ///     settles at the C# call the same way <see cref="Child{T}(UiElement,string)" /> settles it.
    ///     A control builds its own parts in <c>OnCreated</c>, before any of this.
    /// </remarks>
    public void Compose<T>(T created) where T : IComposable {
        if (created is not Component component) {
            return;
        }

        var previousOwner = owner;
        var previousAnchor = Anchor;
        var previousBuilding = building;

        owner = component;
        Anchor = component.Root;
        building = Rooted(component.Root);

        try {
            component.Compose(this);
        } finally {
            owner = previousOwner;
            Anchor = previousAnchor;
            building = previousBuilding;
        }
    }

    /// <summary>The element an attribute written on a capitalised tag applies to.</summary>
    /// <param name="component">The component.</param>
    /// <returns>The element it drew itself into.</returns>
    /// <remarks>
    ///     <para>
    ///         <c>class</c> on <c>&lt;Callout&gt;</c> styles the element the component drew, not the
    ///         component object — and on <c>&lt;ProgressBar&gt;</c> it styles the control, which is
    ///         already an element. The emitter cannot choose between <c>.Root</c> and nothing at
    ///         all, because it does not know which kind of tag it wrote; these two overloads make
    ///         the choice for it, at compile time and at no cost.
    ///     </para>
    /// </remarks>
    public static UiElement Host(Component component) {
        ArgumentNullException.ThrowIfNull(component);
        return component.Root;
    }

    /// <inheritdoc cref="Host(Component)" />
    /// <param name="element">The element, which is its own host.</param>
    public static UiElement Host(UiElement element) => element;

    /// <summary>Where the content written inside a capitalised tag goes.</summary>
    /// <param name="component">The component.</param>
    /// <returns>Its default slot, or its root when it declared none.</returns>
    public static UiElement Inner(Component component) {
        ArgumentNullException.ThrowIfNull(component);
        return component.Content;
    }

    /// <inheritdoc cref="Inner(Component)" />
    /// <param name="element">The element, or the part it keeps its content in.</param>
    /// <remarks>
    ///     <see cref="UiElement.ContentHost" />, which is the element itself for everything that
    ///     does not have a scrolling viewport or a panel to put content in — and is that part for
    ///     the ones that do. A <c>&lt;ScrollView&gt;</c> whose markup children hung off the control
    ///     rather than off its viewport would put them beside the scrollbars.
    /// </remarks>
    public static UiElement Inner(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);
        return element.ContentHost;
    }

    /// <summary>Teaches the runtime an event name, or changes what one already means.</summary>
    /// <param name="name">The name, as written after <c>on:</c>.</param>
    /// <param name="subscribe">
    ///     How to subscribe: given the element, what to call, and an <see cref="EventSubscription" />
    ///     saying how to attach it. The handler it is given already applies the <c>on:</c> modifiers
    ///     that are filters; the ones that are not — the routing leg, and whether an already-handled
    ///     event still arrives — are what the third argument carries, and an entry passes them on by
    ///     calling <see cref="EventSubscription.Listen{T}" /> rather than <c>AddHandler</c>.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         The table is in <c>Vixen.Ui</c> and knows only the events <c>Vixen.Ui</c> raises.
    ///         <c>Vixen.Ui.Controls</c> is where activation lives — a button is pressed by Space and
    ///         Enter as well as by a tap — and it registers from a module initializer, so a project
    ///         that uses a control gets the right meaning for <c>on:click</c> without knowing this
    ///         exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Replacing an entry is allowed and is the point.</b> An additive-only table would
    ///         leave <c>on:click</c> on a <c>&lt;Button&gt;</c> meaning "a pointer tapped it", which
    ///         is right until somebody uses the keyboard — the sort of accessibility bug that is
    ///         invisible to everyone who tests with a mouse.
    ///     </para>
    /// </remarks>
    public static void Subscribe(string name, Action<UiElement, Action<UiEvent>, EventSubscription> subscribe) {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(subscribe);

        Subscriptions[name] = subscribe;
    }

    /// <summary>Creates an element holding fixed text.</summary>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="text">The text.</param>
    /// <returns>The element.</returns>
    public UiElement Text(UiElement? parent, string text) {
        var element = Element(parent, "text");
        element.Text = text;
        return element;
    }

    /// <summary>Creates an element holding text that follows an expression.</summary>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="text">What to show. Re-read whenever something it read changes.</param>
    /// <returns>The element.</returns>
    public UiElement Text(UiElement? parent, Func<object?> text) {
        ArgumentNullException.ThrowIfNull(text);

        var element = Element(parent, "text");
        Bind(() => element.Text = Format(text()));
        return element;
    }

    // ================================================================== Values

    /// <summary>Sets a value that will not change.</summary>
    /// <param name="target">The element.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">Its value.</param>
    /// <remarks>
    ///     <para>
    ///         <c>class</c> is one of two names handled specially, because it is a set rather than a
    ///         value: writing it replaces what the <i>last write</i> put there rather than appending
    ///         to it, which is what makes <c>class="btn @variant"</c> behave when <c>variant</c>
    ///         changes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>style</c> is the other, and it is the one name that must <i>not</i> reach the
    ///         style tree.</b> An attribute there is data a selector can match on — <c>[style]</c> —
    ///         and nothing reads it; in CSS an inline style is a cascade origin that outranks every
    ///         author rule. The engine has had that origin all along
    ///         (<c>CascadeRanks.NormalInline</c>, <c>UiElement.SetStyle</c>); what it did not have was
    ///         a way for markup to reach it, so <c>style="width: 42%"</c> silently did nothing but add
    ///         a selectable attribute. See <see cref="SetInlineStyle" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The last write, not every class the element carries.</b> An element is given
    ///         classes by things that are not this attribute — a scoped stylesheet's scope class in
    ///         <see cref="Element" />, and a <c>Control</c>'s own <c>variant-default</c> and
    ///         <c>size-md</c>, both applied before any markup attribute is. Treating <c>class</c> as
    ///         the complete set deleted them: <c>&lt;Button class="row" Variant="Subtle" /&gt;</c>
    ///         got its variant back from the assignment that followed and lost its size outright.
    ///         A panel that wanted a class on a control tag had to call <c>AddClass</c> from
    ///         <c>OnComposed</c> instead, which is no longer true and no longer worth doing.
    ///     </para>
    /// </remarks>
    public void Attribute(UiElement target, string name, string value) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        if (string.Equals(name, "class", StringComparison.Ordinal)) {
            SetClasses(target, value);
            return;
        }

        if (string.Equals(name, "style", StringComparison.Ordinal)) {
            SetInlineStyle(target, value);
            return;
        }

        Document.Styles.Tree.SetAttribute(target.StyleNode, name, value);
        Document.Invalidate();
    }

    /// <summary>Keeps an attribute equal to an expression.</summary>
    /// <param name="target">The element.</param>
    /// <param name="name">The attribute name.</param>
    /// <param name="value">What it should be.</param>
    public void Bind(UiElement target, string name, Func<object?> value) {
        ArgumentNullException.ThrowIfNull(value);
        Bind(() => Attribute(target, name, Format(value())));
    }

    /// <summary>Runs an assignment now, and again whenever what it read changes.</summary>
    /// <param name="assign">The assignment.</param>
    /// <remarks>
    ///     <para>
    ///         One effect per dynamic expression. It is registered against the region being built,
    ///         so a branch that leaves the tree takes its effects with it — an effect that outlived
    ///         its element would keep the element alive through its closure and keep assigning to
    ///         it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And against the <i>document's</i> scheduler rather than the thread's.</b> See
    ///         <see cref="UiDocument.Effects" />: two documents on one thread sharing a queue means
    ///         either of them can drain the other's, and a document that has been disposed still
    ///         has effects in it.
    ///     </para>
    /// </remarks>
    public void Bind(Action assign) {
        ArgumentNullException.ThrowIfNull(assign);
        building.Track(new Effect(assign, Document.Effects));
    }

    /// <summary>Runs an expression against what a tag made, now and again whenever what it read changes.</summary>
    /// <typeparam name="T">What the tag made: a control, an element, or a <see cref="Component" />.</typeparam>
    /// <param name="target">The thing the tag made.</param>
    /// <param name="action">What to do with it.</param>
    /// <remarks>
    ///     <para>
    ///         <b>This is markup's <c>use</c>, and it exists because a control fed by a
    ///         <i>method</i> had no markup spelling at all.</b> A component-tag parameter is a
    ///         property assignment, so <c>&lt;Slider Value="@x" /&gt;</c> works and
    ///         <c>panel.Inspect(descriptor, provider, targets)</c>,
    ///         <c>list.SetItems(rows)</c> and <c>select.AddOption(…)</c> do not — three arguments,
    ///         a collection, and a call per item are none of them a property. The recorded escape
    ///         for all three was a four-line subclass exposing the call as a property, which
    ///         <c>sealed</c> refuses; <c>use</c> is the same idea without the type.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An effect and not a callback, which is the whole of why it is worth having.</b>
    ///         It is <see cref="Bind(Action)" /> with a subject, so every signal the expression
    ///         reads is a dependency and the call is made again when one of them changes — which is
    ///         what makes <c>use="@(v =&gt; v.Inspect(Chosen, Provider, Targets))"</c> a live panel
    ///         rather than a one-shot. It is registered against the region being built, so a branch
    ///         or a row that leaves takes it with it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which means it must be idempotent, and a call that appends is not.</b>
    ///         <c>use="@(v =&gt; v.AddOption(…))"</c> adds an option every time the expression's
    ///         dependencies change. The rule is the one every effect here follows: say what the
    ///         control should <i>be</i>, not what to do to it — <c>SetItems</c>, not <c>Add</c> —
    ///         and if the control offers only the appending form, clear it first inside the same
    ///         expression.
    ///     </para>
    /// </remarks>
    public void Use<T>(T target, Action<T> action) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(action);

        Bind(() => action(target));
    }

    /// <summary>Subscribes a handler to an event by name.</summary>
    /// <param name="target">The element.</param>
    /// <param name="name">The event name, as written after <c>on:</c>.</param>
    /// <param name="handler">What to run.</param>
    /// <param name="modifiers">
    ///     <c>stop</c> marks the event handled, <c>capture</c> listens on the way down, <c>self</c>
    ///     ignores events that started somewhere else, and <c>once</c> unsubscribes afterwards.
    /// </param>
    public void On(UiElement target, string name, Action handler, params string[] modifiers) {
        ArgumentNullException.ThrowIfNull(handler);
        On<UiEvent>(target, name, _ => handler(), modifiers);
    }

    /// <summary>Subscribes a handler that wants the event.</summary>
    /// <typeparam name="TEvent">The event type the handler expects.</typeparam>
    /// <param name="target">The element.</param>
    /// <param name="name">The event name, as written after <c>on:</c>.</param>
    /// <param name="handler">What to run.</param>
    /// <param name="modifiers">As above.</param>
    /// <exception cref="ArgumentException">The name is not one the runtime knows.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><typeparamref name="TEvent" /> is a filter, not the subscription.</b> What is
    ///         subscribed to is decided by the name's entry in the table — which for <c>click</c> is
    ///         a different event type depending on whether the target is a control — and an argument
    ///         of another type is dropped. So <see cref="UiEvent" /> is what a handler taking no
    ///         argument gets, and a handler that narrows is asking for a subset of what the name
    ///         delivers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>From markup the handler has to be an explicitly typed lambda —
    ///         <c>on:keydown.capture="@((KeyEvent e) => Keyed(e))"</c> — and a method group does not
    ///         work.</b> The emitter writes one call for both overloads and cannot name the event
    ///         type, because which type a name delivers is this table's business rather than the
    ///         compiler's; so <typeparamref name="TEvent" /> is inferred from the argument, and a
    ///         method group offers nothing to infer it from. Its natural type needs the delegate's
    ///         parameter types, which are exactly what is being solved for. The failure is Roslyn's
    ///         <i>"cannot convert from 'method group' to 'System.Action'"</i>, landing on the
    ///         handler's own characters in the <c>.vxml</c> — legible, but not obviously about
    ///         inference, which is why it is written down here and pinned by
    ///         <c>EmitterTests.A_method_group_handler_cannot_type_itself_and_says_so_at_the_handler</c>.
    ///     </para>
    /// </remarks>
    public void On<TEvent>(UiElement target, string name, Action<TEvent> handler, params string[] modifiers)
        where TEvent : UiEvent {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(handler);

        if (!Subscriptions.TryGetValue(name, out var subscribe)) {
            // The markup compiler cannot catch this — it does not know what events exist — and the
            // runtime can, so it does, loudly. A silently ignored `on:clcik` is a control that does
            // nothing for a reason nobody can see.
            throw new ArgumentException(
                $"'{name}' is not an event. Known events: {string.Join(", ", Subscriptions.Keys.Order(StringComparer.Ordinal))}.",
                nameof(name)
            );
        }

        var once = modifiers.Contains("once", StringComparer.Ordinal);
        var self = modifiers.Contains("self", StringComparer.Ordinal);
        var stop = modifiers.Contains("stop", StringComparer.Ordinal);

        // ⚠ Collected so the region can undo the subscription, which matters for exactly one target
        // and is free for every other. A handler bound to an element the body *made* needs no
        // removal — clearing the region removes the element and the subscription goes with it — but
        // `<self />` names `Host(this)`, which the body did not make and a rebuild does not replace.
        // `BuildContext.Rebuild` clears the host's children and re-enters `Build` on the same root,
        // so without this a `.vxml` save doubled the host's handlers and one press counted twice.
        // Pinned by `EmitterTests.Self_does_not_subscribe_the_host_again_when_a_component_is_rebuilt`.
        List<Action> undo = [];

        var how = new EventSubscription {
            Strategy = modifiers.Contains("capture", StringComparer.Ordinal)
                ? RoutingStrategy.Capture
                : RoutingStrategy.Bubble,

            // ⚠ The one modifier that cannot be applied here, so it is passed on rather than read.
            // `stop`, `once` and `self` all filter a handler this owns; `handled` decides whether
            // the router calls it in the first place, which is a property of the registration.
            HandledEventsToo = modifiers.Contains("handled", StringComparer.Ordinal),
            Undo = undo
        };

        var spent = false;

        void Invoke(UiEvent args) {
            if (spent || (self && !ReferenceEquals(args.Source, target)) || args is not TEvent typed) {
                return;
            }

            spent = once;
            handler(typed);

            if (stop) {
                args.Handled = true;
            }
        }

        subscribe(target, Invoke, how);

        // ⚠ One entry for however many handlers the table entry attached — `click` attaches two —
        // and `Unsubscribe` runs it once, which is what a region stopped and then cleared needs.
        building.Track(
            new Unsubscribe(
                () => {
                    foreach (var remove in undo) {
                        remove();
                    }
                }
            )
        );
    }

    /// <summary>Binds a property in both directions.</summary>
    /// <typeparam name="T">The property's type.</typeparam>
    /// <param name="target">The element.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="get">Reads the source.</param>
    /// <param name="set">Writes it back.</param>
    /// <param name="commits">
    ///     The events that commit the write, as written after <c>on:</c>. None means every change
    ///     commits, which is what a <c>bind:</c> with no modifiers does.
    /// </param>
    /// <exception cref="ArgumentException">
    ///     The element has no such property, the property is of a different type than
    ///     <typeparamref name="T" />, or one of the names is not an event.
    /// </exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The type has to match exactly, and this refuses a mismatch out loud because for a
    ///         long time it did not refuse one at all.</b> Both legs go through
    ///         <see cref="UiPropertyKey" />, which boxes: the forward leg unboxes to the property's
    ///         own type and the write-back casts to <typeparamref name="T" />, and an unbox is
    ///         exact — <c>float</c> is not <c>double</c> and <c>int?</c> is not <c>int</c>. The cast
    ///         threw where it always did; what made it invisible is that the forward leg is an
    ///         <c>Effect</c>, and a throwing effect is suspended and logged rather than propagated —
    ///         deliberately, so that one bad binding cannot take a window down. The result was the
    ///         worst failure this framework can produce: a control that never moves, a model that is
    ///         never written, and nothing said. A <c>bind:</c> that cannot work now says so at
    ///         compose, where the panel that wrote it is on the stack.
    ///     </para>
    ///     <para>
    ///         <b>It is not a conversion seam and does not pretend to be one.</b> Coercing here would
    ///         make <c>bind:</c> lossy in a way the author never wrote down; what a mismatched model
    ///         wants is either a property of its own type or an explicit converter, and neither is
    ///         something this method can invent.
    ///     </para>
    ///     <para>
    ///         With no <paramref name="commits" /> the write-back arrives through
    ///         <see cref="UiElement.PropertyChanged" /> rather than through a poll, and is guarded so
    ///         that the assignment this binding just made does not come straight back as a change to
    ///         write to the source — which would be a loop the effect scheduler's runaway detector
    ///         would catch, several frames after the cause.
    ///     </para>
    ///     <para>
    ///         <b>Which event commits the write, and it is an event name rather than a vocabulary of
    ///         its own.</b> <c>bind:Value.blur</c>, <c>bind:Value.submit</c> and
    ///         <c>bind:Value.dragend</c> all read out of the same table <c>on:</c> reads, so
    ///         "commit" needs no per-control registration and no second list of names to keep in
    ///         step with the first. A control that publishes a moment publishes it once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The default is unchanged and stays unchanged.</b> Every <c>bind:</c> in the tree
    ///         writes a <c>Signal&lt;T&gt;</c>, where a write per keystroke is idempotent and a
    ///         deferred one would only make the panel lag its own field. What per-change costs is
    ///         paid by a consumer that treats each write as a decision — an undo entry per frame of
    ///         a slider drag — and that consumer is the one that asks for a commit event. Blazor
    ///         defaults the other way round; this table's names are the reason it cannot here, since
    ///         a control with no commit moment would then never write at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The value is read at the event and not remembered from the change.</b> So a
    ///         field that reformats what it holds on commit — <c>NumericInput</c> turns <c>007</c>
    ///         into <c>7</c> in <c>OnSubmit</c>, before <c>submit</c> is raised — hands the model
    ///         what it settled on rather than what was typed.
    ///     </para>
    /// </remarks>
    public void TwoWay<T>(UiElement target, string name, Func<T> get, Action<T> set, params string[] commits) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(commits);

        var key = KeyOf(target, name);

        if (key.ValueType != typeof(T)) {
            throw new ArgumentException(
                $"'{target.Tag}.{name}' is a {key.ValueType.Name} and the bound expression is a "
                + $"{typeof(T).Name}. A two-way binding goes both ways through the property, and both "
                + "are exact — bind an expression of the property's own type, or convert either side "
                + "explicitly.",
                nameof(name)
            );
        }

        var writing = false;

        Bind(() => {
            writing = true;
            try {
                key.SetValue(target, get());
            } finally {
                writing = false;
            }
        });

        if (commits.Length == 0) {
            Watch(target, key, () => !writing, () => set((T)key.GetValue(target)!));
            return;
        }

        foreach (var commit in commits) {
            // ⚠ No `writing` guard, and it is not an oversight. That guard exists to stop the
            // forward leg's own assignment arriving back as a change; an event is delivered from
            // input, never from inside `key.SetValue`, so there is nothing to suppress — and
            // suppressing on `IsFlushing` the way `Changed` does would drop a commit that landed in
            // the same frame as an unrelated binding's write.
            On(target, commit, () => set((T)key.GetValue(target)!));
        }
    }

    /// <summary>Calls a handler whenever a property changes, other than because a binding wrote it.</summary>
    /// <typeparam name="T">The property's type.</typeparam>
    /// <param name="target">The element.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="read">Reads the property. Its return type is what <typeparamref name="T" /> is.</param>
    /// <param name="handler">What to run, given the new value.</param>
    /// <exception cref="ArgumentException">The element has no such property.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>This is <c>on:change</c>, and it is not an event.</b> A control's value-change
    ///         notification is <c>Action&lt;TControl, TValue&gt;</c> — <c>Slider.ValueChanged</c>,
    ///         <c>ToggleBase.CheckedChanged</c>, <c>NumericInput.NumberChanged</c>,
    ///         <c>Select.SelectionChanged</c> — and the <see cref="Subscribe" /> table holds
    ///         <c>Action&lt;UiElement, Action&lt;UiEvent&gt;, RoutingStrategy&gt;</c>, which is a
    ///         routed gesture. No entry in that table can carry a value, and the six controls that
    ///         also raise a routed <c>ValueChangedEvent&lt;T&gt;</c> are six of about thirty and name
    ///         a different <c>T</c> each, so one name could not subscribe to them either.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the mechanism is <see cref="TwoWay{T}" />'s write-back leg, on its own.</b>
    ///         Every <c>[UiProperty]</c> raises <see cref="UiElement.PropertyChanged" /> when its
    ///         value actually changes, whatever changed it — a drag, a key, an access key, or the
    ///         panel's own code — which is strictly more than any one control's event hears, and it
    ///         is already the thing <c>bind:</c> trusts. Nothing new has to be registered per control
    ///         and nothing is reflected over: the key is looked up once here and the value is read
    ///         through <paramref name="read" />, so the type is the property's own and no cast or box
    ///         is involved.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Changes made while the document's effects are draining are not reported, and
    ///         that is the one rule this does not share with <c>bind:</c>.</b> A write during a flush
    ///         came <i>from</i> a binding, which means it came from the model — so handing it to a
    ///         handler whose job is to put values into the model is at best a write of what is
    ///         already there. It is not always harmless: the forward binding of
    ///         <c>&lt;Slider Value="@bus.Gain" change:Value="…" /&gt;</c> runs one flush <i>after</i>
    ///         the subscription is made, so without this the panel would post an undo entry for a
    ///         gain nobody touched, every time it was opened. The hand-written C# it replaces cannot
    ///         have that bug, because there the value is assigned before the <c>+=</c>.
    ///     </para>
    ///     <para>
    ///         The cost is a change a control makes to itself <i>during</i> a binding's write — a
    ///         coerce that clamps, <c>RangeBase.OnBoundsChanged</c> re-snapping a value — which the
    ///         model is not told about. That is a real divergence and the honest statement is that it
    ///         is the lesser one.
    ///     </para>
    /// </remarks>
    public void Changed<T>(UiElement target, string name, Func<T> read, Action<T> handler) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(handler);

        var key = KeyOf(target, name);
        Watch(target, key, () => !Document.Effects.IsFlushing, () => handler(read()));
    }

    /// <summary>Registers an <c>@for</c> row's element under the key the loop reconciles it on.</summary>
    /// <typeparam name="TElement">What the tag made.</typeparam>
    /// <param name="refs">The handle to register into.</param>
    /// <param name="element">The element.</param>
    /// <exception cref="InvalidOperationException">No <c>@for</c> row is being built.</exception>
    /// <remarks>
    ///     ⚠ <b>The entry is tracked on the row's region, so it goes when the row does.</b> A handle
    ///     that only ever gained entries would answer for rows that had left the document, and would
    ///     hold them — and every element under them — alive for as long as the panel lived.
    /// </remarks>
    public void Refs<TElement>(ElementRefs<TElement> refs, TElement element) where TElement : UiElement {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(element);

        if (iteration is not { } key) {
            // The markup compiler refuses this (`VXML2013`) so nothing generated reaches here. Code
            // calling the runtime directly can, and an entry under a key nobody can name would be a
            // handle that silently answers nothing.
            throw new InvalidOperationException(
                "'refs' is only meaningful inside an @for: its key is the loop's. Outside one, hold "
                + "the element in a member of its own — which is what 'ref' does."
            );
        }

        refs.Add(key, element);
        building.Track(new Unsubscribe(() => refs.Remove(key, element)));
    }

    /// <summary>The registered property one of these bindings names, or an error saying it is not one.</summary>
    static UiPropertyKey KeyOf(UiElement target, string name) {
        ArgumentNullException.ThrowIfNull(name);

        return UiPropertyRegistry.TryFindFor(target, name, out var key)
            ? key
            : throw new ArgumentException($"'{target.Tag}' has no property called '{name}'.", nameof(name));
    }

    /// <summary>Runs something when one property changes, for as long as the region lives.</summary>
    /// <param name="target">The element.</param>
    /// <param name="key">The property to listen for.</param>
    /// <param name="wanted">Whether this change is one to report.</param>
    /// <param name="react">What to do about it.</param>
    void Watch(UiElement target, UiPropertyKey key, Func<bool> wanted, Action react) {
        void Notified(UiElement _, UiPropertyKey changed) {
            if (ReferenceEquals(changed, key) && wanted()) {
                react();
            }
        }

        target.PropertyChanged += Notified;
        building.Track(new Unsubscribe(() => target.PropertyChanged -= Notified));
    }

    /// <summary>Declares where a caller's children go.</summary>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="name">The slot's name.</param>
    /// <returns>The slot element, for a caller that has to route its own content into it.</returns>
    /// <remarks>
    ///     ⚠ <b>It returns the element because a <c>@inherits</c> component has no
    ///     <see cref="Component.Slots" /> to be declared into.</b> A <see cref="Component" /> is
    ///     handed its content by <see cref="Inner(Component)" />, which reads the dictionary
    ///     <see cref="Component.Declare" /> fills; a <see cref="UiElement" /> answers the same
    ///     question with <see cref="UiElement.ContentHost" />, which is a property it overrides — so
    ///     generated code takes the element back and returns it from there. One call site does the
    ///     declaring either way, which is why this is a return value rather than a second method.
    /// </remarks>
    public UiElement Slot(UiElement? parent, string name) {
        ArgumentNullException.ThrowIfNull(name);

        var slot = Element(parent, "slot");
        owner?.Declare(name, slot);

        return slot;
    }

    /// <summary>Where content a consumer addressed to a named slot goes.</summary>
    /// <param name="component">The component whose tag the content was written inside.</param>
    /// <param name="name">The slot's name, from the consumer's <c>slot="…"</c>.</param>
    /// <returns>The element that slot was declared on.</returns>
    /// <exception cref="InvalidOperationException">The component declares no such slot.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The consumer half of <see cref="Slot" />, and until it existed the declaring half
    ///         was write-only.</b> <see cref="Component.Declare" /> put every slot in the dictionary
    ///         and one line read it back — <see cref="Component.Content" />, looking up
    ///         <see cref="DefaultSlot" /> and nothing else. A <c>&lt;slot name="footer"&gt;</c>
    ///         therefore parsed, bound, emitted, ran, and could not be filled by anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unknown name throws rather than falling back to the default slot, which is the
    ///         opposite of what the web platform does.</b> HTML drops unmatched slotted content on the
    ///         floor, and that reading is right for a document assembled from parts that do not know
    ///         each other. Here the two sides are compiled together against one another, so a name
    ///         that matches nothing is a typo the author can fix — and the two silent alternatives are
    ///         both worse than an exception: dropping it makes a panel come up missing a section, and
    ///         defaulting it puts the footer at the top of the body, which reads as a layout bug in the
    ///         component rather than a misspelling in the consumer. The message names the slots the
    ///         component does declare, which is the same bargain an unknown <c>on:</c> event is
    ///         emitted under.
    ///     </para>
    /// </remarks>
    public static UiElement Into(Component component, string name) {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(name);

        if (component.Slots?.TryGetValue(name, out var slot) == true) {
            return slot;
        }

        var declared = component.Slots is { Count: > 0 } slots
            ? string.Join(", ", slots.Keys.Order(StringComparer.Ordinal))
            : "none";

        throw new InvalidOperationException(
            $"'{component.GetType().Name}' declares no slot named '{name}'. It declares: {declared}."
        );
    }

    /// <inheritdoc cref="Into(Component, string)" />
    /// <param name="element">The control the content was written inside.</param>
    /// <param name="name">The slot's name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Present so that the failure is about slots rather than about overload
    ///         resolution.</b> A capitalised tag names a component or a control and the emitter
    ///         cannot tell which — the two <see cref="Inner(Component)" /> overloads exist for
    ///         exactly that reason. Without this one, <c>slot="footer"</c> inside a
    ///         <c>&lt;ScrollView&gt;</c> would be a Roslyn error about converting a
    ///         <see cref="UiElement" /> to a <see cref="Component" />, on generated code the author
    ///         never wrote, and the fix would not be visible in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This used to say a control has no slots and never will, and that was wrong.</b>
    ///         A control has exactly as many places as it has <i>parts</i>, and
    ///         <see cref="UiElement.ContentHost" /> can name one of them: an <c>Expander</c> is a
    ///         header and a body, so markup could fill the body and had no word for the header at
    ///         all. <see cref="UiElement.NamedHost" /> is the control-side answer, and a control
    ///         that overrides nothing still lands here with the message this method was written for.
    ///     </para>
    /// </remarks>
    public static UiElement Into(UiElement element, string name) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(name);

        if (element.NamedHost(name) is { } host) {
            return host;
        }

        throw new InvalidOperationException(
            $"'{element.Tag}' publishes no slot named '{name}'. A control's named slots come from "
            + "'UiElement.NamedHost'; drop the 'slot' attribute to write into its content."
        );
    }

    // ================================================================== Control flow

    /// <summary>Builds whichever arm a selector picks, and rebuilds when it picks another.</summary>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="arm">Which arm is live, or a negative number for none.</param>
    /// <param name="build">Builds the arm it is given.</param>
    /// <remarks>
    ///     ⚠ <b><c>@if</c> and <c>@switch</c> are the same thing here.</b> They differ only in how
    ///     the arm is chosen, and giving the runtime two constructs for swapping a subtree in and
    ///     out would mean two places to get the disposal of a branch's effects wrong.
    /// </remarks>
    /// <remarks>
    ///     ⚠ <b>An arm inside a <c>@for</c> row builds under that row's iteration key, and getting
    ///     that wrong was a silent defect until 2026-08-23.</b> <see cref="For{T}" /> sets
    ///     <c>iteration</c> around the <i>synchronous</i> build of a new region and restores it in a
    ///     <c>finally</c>; this registers its own <see cref="Bind(Action)" />, which the scheduler
    ///     runs later — so a <c>refs</c> in an arm found no key and threw the "only meaningful inside
    ///     an @for" message that <see cref="Refs{TElement}" />'s own remark says nothing generated can
    ///     reach. It reached it, and what it looked like was not an exception in anybody's face: the
    ///     arm's builder was abandoned at the throw, so the element on the line above the <c>refs</c>
    ///     survived with no classes, no bindings and no children while every other panel on the
    ///     screen was correct. Capturing the key at registration and restoring it round the build is
    ///     the same bargain <see cref="For{T}" /> already makes for a nested loop, and for the same
    ///     reason: an arm inside a row belongs to the row.
    /// </remarks>
    public void Switch(UiElement? parent, Func<int> arm, Action<BuildContext, UiElement, int> build) {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(build);

        var target = parent ?? Anchor;
        var region = Open(target);
        var current = int.MinValue;

        // Whatever loop row this arm was declared in. Captured here, because here is the only moment
        // it is still on the context: the effect below runs after `For` has put the outer value back.
        var declared = iteration;

        Bind(() => {
            var next = arm();
            if (next == current) {
                return;
            }

            current = next;
            region.Clear();

            if (next < 0) {
                return;
            }

            var outer = iteration;

            iteration = declared;
            try {
                In(target, region, () => build(this, target, next));
            } finally {
                iteration = outer;
            }

            region.Reposition();
        });
    }

    /// <summary>Builds one subtree per item, keyed, and reconciles when the sequence changes.</summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="parent">Its parent, or null for the mount point.</param>
    /// <param name="items">The sequence.</param>
    /// <param name="key">What identifies an item across changes.</param>
    /// <param name="build">Builds one item.</param>
    /// <remarks>
    ///     <para>
    ///         An item whose key is still there keeps its elements — and therefore its focus, its
    ///         scroll offset and its animation state. That is the whole reason keys exist, and why a
    ///         missing one is a warning at compile time rather than a silent fallback to the index.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reordering moves every surviving item rather than a minimal set.</b> A move that
    ///         does not change an element's index returns immediately, so an unchanged list costs a
    ///         walk and nothing else; a rotation costs one move per item where a
    ///         longest-increasing-subsequence pass would cost far fewer. Owed, and the honest
    ///         statement is that this is correct and not yet minimal.
    ///     </para>
    /// </remarks>
    /// <param name="exit">How long a removed row stays on screen, or null to remove it at once.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><paramref name="exit" /> defaults to null, and that is opt-in on purpose.</b>
    ///         Deferring every removal would change what "the row is gone" means for every caller in
    ///         the tree, and the ones it would surprise are the tests: a list that removes an item
    ///         and asserts on its children in the same breath is correct today and would be reading
    ///         a document still holding a row nobody asked for.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a leaving row keeps its place in the order, which is what makes a fade look
    ///         like one.</b> It stays in the region's slot list, so the rows below it are positioned
    ///         after it and it shrinks or fades where it stood rather than jumping to the end of the
    ///         list to die. Where it lands is decided by walking back through the previous order for
    ///         the nearest row that is still there; an index remembered from the old order would put
    ///         it in the wrong place the moment anything else moved.
    ///     </para>
    /// </remarks>
    public void For<T>(
        UiElement? parent,
        Func<IEnumerable<T>> items,
        Func<T, object> key,
        Action<BuildContext, UiElement, T> build,
        ExitSpec? exit = null
    ) {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(build);

        var target = parent ?? Anchor;
        var region = Open(target);
        var live = new Dictionary<object, Region>();
        var leaving = new Dictionary<object, Region>();

        // Every row this region holds, live and leaving together and in the order they are drawn in.
        var order = new List<Region>();
        var reconciling = false;

        void Settle() {
            // The chain has to be rewritten before anything is repositioned: a region's index comes
            // from what it follows, and after a reorder that is a different region than it was.
            object? predecessor = null;
            foreach (var item in order) {
                item.Rebind(predecessor);
                predecessor = item;
            }

            region.Reorder(order);
            region.Reposition();
        }

        void Ended(object identity, Region ended) {
            leaving.Remove(identity);
            order.Remove(ended);

            // Nothing to settle from inside a reconciliation: the walk below rebuilds the whole
            // order and repositions once, and doing it twice is only slower.
            if (!reconciling) {
                Settle();
            }
        }

        Bind(() => {
            var wanted = new List<Region>();
            var kept = new Dictionary<object, Region>();

            reconciling = true;
            try {
                foreach (var item in items()) {
                    var identity = key(item);

                    if (live.Remove(identity, out var existing)) {
                        kept[identity] = existing;
                        wanted.Add(existing);
                        continue;
                    }

                    // ⚠ A key that comes back before its old row has finished leaving ends that row
                    // now. Reviving it is not available — `Region.Leave` disposed its bindings — and
                    // letting both stand is the one failure an exit can introduce that the rest of
                    // the runtime has no defence against: two subtrees under one identity, with
                    // `refs` and the keyed effect table pointing at whichever was written last.
                    if (leaving.Remove(identity, out var returning)) {
                        order.Remove(returning);
                        returning.Finish();
                    }

                    var created = new Region(target, null, region);
                    var captured = item;
                    var outer = iteration;

                    // ⚠ Saved and restored rather than set and cleared: an `@for` inside an `@for`
                    // body builds while the outer row is still the one a `refs` on an outer element
                    // belongs to, and a nested loop that cleared this on the way out would give the
                    // rest of the outer row no iteration at all.
                    iteration = identity;
                    try {
                        In(target, created, () => build(this, target, captured));
                    } finally {
                        iteration = outer;
                    }

                    kept[identity] = created;
                    wanted.Add(created);
                }

                // Whatever is left in `live` is what the new sequence does not contain.
                foreach (var (identity, gone) in live) {
                    if (exit is null) {
                        gone.Clear();
                        continue;
                    }

                    var captured = identity;
                    var row = gone;

                    leaving[identity] = row;
                    row.Leave(exit, () => Ended(captured, row));
                }

                live.Clear();
                foreach (var (identity, item) in kept) {
                    live[identity] = item;
                }

                var previous = new List<Region>(order);

                order.Clear();
                order.AddRange(wanted);

                foreach (var stale in previous) {
                    if (!stale.IsLeaving || order.Contains(stale)) {
                        continue;
                    }

                    order.Insert(After(previous, order, stale), stale);
                }
            } finally {
                reconciling = false;
            }

            Settle();
        });
    }

    /// <summary>Where a leaving row goes in the new order: after the nearest row still in it.</summary>
    static int After(List<Region> previous, List<Region> order, Region stale) {
        for (var i = previous.IndexOf(stale) - 1; i >= 0; i--) {
            var index = order.IndexOf(previous[i]);

            if (index >= 0) {
                return index + 1;
            }
        }

        return 0;
    }

    // ================================================================== Regions

    /// <summary>Stops a component's own build, and tells it so.</summary>
    /// <param name="host">The element it drew itself into.</param>
    /// <remarks>
    ///     ⚠ <b>The hook runs first, while the component's elements are still there.</b> That is
    ///     what makes it useful — a panel saving a scroll offset or a selection has something to
    ///     read — and it is what every other framework's unmount does. Its effects are disposed
    ///     immediately afterwards, so an <c>OnUnmounted</c> that writes a signal cannot leave
    ///     anything running.
    ///
    ///     ⚠ <b>It stops rather than clears, because a component's elements are all under its host
    ///     and the host is somebody else's to remove.</b> Whichever of the two things that can end a
    ///     component got here — the enclosing region clearing, or the document removing the host —
    ///     is already taking the host out, and removing the subtree a second time from underneath
    ///     would be one nested <c>Document.Remove</c> per element inside the walk that is removing
    ///     them. Same bargain <c>Compose</c> makes; see <c>Region.Stop</c>.
    ///
    ///     ⚠ And the region is fetched rather than captured: <see cref="Rebuild" /> replaces a
    ///     component's region with a new one, so a closure holding the old object would stop
    ///     something nothing is in.
    /// </remarks>
    void Ended(UiElement host) {
        Document.ComponentAt(host)?.Unmount();
        Rooted(host).Stop();
    }

    /// <summary>What ends a component, whichever of the two ways it ends reaches it first.</summary>
    /// <param name="host">The element it drew itself into.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two, because a component can leave the tree without any region hearing about
    ///         it.</b> A component built inside a branch is ended by the region that built it, which
    ///         is what puts its effects away before the branch's elements go. A component built onto
    ///         a mount — which is every markup panel in the editor, and everything
    ///         <see cref="BuildInto" /> makes — has no such region above it, and used to go on
    ///         running for the life of the document after its host was removed. An inspector rebuilt
    ///         on every selection change leaked a panel's worth of effects each time.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One object registered twice rather than two teardowns, because
    ///         <see cref="Unsubscribe" /> is spent by its first call.</b> The orders differ — the
    ///         region disposes it before it removes the host, the document while announcing the
    ///         removal — and both are correct; what must not happen is <c>OnUnmounted</c> twice.
    ///     </para>
    /// </remarks>
    Unsubscribe Teardown(UiElement host) {
        var teardown = new Unsubscribe(() => Ended(host));
        Document.TearsDownAt(host, teardown);
        return teardown;
    }

    /// <summary>The region a parent's content goes into, opening one the first time it is asked.</summary>
    /// <remarks>
    ///     ⚠ <b>A region made here is linked into the region being built, and that is what makes a
    ///     teardown reach it.</b> A region hangs off the element its content has as a <i>parent</i>,
    ///     so an <c>@for</c> written inside a nested <c>&lt;div&gt;</c> opens against that div —
    ///     which is a different key in this table from the one the enclosing branch or component
    ///     builds into. Nothing above it pointed at it, so clearing the enclosing branch removed the
    ///     div and left every row's effects running: reading signals, assigning to elements that had
    ///     left the document, and holding them alive through their closures.
    ///
    ///     ⚠ <b><see cref="building" /> is the right owner because the element was created into it
    ///     or into something outliving it.</b> Markup nests, so the innermost control flow live when
    ///     a parent is first built into is the one whose end is also that content's end. Hand-written
    ///     C# can break that — build into an element from inside a branch that the element outlives —
    ///     and gets a subtree cleared with the branch, which is the reading of the code as written.
    /// </remarks>
    Region RegionOf(UiElement parent) {
        if (regions.TryGetValue(parent, out var existing)) {
            return existing;
        }

        var region = Rooted(parent);
        building.Link(region);
        return region;
    }

    /// <summary>The same, for a parent whose region something other than a region ends.</summary>
    /// <remarks>
    ///     ⚠ <b>A component's host, and the mount.</b> Both are ended by a
    ///     <see cref="Unsubscribe" /> held elsewhere — see <see cref="Child{T}(UiElement)" /> — so linking them
    ///     into whatever happened to be building would give them a second owner and, across a
    ///     <see cref="Rebuild" />, a new link on the enclosing region for every reload.
    /// </remarks>
    Region Rooted(UiElement parent) {
        if (regions.TryGetValue(parent, out var existing)) {
            return existing;
        }

        var region = new Region(parent, null, forget: () => regions.Remove(parent));
        regions[parent] = region;
        return region;
    }

    /// <summary>Opens a sub-region after whatever is currently last in its parent.</summary>
    Region Open(UiElement parent) {
        var host = RegionOf(parent);
        var region = new Region(parent, host.Last, host);
        host.Add(region);
        return region;
    }

    /// <summary>Runs a build with a region as the destination for that parent's content.</summary>
    void In(UiElement parent, Region region, Action build) {
        regions.TryGetValue(parent, out var previousRegion);
        var previousBuilding = building;

        regions[parent] = region;
        building = region;

        try {
            build();
        } finally {
            if (previousRegion is null) {
                regions.Remove(parent);
            } else {
                regions[parent] = previousRegion;
            }

            building = previousBuilding;
        }
    }

    // ================================================================== Helpers

    void SetClasses(UiElement target, string value) {
        var wanted = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Only what this attribute put there last time comes off. Anything else on the element was
        // put there by somebody else — the scope class, or the control itself — and is not this
        // binding's to remove. See `Attribute`.
        if (classes.TryGetValue(target, out var previous)) {
            foreach (var stale in previous) {
                if (!wanted.Contains(stale, StringComparer.Ordinal)) {
                    target.RemoveClass(stale);
                }
            }
        }

        foreach (var className in wanted) {
            target.AddClass(className);
        }

        classes.AddOrUpdate(target, wanted);
    }

    /// <summary>Writes a <c>style="…"</c> attribute's declarations onto an element.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Only the properties this attribute wrote last time come off</b>, which is
    ///         <see cref="SetClasses" />'s rule and it is here for a sharper reason. A control writes
    ///         its own inline declarations — a <c>DataGrid</c> row's <c>top</c>, a <c>Selects</c>
    ///         popup's <c>min-width</c>, a <c>DockingHost</c> pane's <c>flex-grow</c> — and it writes
    ///         them from its own code, after the markup has been applied. Treating the attribute as
    ///         the element's complete inline set would delete those, and a
    ///         <c>&lt;DataGrid style="height: 40%" /&gt;</c> would silently unposition every row in it.
    ///     </para>
    ///     <para>
    ///         The text is remembered as well as the names, so re-evaluating a binding to the value it
    ///         already had costs one string compare and no parse. That matters: this runs from an
    ///         effect, and the parse is a real one — see
    ///         <c>StyleSheetLoader.ReadDeclarations</c>, which goes through ExCSS so that
    ///         <c>style="padding: 4px"</c> means what <c>padding: 4px</c> means in a rule. A caller
    ///         moving one number sixty times a second is better served by
    ///         <see cref="UiElement.SetStyle(string, string?)" /> directly, which interns a value and
    ///         allocates nothing.
    ///     </para>
    /// </remarks>
    void SetInlineStyle(UiElement target, string value) {
        if (styleText.TryGetValue(target, out var last) && string.Equals(last, value, StringComparison.Ordinal)) {
            return;
        }

        Document.Styles.Loader.ReadDeclarations(value, styleScratch);

        if (styles.TryGetValue(target, out var previous)) {
            foreach (var stale in previous) {
                if (!styleScratch.Exists(written => string.Equals(written.Property, stale, StringComparison.Ordinal))) {
                    target.SetStyle(stale, null);
                }
            }
        }

        var names = new string[styleScratch.Count];

        for (var i = 0; i < styleScratch.Count; i++) {
            var (property, declared, important) = styleScratch[i];
            target.SetStyle(property, declared, important);
            names[i] = property;
        }

        styles.AddOrUpdate(target, names);
        styleText.AddOrUpdate(target, value);
    }

    static string Format(object? value) =>
        value switch {
            null => string.Empty,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    sealed class Unsubscribe(Action undo) : IDisposable {
        Action? undo = undo;

        public void Dispose() {
            undo?.Invoke();
            undo = null;
        }
    }
}
