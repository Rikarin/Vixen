// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Composition;

/// <summary>
///     A reusable piece of interface: some state, and a <see cref="Build" /> that turns it into
///     elements.
/// </summary>
/// <remarks>
///     <para>
///         What a <c>.vxml</c> compiles into. <c>Vixen.Ui.Markup</c> emits a partial class deriving
///         from this whose <see cref="Build" /> is one statement per element and one effect per
///         dynamic expression — so writing a component by hand and generating one produce the same
///         thing, and the generated half is steppable rather than magic (ADR-002).
///     </para>
///     <para>
///         <see cref="Build" /> runs <b>once</b>. That is the whole of ADR-010: there is no render
///         function to call again, no virtual DOM to diff, and no reason to walk a tree that did not
///         change. What changes later changes because an effect ran, and an effect assigns exactly
///         the property it was written for.
///     </para>
/// </remarks>
public abstract class Component : IComposable {
    /// <summary>The element this component drew itself into.</summary>
    /// <remarks>
    ///     Its host, not its first element. A <c>class</c> written on the component's tag styles
    ///     this, which is why it exists as a distinct thing from whatever the component built.
    /// </remarks>
    public UiElement Root { get; private set; } = null!;

    /// <summary>Where a caller's children are projected.</summary>
    /// <remarks>
    ///     The component's default <c>&lt;slot /&gt;</c> if it declared one, and its root otherwise —
    ///     so a component that never thought about projection still accepts children, and one that
    ///     did decides where they go.
    /// </remarks>
    public UiElement Content { get; private set; } = null!;

    /// <summary>The element name this component's host answers to.</summary>
    /// <remarks>
    ///     <para>
    ///         The type's name in lower case, so <c>&lt;Callout /&gt;</c> is styled by
    ///         <c>callout { … }</c> — a component is a kind of element as far as a stylesheet is
    ///         concerned, and CSS type selectors are lower case.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Overridable for the same reason <see cref="UiElement.TagName" /> is.</b> A
    ///         default taken from the type's name cannot produce a hyphen, and every compound tag in
    ///         the control library and the editor's stylesheets has one — so a component called
    ///         <c>TaskCentre</c> would silently answer to <c>taskcentre</c> and no rule written for
    ///         it would match. In markup the override is three lines of <c>@code</c>, which is where
    ///         a component's C# belongs anyway.
    ///     </para>
    /// </remarks>
    protected internal virtual string TagName => GetType().Name.ToLowerInvariant();

    /// <summary>The component's own stylesheet, if it declared a <c>&lt;style&gt;</c> block.</summary>
    protected virtual string? Style => null;

    /// <summary>Whether that stylesheet was marked <c>scoped</c>.</summary>
    protected virtual bool StyleIsScoped => false;

    /// <summary>Builds this component's elements.</summary>
    /// <param name="ctx">What to build with.</param>
    protected abstract void Build(BuildContext ctx);

    /// <summary>Called when the component leaves the tree, before its effects are disposed.</summary>
    /// <remarks>
    ///     <para>
    ///         Where a component gives back what the runtime did not give it. Effects, two-way
    ///         bindings and the elements themselves are the region's and are taken care of; a
    ///         handler hung on a model — <c>manager.Changed += …</c> — is the component's, and
    ///         nothing else knows it exists.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unmount is not a dispose.</b> The component object survives it: a hot reload
    ///         re-mounts the same instance so that its signals, and therefore most of what "state
    ///         was preserved" means, carry across. So this may run more than once, and
    ///         <see cref="Build" /> may follow it.
    ///     </para>
    /// </remarks>
    protected virtual void OnUnmounted() { }

    internal void Unmount() => OnUnmounted();

    /// <summary>The slots this component declared, by name.</summary>
    /// <remarks>
    ///     Cleared on every mount rather than added to. A reload re-runs <see cref="Build" /> on the
    ///     same instance, and a slot list that survived would still name the elements the previous
    ///     build made — which are gone.
    /// </remarks>
    internal Dictionary<string, UiElement>? Slots { get; private set; }

    /// <summary>The class this component's elements carry, or null when its styles are not scoped.</summary>
    /// <remarks>
    ///     Per <i>type</i>, because the stylesheet is: every instance shares one class, and a
    ///     per-instance one would mean a rule set per row of a list.
    /// </remarks>
    internal string? Scope => StyleIsScoped ? ScopedStyles.ScopeOf(GetType()) : null;

    internal void Mount(BuildContext ctx, UiElement root) {
        Attach(ctx, root);
        Compose(ctx);
    }

    /// <summary>Everything a mount does before <see cref="Build" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Split out so that a caller can assign the component's parameters in between.</b>
    ///     They used to be assigned after <c>Build</c> had run, which meant every effect the
    ///     component made had already read them at their defaults — see
    ///     <see cref="BuildContext.Create{T}(Vixen.Ui.UiElement,string)" />.
    /// </remarks>
    internal void Attach(BuildContext ctx, UiElement root) {
        Root = root;
        Content = root;
        Slots = null;

        // ⚠ Before `Build`, so that a component reachable from its host is reachable for the whole
        // of the build rather than only afterwards — and so that a component whose own markup asks
        // the document about an ancestor gets an answer.
        ctx.Document.Mounted(root, this);

        // ⚠ Before `Build`, because `BuildContext.Element` reads it for every element the component
        // makes — and after it the component's own elements would already exist unscoped.
        if (Scope is { } scope) {
            root.AddClass(scope);
        }
    }

    /// <summary>The rest of a mount: <see cref="Build" /> and what depends on what it made.</summary>
    internal void Compose(BuildContext ctx) {
        var root = Root;

        Build(ctx);

        if (Slots?.TryGetValue(BuildContext.DefaultSlot, out var slot) == true) {
            Content = slot;
        }

        if (Style is { } css) {
            // ⚠ **Once per type, not once per instance.** The loader interns and the cascade dedupes,
            // so loading it per instance was correct and wasteful — but "wasteful" here meant a rule
            // set per row of a list, which is a linear cost on the thing virtualisation exists to
            // make constant.
            root.Document.LoadOnce(GetType(), Scope is { } named ? ScopedStyles.Scope(css, named) : css);
        }
    }

    internal void Declare(string name, UiElement host) {
        Slots ??= new(StringComparer.Ordinal);
        Slots[name] = host;
    }
}
