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

**That is the whole markup spelling and it needs nothing new in the language.** A provider overrides
`OnProvide` in its code-behind; a consumer exposes a property that injects, and the markup reads it
as it reads any other expression.

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

**A `<provide value="@theme" />` tag and a typed `@inject` directive.** Both are sugar over what is
above and both are real work in the binder and the emitter; the runtime is reachable from markup
today without them, which is the difference between sugar and a missing feature.

**No reactivity of its own.** An ambient value is read where it is read; a value that has to *change*
is a signal put into the ambient slot, and the consumer's `@expr` subscribes to it as it would to
any other. Providing a new object does not invalidate anything that already read the old one.

## See also

- [Markup panels](../ui/markup-panels.md) — `OnProvide` is the component seam these are written from.
- [Commands and the responder chain](../ui/commands.md) — the other walk up the tree, and why the two
  answer different questions about the same ancestors.
- [Documents](../ui/documents.md) — the document is the last provider on the way up, which is what
  makes it the right place for an application-wide value.
