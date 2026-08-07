// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Vixen.Ui.Reactive;

namespace Vixen.Ui.Composition;

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
    static readonly ConcurrentDictionary<string, Action<UiElement, Action<UiEvent>, RoutingStrategy>> Subscriptions =
        new(StringComparer.Ordinal) {
            ["tap"] = (element, handler, strategy) =>
                element.AddHandler<TapEvent>((_, args) => handler(args), strategy),
            ["click"] = (element, handler, strategy) =>
                element.AddHandler<TapEvent>((_, args) => handler(args), strategy),
            ["dblclick"] = (element, handler, strategy) =>
                element.AddHandler<TapEvent>((_, args) => { if (args.Count >= 2) { handler(args); } }, strategy),
            ["longpress"] = (element, handler, strategy) =>
                element.AddHandler<LongPressEvent>((_, args) => handler(args), strategy),
            ["pointerdown"] = (element, handler, strategy) =>
                element.AddHandler<PointerEvent>(
                    (_, args) => { if (args.Action == PointerAction.Pressed) { handler(args); } },
                    strategy
                ),
            ["pointerup"] = (element, handler, strategy) =>
                element.AddHandler<PointerEvent>(
                    (_, args) => { if (args.Action == PointerAction.Released) { handler(args); } },
                    strategy
                ),
            ["pointermove"] = (element, handler, strategy) =>
                element.AddHandler<PointerEvent>(
                    (_, args) => { if (args.Action == PointerAction.Moved) { handler(args); } },
                    strategy
                ),
            ["dragstart"] = (element, handler, strategy) =>
                element.AddHandler<DragEvent>(
                    (_, args) => { if (args.Stage == DragStage.Started) { handler(args); } },
                    strategy
                ),
            ["drag"] = (element, handler, strategy) =>
                element.AddHandler<DragEvent>(
                    (_, args) => { if (args.Stage == DragStage.Moved) { handler(args); } },
                    strategy
                ),
            ["dragend"] = (element, handler, strategy) =>
                element.AddHandler<DragEvent>(
                    (_, args) => { if (args.Stage is DragStage.Completed or DragStage.Cancelled) { handler(args); } },
                    strategy
                )
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

    /// <summary>Where subscriptions go, so that clearing a branch stops everything inside it.</summary>
    Region building;

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
    ///     is composing one. Never both: <see cref="Child{T}" /> saves and restores
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
    ///         library already follows for a control built by hand.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An element is not entered, and a component is.</b> A component's markup builds
    ///         into its host and belongs to it, so <see cref="Anchor" /> and the scope move; a
    ///         control builds its own parts in <c>OnCreated</c> and what the markup wrote inside its
    ///         tag is the caller's content, projected in. Moving the anchor for a control would make
    ///         its stylesheet's scope class land on elements the caller wrote.
    ///     </para>
    /// </remarks>
    public T Child<T>(UiElement? parent) where T : IComposable, new() {
        var created = new T();

        switch (created) {
            case UiElement element: {
                var target = parent ?? Anchor;
                Document.Adopt(element, null, target);
                RegionOf(target).Add(element);

                if (Scope is { } elementScope) {
                    element.AddClass(elementScope);
                }

                return created;
            }

            case Component component: {
                var host = Element(parent, component.TagName);

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

                var previousOwner = owner;
                var previousAnchor = Anchor;
                var previousBuilding = building;

                owner = component;
                Anchor = host;
                building = Rooted(host);

                try {
                    component.Mount(this, host);
                } finally {
                    owner = previousOwner;
                    Anchor = previousAnchor;
                    building = previousBuilding;
                }

                return created;
            }

            default:
                throw new InvalidOperationException(
                    $"'{typeof(T).Name}' is neither a component nor an element, so it cannot be a tag."
                );
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
    ///     How to subscribe: given the element, what to call, and which leg of the route to listen
    ///     on. The handler it is given already applies the <c>on:</c> modifiers.
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
    public static void Subscribe(string name, Action<UiElement, Action<UiEvent>, RoutingStrategy> subscribe) {
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
    ///         <c>class</c> is the one name handled specially, because it is a set rather than a
    ///         value: writing it replaces what the <i>last write</i> put there rather than appending
    ///         to it, which is what makes <c>class="btn @variant"</c> behave when <c>variant</c>
    ///         changes.
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
    ///     ⚠ <b><typeparamref name="TEvent" /> is a filter, not the subscription.</b> What is
    ///     subscribed to is decided by the name's entry in the table — which for <c>click</c> is a
    ///     different event type depending on whether the target is a control — and an argument of
    ///     another type is dropped. So the default <see cref="UiEvent" /> is what markup emits, and
    ///     a handler that narrows is asking for a subset of what the name delivers.
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
        var strategy = modifiers.Contains("capture", StringComparer.Ordinal)
            ? RoutingStrategy.Capture
            : RoutingStrategy.Bubble;

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

        subscribe(target, Invoke, strategy);
    }

    /// <summary>Binds a property in both directions.</summary>
    /// <typeparam name="T">The property's type.</typeparam>
    /// <param name="target">The element.</param>
    /// <param name="name">The property's name.</param>
    /// <param name="get">Reads the source.</param>
    /// <param name="set">Writes it back.</param>
    /// <exception cref="ArgumentException">The element has no such property.</exception>
    /// <remarks>
    ///     The write-back arrives through <see cref="UiElement.PropertyChanged" /> rather than
    ///     through a poll, and is guarded so that the assignment this binding just made does not
    ///     come straight back as a change to write to the source — which would be a loop the effect
    ///     scheduler's runaway detector would catch, several frames after the cause.
    /// </remarks>
    public void TwoWay<T>(UiElement target, string name, Func<T> get, Action<T> set) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        if (!UiPropertyRegistry.TryFindFor(target, name, out var key)) {
            throw new ArgumentException($"'{target.Tag}' has no property called '{name}'.", nameof(name));
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

        void Changed(UiElement _, UiPropertyKey changed) {
            if (writing || !ReferenceEquals(changed, key)) {
                return;
            }

            set((T)key.GetValue(target)!);
        }

        target.PropertyChanged += Changed;
        building.Track(new Unsubscribe(() => target.PropertyChanged -= Changed));
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
    public void Switch(UiElement? parent, Func<int> arm, Action<BuildContext, UiElement, int> build) {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(build);

        var target = parent ?? Anchor;
        var region = Open(target);
        var current = int.MinValue;

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

            In(target, region, () => build(this, target, next));
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
    public void For<T>(
        UiElement? parent,
        Func<IEnumerable<T>> items,
        Func<T, object> key,
        Action<BuildContext, UiElement, T> build
    ) {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(build);

        var target = parent ?? Anchor;
        var region = Open(target);
        var live = new Dictionary<object, Region>();

        Bind(() => {
            var wanted = new List<Region>();
            var kept = new Dictionary<object, Region>();

            foreach (var item in items()) {
                var identity = key(item);

                if (live.Remove(identity, out var existing)) {
                    kept[identity] = existing;
                    wanted.Add(existing);
                    continue;
                }

                var created = new Region(target, null, region);
                var captured = item;
                In(target, created, () => build(this, target, captured));

                kept[identity] = created;
                wanted.Add(created);
            }

            // Whatever is left in `live` is what the new sequence does not contain.
            foreach (var gone in live.Values) {
                gone.Clear();
            }

            live.Clear();
            foreach (var (identity, item) in kept) {
                live[identity] = item;
            }

            // The chain has to be rewritten before anything is repositioned: a region's index comes
            // from what it follows, and after a reorder that is a different region than it was.
            object? predecessor = null;
            foreach (var item in wanted) {
                item.Rebind(predecessor);
                predecessor = item;
            }

            region.Reorder(wanted);
            region.Reposition();
        });
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
    ///     <see cref="Unsubscribe" /> held elsewhere — see <see cref="Child{T}" /> — so linking them
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
