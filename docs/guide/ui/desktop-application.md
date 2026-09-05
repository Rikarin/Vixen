---
title: Running a UI application
slug: ui/desktop-application
kind: guide
area: Platform
summary: UiApplication.Run(options) is the whole of a Vixen interface's Main — a window, a device and the four steps of a frame — and this is what the options say, when the three events fire relative to the layout, and why the loop redraws every frame instead of when something changed.
api: [T:Vixen.Ui.Desktop.UiApplication, T:Vixen.Ui.Desktop.UiApplicationOptions, T:Vixen.Ui.Desktop.UiFrame, T:Vixen.Ui.CloseRequestEvent, T:Vixen.Ui.UiCloseReason]
tags: [ui, desktop, hosting, windowing, application, entry-point, quit, lifecycle]
since: 0.2
status: preview
related: [ui/markup-project-setup, ui/background-tasks]
---

## What it is

`UiApplication` is the host for an application whose whole content is a user interface. It opens a
window, creates a device, mounts a component into a `UiDocument`, and runs the four steps of a frame
until the window closes.

`UiApplication.Run(options)` is the entry point, and it is the only public way to start one — the
constructor is internal, because an application that has been constructed and not run is a window
nobody opened.

```csharp no-compile="a Main; Vixen.Ui.Desktop is a Platform/ assembly the doc compilations do not reference"
static int Main(string[] arguments) =>
    UiApplication.Run(
        new UiApplicationOptions {
            Title = "Vixen — Hello UI",
            Organisation = "Vixen",
            Application = "HelloUi",
            Content = () => new Shell()
        },
        arguments
    );
```

That is the whole of a Vixen application's `Main`.

⚠ **It knows nothing about the engine.** `Vixen.Ui.Desktop` is a `Platform/` assembly holding a
window, a device and a frame loop; it does not reference `Vixen.Engine` or `Vixen.App`, and neither
does an application that uses it. The boundary is what keeps a tool from dragging a renderer, a scene
graph and an asset database behind it.

## What it is for

**Because there used to be three of these and they had diverged.** `Samples/02-HelloUi` carried a
five-hundred-line `Program.cs` with a Vulkan device, a swapchain, a render graph, an atlas upload,
resize coalescing and a suboptimal-present rule in it; `Vixen.Templates`' `AppHost` and
`Vixen.Editor.Host`'s `EditorHost` each carried their own copy of the same thing, and the three did
not agree about any of it. Every one of them was written because the alternative was referencing a
host that drags an engine behind it.

⚠ **A frame is drawn every pass, not when something changed.** The loop is unconditional, and that is
a decision rather than an omission: an interface that repainted on change needs something to be the
authority on what "changed" means, and the reactive graph deliberately is not that — an `Effect` can
assign a property that no draw depends on, and a caret blinks with nothing assigned at all. The cost
is a device that is always busy on a laptop, which is real; the alternative is a class of bug where
the screen is correct only if somebody remembered to invalidate.

## Using it

**`UiApplicationOptions` has a shippable default for everything except one.** The shortest useful set
is `Title` and `Content`; `Content` is a `Func<Component>` and is the one thing there is no sensible
default for.

| Option | Default | What it settles |
|---|---|---|
| `Title` | `"Vixen"` | What the window is called |
| `Size` | `1280 × 800` | Its size, in device-independent pixels |
| `IsResizable` | `true` | Whether the user may resize it |
| `Organisation` / `Application` | `"Vixen"` | The two halves of where settings are kept |
| `Content` | — | Builds the interface |
| `Mount` | the default | Puts the interface into the document, for hot reload |
| `Styles` | `[]` | Stylesheets to load, in order, as author sheets |
| `InstallControlTheme` | `true` | Whether the control set's theme goes in under everything |
| `RootClasses` | `[]` | Classes to put on the document's root |
| `Ground` | a dark blue-grey | What the window is cleared to |
| `InstallSystemFont` | `true` | Whether a face is borrowed from the OS when none is registered |
| `Configure` | — | Run once against the document, after the sheets and before the content |
| `Frames` | `0` | How many frames to run, or zero for "until it is closed" |
| `Platform` | SDL | Where the window comes from |

⚠ **`Styles` cannot be inferred, and the generated utility sheet is the one people forget.**
`VixenUtilityStyles.Css` is a string in the assembly the build produced; nothing walks a manifest
looking for it, so an application whose classes silently do nothing is nearly always one that did not
name it here. See [making a project compile markup](markup-project-setup.md) for where that string
comes from.

⚠ **`Ground` is used twice and the two must not disagree.** It is the clear colour *and* the colour
`UiRenderer.Compose` composites against, so a window whose two halves were given different values
gets a fringe wherever the interface is translucent.

**Three events, and where each one is is the whole of what distinguishes them.**

* `Started` fires once, after the interface is built and before the first frame is pumped.
* `Frame` fires once a frame, after the events are pumped and **before** `UiDocument.Update` — so a
  signal a handler writes is laid out and drawn in the same frame rather than the next one.
* `Stopping` fires once, after the loop stops and while the document is **still alive**. That is why
  it is not `Dispose`: a handler that wants to read what is on screen before it goes still can.

Each has a matching property on the options, for an application that has nowhere to hold the
instance.

`UiFrame` is what `Frame` carries: `Now`, the time since the application started, and `Delta`, how
long the previous frame took.

**`Tasks` is a `BackgroundTaskManager`, pumped once a frame before `Frame` fires.** An application
with long work to report does not need a timer of its own; see
[background tasks](background-tasks.md).

**`Run(options, arguments)` is the overload to use from `Main`.** It reads the arguments every Vixen
application understands before starting, of which the one that matters here is `--frames N` — which
sets `Frames`, and is what makes a screenshot run or a smoke test terminate.

**`Platform` is how an application reaches the operating system.** The clipboard, the native file
pickers, the displays and the process lifecycle live on `IPlatform`, which `Vixen.Ui` is not allowed
to name — so this property is the only route to them from application code, and `Started` is where
to read it.

⚠ **Ask `Capabilities` before using most of it.** A headless run has no displays; a Linux session
with no `zenity` or `kdialog` has no file picker; and a picker that is not there answers "nothing
chosen", which is exactly what the user pressing Cancel answers. `platform.Pickers()` is that
question spelled once: it hands back `INativeDialogs` when `PlatformCapabilities.NativeDialogs` is
present and `null` otherwise, which is what an `Open…` menu item's enablement should read.
`ShowMessageAsync` is the exception and is always safe to call.

⚠ **Everything on it belongs to the loop thread**, so call it from `Started`, `Frame`, `Stopping` or
an event handler — never from a continuation that resumed on a pool thread. Win32 delivers messages
to the thread that made the window and AppKit refuses to be touched from anywhere else; that is the
operating systems' rule, not the framework's.

⚠ **Hot reload arrives by reference and there is no flag.** A `Debug`-only reference to
`Vixen.Ui.Desktop.HotReload` is the whole of turning it on: `UiApplication`'s static constructor
loads that assembly by name and runs its module initializer, so a Release build does not resolve it
and nothing in `Main` changes. `Mount` exists for the same reason — it lets the reload host own the
mounting without `Vixen.Ui.HotReload` being linked into a shipped application.

## Quitting, and refusing to

`UiApplication.Quit()` asks before it stops. The request is routed from the focus outwards as a
`CloseRequestEvent`, and anything on the route may `Cancel()` it; `UiDocument.CloseRequested` fires
afterwards for a head that is not in the element tree.

The prompt an application with a document wants is written already — it is
`DocumentClosePrompt.Install`, and one line is the whole of it:

```csharp no-compile="a fragment; `application` and `dialogs` are the application's own"
using var prompt = DocumentClosePrompt.Install(
    application.Document.Root,
    dialogs,
    () => application.Quit()
);
```

Something that is not a document — an unfinished upload, a running job — answers the request itself:

```csharp no-compile="a fragment; `document` and `job` are the application's own"
document.Root.AddHandler<CloseRequestEvent>((_, args) => {
    if (job.IsFinished) {
        return;
    }

    args.Cancel();
    job.Cancelled += () => application.Quit();
});
```

⚠ **A refusal is "not now", not "never".** A Save / Don't Save / Cancel prompt is a dialog and a
dialog is answered frames later, so a synchronous veto cannot wait for one without blocking the loop
that draws it. The handler cancels, opens the prompt, and calls `Quit()` again when it has an
answer — which is the shape `EditorHost` has used since save-on-close was built, and the shape
`DocumentClosePrompt` packages. ⚠ Calling `Quit()` again runs every handler again, so a handler that
refuses on a condition its own answer did not change has to latch over the second pass or the
application cannot be quit at all.

⚠ **`Cancel()` is not `Handled`.** They are two questions. A document that saved silently has dealt
with the request and is content to go, and a handler forced to say so by leaving `Handled` false
could not be told apart from one that had refused.

`Stop()` is the unconditional form, for the handler that has finished asking. `Platform` is the
platform the application is running on — the clipboard, the native dialogs, the lifecycle and the
display list all hang off it.

## Examples

The application template's `Main`, which is the shape to copy:

```csharp no-compile="a Main; Vixen.Ui.Desktop is a Platform/ assembly the doc compilations do not reference"
static int Main(string[] arguments) =>
    UiApplication.Run(
        new UiApplicationOptions {
            Title = "My Tool",
            Organisation = "Acme",
            Application = "MyTool",
            Size = new Int2(1280, 800),

            // The generated sheet. Nothing finds it for you.
            Styles = { VixenUtilityStyles.Css },
            RootClasses = { "dark" },
            Content = () => new Shell()
        },
        arguments
    );
```

Reporting a frame time into the interface, using the event that runs before the layout:

```csharp no-compile="a fragment; `application` is the one Run built and `model` is the caller's"
application.Frame += (_, frame) => model.FrameTime.Value = frame.Delta.TotalMilliseconds;
```

Because `Frame` runs before `UiDocument.Update`, the number written there is laid out and drawn in
that same frame.

A headless run of a fixed length, which is what a screenshot job wants:

```
MyTool --frames 4
```

## See also

* [Making a project compile markup](markup-project-setup.md) — where `VixenUtilityStyles.Css` and
  the `.vxml` classes come from.
* [Background tasks](background-tasks.md) — what `UiApplication.Tasks` is pumping.
* [Docking panels](docking-panels.md) — the arrangement an editor-shaped application puts inside the
  window, and the scrolling contract a panel keeps to.
* [`UiDocument`](/docs/api/vixen.ui/uidocument) — the tree the loop lays out, draws and dispatches
  into.
