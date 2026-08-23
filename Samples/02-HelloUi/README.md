<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 02 — Hello UI

A small editor shell: a menu bar over a docking host, a virtualised tree in one panel, a property
grid in another, a gallery of the control set in a third, and toasts over the lot.

```bash
dotnet run --project Samples/02-HelloUi
```

**What it is a picture of is how you write a Vixen interface.** Markup for the tree, a stylesheet for
the tokens and the rules, utility classes for everything a class name can say, and a model made of
signals. That is the same three files a web project has, and none of it is a special case for being
a sample.

## The files, in the order to read them

| | |
|---|---|
| [`Shell.vxml`](Shell.vxml) | The window: a menu bar, a docking host, a toast host. **Start here.** |
| [`Panels/Gallery.vxml`](Panels/Gallery.vxml) | The control set, as tags. Every value is a two-way binding to a signal. |
| [`Panels/Hierarchy.vxml`](Panels/Hierarchy.vxml) | A thousand nodes, about thirty of them realised. |
| [`Panels/Inspector.vxml`](Panels/Inspector.vxml) | A property grid over an ordinary object. |
| [`Theme/vixen.ui.vcss`](Theme/vixen.ui.vcss) | The tokens. Change the accent here and it changes everywhere. |
| [`Theme/shell.vcss`](Theme/shell.vcss) | The rules a class name cannot say, and nothing else. |
| [`ShellModel.cs`](ShellModel.cs) | The state, as signals. |
| [`Program.cs`](Program.cs) | A thirty-line `Main` — what the window is called, and which component to mount — plus the sample's two development conveniences. |

`HelloUi.csproj` is worth a look for what is *not* in it. `<VixenUi>true</VixenUi>` is the whole of
the UI plumbing — the VXML compiler, the `[UiProperty]` generator, the `.vxml` and `.vcss` item types
and the utility-stylesheet step all arrive with it. Outside this repository it is not even a line: a
`PackageReference` brings all of it, because `Vixen.Ui` ships its MSBuild logic in `buildTransitive/`.

## What this sample proves, which is an absence

[Doc 02 § Samples](../../docs/plan/02-repository-layout.md) calls this one "Vixen.Ui only, no engine —
proves the UI/Engine boundary", and [doc 15](../../docs/plan/15-risks-and-open-questions.md) makes it
what proves the framework standalone before the editor is allowed to depend on it. So there is no
`Vixen.App` here — that assembly references `Vixen.Engine` — and `CheckArchitecture` fails the build
if anybody adds one.

⚠ **`Program.cs` used to be five hundred lines because of that rule.** Avoiding the engine's host
meant writing a Vulkan device, a swapchain, a render graph, an atlas upload, resize coalescing and a
suboptimal-present rule by hand. All of it is
[`Vixen.Ui.Desktop`](../../Platform/Vixen.Ui.Desktop/README.md) now — a `Platform/` assembly that is
a window, a device and four steps of a frame, with no scene, no ECS world and no game loop anywhere
in it. The boundary costs nothing to keep, and this file is a bootstrap again.

## The authoring loop

```bash
dotnet watch --project Samples/02-HelloUi
```

**All three of the engine's reload channels are wired here, and they are three different mechanisms.**

| Save a… | What happens | What survives |
|---|---|---|
| `.vcss` | A file watcher reloads the sheet. No rebuild. | Everything — element identity is untouched, so the focus, the scroll offset, the docking arrangement and the tree's place in its thousand rows all stay put. |
| `.vxml` | `dotnet watch` recompiles it into a new `Build`, the runtime patches the assembly, and `HotReloadHost` re-runs `Build` on the same component objects. | The components and their fields, so their signals — which is most of what "state was preserved" means. **Not** the elements: two `Build` bodies are two different programs. The focus is put back by path. |
| `.cs`, incompatibly | The component object is re-created too. | Nothing but what the sample re-applies itself. |

Measured on the middle row: a `.vxml` edit reloaded in **766 ms** with
`Channel = Markup, Components = 1, Succeeded = True` and no restart. The sample prints every report,
which is the only way to tell a rebuild that reloaded from one that could not — **a `Build` that
throws leaves the component empty**, and an empty panel and an unchanged panel look identical for the
second it takes to notice.

⚠ **The markup channel needs one line and does nothing without it.** `UiApplicationOptions.Mount`
is what puts the shell under a `HotReloadHost`; mounted the ordinary way through `Content`, the host
tracks nothing, `ReloadComponents` walks an empty list, and **it reports success over zero
components** — so a `.vxml` save does nothing at all with no diagnostic anywhere. That was this
sample's state until it was checked. `Content_alone_leaves_the_reload_host_with_nothing_to_do` in
`Vixen.Ui.Desktop.Tests` is that behaviour written down.

⚠ **A recreated component loses what this file assigned it.** `HotReloadHost` builds a fresh instance
through the parameterless constructor, so `Shell.Model` is not the `ShellModel` `Program.cs` holds
any more — which is why the sample re-applies it from `Reloaded`. An application whose components
take no parameters needs none of that.

⚠ **Changing a rule takes effect; *deleting* one does not, until the next build.** The sample prints
which of the two it got on start-up. `shell.vcss` is handed to the build as a `VixenStyleBase`, so
what the document holds is the generated sheet with this file concatenated into the front of it —
there is no separate sheet for `HotReloadWatcher.Load` to bind the path to, and it layers the file on
top instead. That is the right arrangement for shipping (it is what fixes the layer order and expands
`@apply`) and the wrong one for taking a rule out at run time. `HotReloadWatcher.Replaces` is the API
that says which you have, and this is what it is for.

⚠ **A shipping application drops all of it**: the `Vixen.Ui.HotReload` reference, `Mount`, and the
`Watch` method. `Content` is the shipping form of `Mount`. See that project's own file, which says
why it is neither trimmable nor AOT-compatible.

Try `--accent` by hand: change `--color-brand` in `Theme/vixen.ui.vcss`, and note that *that* one
needs a rebuild. Tokens are compiled into the utility sheet at build time; the rules in `shell.vcss`
are not.

`--frames N` runs exactly N frames and exits, which is how CI proves the whole stack starts, presents
and stops without a validation error or a hang — on a machine that may have no GPU at all, because
everything above the RHI runs whether or not a device was ever created. On the way out the sample
prints the docking arrangement, which is what an application would write to disk.

## Where the markup stops, and why

Everything in this sample is a nested tag except one thing, and that one cannot be:

**`TreeView`, because a `TreeNode` is not a `UiElement`.** It is a plain object in a model the
virtualiser flattens — which is the whole point of a virtualised tree, and the reason a thousand rows
cost about thirty elements. A `<TreeNode>` tag would have to be an element, and a tree whose nodes
were elements would be a tree that allocates a thousand of them. So `Panels/Hierarchy.vxml` builds
its model in `OnComposed`, and that is not friction: `@for` is the right answer for the five buttons
in the gallery and the wrong one for a thousand rows.

Everything else was friction and is fixed. `UiElement.OnChildAdded` is the seam: a container does its
registering there, and its `AddX` method is sugar over `Add<T>()` and a property or two, so both
routes arrive at the same state by the same code. Two of the controls needed a second signal as well,
because **a tag is created before its attributes are assigned** — so a container hears about a child
that does not yet know what it is:

| Control | What arriving could not say | What says it |
|---|---|---|
| `Select`, `ComboBox` | the option's value and label | `Popover.ContentAdded`, then the option's own `PropertyChanged` and `LabelChanged` |
| `DockingHost` | the panel's id | `DockPanel.Id`'s setter, which is what files it and places it |
| `RadioGroup` | the radio's value | restated on every arrival, and on `Value` |
| `MenuBar`, `Menu` | — | `OnChildAdded`, which also moves the menu to the root: every overlay is a root child |
| `Breadcrumb` | — | `OnChildAdded`, which inserts the chevron *before* the step that asked for it |
| `Accordion`, `Pagination` | — | nothing: they were never gaps. See below. |

⚠ **Two entries in this table used to claim `Accordion` and `Pagination` were gaps, and both were
wrong.** `Accordion.Sections` already read its children rather than keeping a list — its own remark
says a list "would be one that markup could not write to" — and a `Pagination`'s buttons are
*generated* from `PageCount`, so three numbers were always its whole authoring surface. Checking
turned two imagined fixes into two tests.

⚠ **`Option.Label` is the odd one out and is worth knowing about.** `ButtonBase.Label` writes a
part's text, which notifies nobody — so a `<Option Value="cutout" Label="Cutout" />` under a
`<Select Value="cutout" />` left the closed field showing its placeholder for a value that was
genuinely selected. `Label` is virtual now and `Option` overrides it to say so.

## Two layout traps, both found by looking at the picture

Neither of these fails, logs, or shows up in a test that does not measure a box — and both read as an
engine bug rather than as a missing declaration. They are the same trap at two depths.

**A component draws into a host element of its own.** `Shell.vxml` says `@tag app-shell`, so what is
under the document's root is `<app-shell>` and not the markup's first tag. CSS's initial
`flex-direction` is `row` and `flex-grow` is unset, so that element is content-sized in both axes
unless something says otherwise — and nothing does, because no file mentions it.

- **At the window.** The whole interface rendered in a strip about a hundred pixels wide down the
  left of a black window. Fixed in the host: `UiApplication` loads a user-agent sheet making the root
  a column and giving the content host `flex-grow: 1`, the way a browser ships `html, body`.
- **At each panel.** The hierarchy panel was blank, which reads as the virtualiser having realised no
  rows — and was the tree realising its rows correctly into a box zero pixels tall. Fixed in
  [`Theme/shell.vcss`](Theme/shell.vcss), which names the three panel tags, because a host cannot
  reach an element a component mounted into a `DockPanel`.
