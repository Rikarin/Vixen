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
| [`Program.cs`](Program.cs) | Thirty lines: what the window is called, and which component to mount. |

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

**Edit `Theme/shell.vcss` while the sample is running and the window repaints.** Every element keeps
its identity across a style reload, so the focus, the scroll offset, the docking arrangement and the
tree's place in its thousand rows all survive. That is six lines in `Program.cs` — a
`HotReloadWatcher` over the source directory, polled once a frame — and dropping them plus the
`Vixen.Ui.HotReload` reference is what a shipping application does.

⚠ **Changing a rule takes effect; *deleting* one does not, until the next build.** The sample prints
which of the two it got on start-up. `shell.vcss` is handed to the build as a `VixenStyleBase`, so
what the document holds is the generated sheet with this file concatenated into the front of it —
there is no separate sheet for `HotReloadWatcher.Load` to bind the path to, and it layers the file on
top instead. That is the right arrangement for shipping (it is what fixes the layer order and expands
`@apply`) and the wrong one for taking a rule out at run time. `HotReloadWatcher.Replaces` is the API
that says which you have, and this is what it is for.

Try `--accent` by hand: change `--color-brand` in `Theme/vixen.ui.vcss`, and note that *that* one
needs a rebuild. Tokens are compiled into the utility sheet at build time; the rules in `shell.vcss`
are not.

`--frames N` runs exactly N frames and exits, which is how CI proves the whole stack starts, presents
and stops without a validation error or a hang — on a machine that may have no GPU at all, because
everything above the RHI runs whether or not a device was ever created. On the way out the sample
prints the docking arrangement, which is what an application would write to disk.

## Where the markup stops, and why

⚠ **Four controls are wired in `OnComposed` rather than written as tags, and each one is a gap in the
engine rather than a preference.** A nested tag goes to `UiElement.ContentHost`, and a control that
does not override it puts children on itself instead of in the slot they belong in — where they draw,
unregistered, doing nothing:

| Control | What builds its contents | What a nested tag would do |
|---|---|---|
| `MenuBar` | `AddMenu`, which parents the dropdown to `Document.Root` | Draw a menu inside the bar, clipped by it |
| `DockingHost` | `AddPanel`, which assigns an id and places it in the layout | Draw a panel the arrangement does not know about |
| `TreeView` | `Root.Add`, into a model the virtualiser flattens | Draw a row the virtualiser never realises |
| `RadioGroup`, `Select` | `AddOption`, into a list the control keeps | Draw a choice with no `Value`, no exclusivity, no roving tab index |

`Tabs`, `Expander`, `ScrollView` and `Card` do override it, which is why every one of *those* is a
tag here. It is one property per control, and closing the gap is worth doing.

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
