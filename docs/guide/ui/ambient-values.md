---
title: Ambient values
slug: ui/ambient-values
kind: guide
area: Core
summary: A value provided on an element and found by type from anything inside it — SwiftUI's Environment with the walk written down — so a theme, a selection or a view-model stops being threaded through props by hand.
api: [T:Vixen.Ui.UiElement, T:Vixen.Ui.UiDocument]
tags: [ui, markup, components, composition]
since: 0.2
status: preview
related: [ui/markup-panels, ui/commands, ui/documents]
---

## What it is

`element.Provide<T>(value)` puts a value on an element. `element.Inject<T>()` finds the nearest one
on the way up — this element, then its ancestors, then the document.

```csharp no-compile="a fragment; `panel` and `leaf` are UiElements in a document"
document.Provide<ITheme>(new DarkTheme());   // the application's
panel.Provide<ITheme>(new PreviewTheme());   // this subtree's

leaf.Inject<ITheme>();                       // the preview one
panel.Unprovide<ITheme>();                   // reveals the application's again
```

Every cross-cutting value in this framework was threaded through props by hand before this —
`Samples/02-HelloUi/Shell.vxml` repeats `Model="@Model"` on three panels in a row, and each panel
that gains a child gains another copy of the line.

⚠ **The key is the type argument, not the value's runtime type.** `Provide<ITheme>(new DarkTheme())`
is found by `Inject<ITheme>` and not by `Inject<DarkTheme>`. That is what makes an interface the
useful key and what stops a subclass silently shadowing the base everything else asks for.

⚠ **This is not `[UiProperty(Inherits = true)]` and not the cascade.** That attribute's generated
walk tests `ancestor is TOwner`, so it inherits down one *kind* of element and is CSS inheritance
wearing a C# name — its only producers in the whole tree are three test fixtures. Nothing that ships
inherits a property at all.

⚠ **Walked on every ask rather than cached**, which is `FindUndoManager`'s rule and
`FindEditedDocument`'s: an element is reparented, a panel is torn off into its own window, and a
cached answer is the one that was nearest when the control was built. All three are deliberately the
same walk — nearest declaration wins, and the document is the last word.

## What it is for

Cross-cutting values that every level of a tree may need and no level should have to thread through
the ones between — a theme, a selection, a service. ⚠ **The nearest one wins**, which is the whole
point: a preview pane provides its own theme and everything inside it sees that, while the rest of
the application sees the document's, and neither has to know about the other.

## Using it

## From a component

```csharp no-compile="a fragment; the code-behind half of a .vxml component"
partial class Workspace {
    public Project Project { get; set; } = Project.Empty;

    protected override void OnProvide() => Provide<ISelection>(new Selection(Project));
}

partial class Inspector {
    ISelection Selection => Inject<ISelection>() ?? Selection.None;
}
```

```xml no-compile="a fragment"
<inspector-name>@Selection.Name</inspector-name>
```

A provider overrides `OnProvide` in its code-behind; a consumer exposes a property that injects, and
the markup reads it as it reads any other expression.

### `<provide>`, for a value the markup already has

A component that is providing something it can name in an expression writes it as a tag instead:

```xml no-compile="a fragment"
<workspace>
    <provide type="ISelection" value="@Selection" />
    <Inspector />
    <Timeline />
</workspace>
```

That becomes `workspace.Provide<ISelection>(Selection)` — on the element the tag is *written in*, not
on the component's own root, so a sibling of `<workspace>` does not see it.

⚠ **The key is written out because it cannot be inferred, and that is a fact about `Provide<T>` and
not about the tag.** The runtime keys on the type argument so that an interface is the useful key and
a subclass cannot shadow its base; an inferred key would be the concrete class every time, and
`Inject<ISelection>` would find nothing. The binder could not infer it in any case — it never touches
the compilation, which is what keeps a C# edit from re-running the markup generator.

⚠ **Document order is the rule.** The emitter writes nodes in the order they appear, so a component
written *above* the `<provide>` is built before the value exists and injects null. That is the same
reading an author already has of every other tag, and it is why the conventional place for a
`<provide>` is the first line inside its element.

`OnProvide` is still the hook to use when the value has to be *computed* — it runs after this
component's parameters are assigned and before any child of it exists, which no tag position can
express.

⚠ **`OnProvide` is the one hook that runs after this component's parameters are assigned and before
any child of it exists**, and both halves matter. Earlier — at attach time — and the value would be
computed from a parameter still at its default, which looks correct from C# (where a caller sets
properties before mounting) and is wrong from every `.vxml`, where the emitter's `Create` …
assignments … `Compose` is what sets them. Later — after `Build` — and the children that were meant
to inject it have already been built.

⚠ **It is a hook rather than the first line of `Build` because a markup component has no first line
to write in.** The generator owns the whole method, so a runtime reachable only from inside `Build`
would be reachable from hand-written components and from no markup at all.

**`Component.Inject` is answerable from inside `Build`** because `Root` is already parented by then:
`BuildContext.Create` makes the host element and attaches it before `Compose` runs the build.

## Examples

**A preview that overrides one value for its subtree.** `Unprovide` reveals what was underneath
rather than clearing it, so the application's theme is never lost:

```csharp no-compile="a fragment; `document` and `preview` are the caller's own"
document.Provide<ITheme>(new DarkTheme());
preview.Provide<ITheme>(new PreviewTheme());

preview.Inject<ITheme>();     // the preview one
preview.Unprovide<ITheme>();
preview.Inject<ITheme>();     // the document's again
```

**A leaf that must work with nothing provided.** `Inject` answering null is an ordinary case, not an
error — a control written to fall back is a control that can be dropped into any tree:

```csharp no-compile="a fragment; the code-behind of a component"
ISelection Selection => Inject<ISelection>() ?? Selection.None;
```

## What is deliberately not here yet

**A typed `@inject` directive.** `<provide>` exists; its mirror does not. `@inject ITheme Theme` at
the top of a `.vxml` would generate the property a consumer writes by hand today — which is one line
of code-behind, not a missing capability, and unlike `<provide>` it needs a new directive in the
lexer and the parser rather than a branch in the binder and the emitter. Worth doing when a file
injects three values rather than one.

**Three diagnostics and no fourth.** `VXML2021`, `VXML2022` and `VXML2023` are about the tag's own
shape. Nothing checks that the `type` names a real type, or that anything ever provides what a
consumer injects — the first is Roslyn's, reported at the `type` attribute through the `#line`, and
the second is not decidable at compile time in a tree that is assembled at run time.

**No reactivity of its own.** An ambient value is read where it is read; a value that has to *change*
is a signal put into the ambient slot, and the consumer's `@expr` subscribes to it as it would to
any other. Providing a new object does not invalidate anything that already read the old one.

## See also

- [Markup panels](../ui/markup-panels.md) — `OnProvide` is the component seam these are written from.
- [Commands and the responder chain](../ui/commands.md) — the other walk up the tree, and why the two
  answer different questions about the same ancestors.
- [Documents](../ui/documents.md) — the document is the last provider on the way up, which is what
  makes it the right place for an application-wide value.
