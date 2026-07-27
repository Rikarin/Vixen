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
public abstract class Component {
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

    /// <summary>The component's own stylesheet, if it declared a <c>&lt;style&gt;</c> block.</summary>
    protected virtual string? Style => null;

    /// <summary>Whether that stylesheet was marked <c>scoped</c>.</summary>
    protected virtual bool StyleIsScoped => false;

    /// <summary>Builds this component's elements.</summary>
    /// <param name="ctx">What to build with.</param>
    protected abstract void Build(BuildContext ctx);

    /// <summary>The slots this component declared, by name.</summary>
    internal Dictionary<string, UiElement>? Slots { get; private set; }

    internal void Mount(BuildContext ctx, UiElement root) {
        Root = root;
        Content = root;

        Build(ctx);

        if (Slots?.TryGetValue(BuildContext.DefaultSlot, out var slot) == true) {
            Content = slot;
        }

        if (Style is { } css) {
            // ⚠ Loaded once per component *type* would be right and this is once per instance,
            // which reloads the same rules for every row of a list. The loader interns and the
            // cascade dedupes, so it is correct and wasteful rather than wrong; keyed by type is
            // owed. `scoped` is parsed and carried but not yet applied — there is no scope
            // attribute to select on, so saying so is better than pretending.
            root.Document.Load(css);
        }
    }

    internal void Declare(string name, UiElement host) {
        Slots ??= new(StringComparer.Ordinal);
        Slots[name] = host;
    }
}
