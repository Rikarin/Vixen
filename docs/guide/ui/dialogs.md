---
title: Dialogs that answer
slug: ui/dialogs
kind: guide
area: Core
summary: A modal question an application can await — confirm, prompt, choose, or one it fills in itself — queued one at a time, completed from the document's tick rather than from the click that answered it, and answered rather than dropped when the application goes away.
api: [T:Vixen.Ui.Controls.DialogService, T:Vixen.Ui.Controls.DialogSession`1, T:Vixen.Ui.Controls.Dialog]
tags: [ui, controls, dialogs, modality, async]
since: 0.2
status: preview
related: [ui/commands, editor/index]
---

## What it is

`Dialog` is the control: a backdrop that covers what is behind, a focus scope Tab cannot walk out
of, and the focus put back where it was when it closes. What it does not have is an answer — it is a
box with a header, a body and a footer, and nothing in it knows what pressing a button means.

`DialogService` is the answer. It owns one `UiDocument`, presents one dialog at a time into it, and
hands back a `Task<T>` a caller awaits:

* `ConfirmAsync` — yes or no, with Escape and the close button both meaning no.
* `PromptAsync` — a line of text, or `null` if the user backed out. An empty field is a back-out
  rather than an empty answer, and the confirming button says so by staying disabled.
* `ChooseAsync` — Save / Don't Save / Cancel and the handful like it, answering with an index, or
  `-1` for a back-out.
* `ShowAsync<T>` — a dialog the caller fills in, through a `DialogSession<T>`: put controls in
  `Body`, and declare each footer button as a label and *what pressing it answers*.

## What it is for

An application that has to ask a question in the middle of doing something, and carry on where it
left off with the answer. `await` is the whole of the interface:

```csharp no-compile="a fragment; `dialogs` is the application's DialogService"
if (await dialogs.ConfirmAsync("Delete the layout?", danger: true)) {
    layouts.Delete(name);
}
```

Four things about that line are the reason this is in the framework rather than in each application,
and each of them is a defect a first attempt has:

* **Answering removes nothing.** The click that answers was dispatched *into* the dialog's own
  subtree. Tearing that subtree down inside its own event leaves the router walking elements that
  are no longer in the document. `Answer` records the value and closes the overlay; the element goes
  away on the next tick.
* **The continuation runs on the frame loop.** The task is completed from `DialogService.Pump`,
  between two frames, not from the click handler — so the command that was awaiting resumes with
  nothing half-dispatched underneath it.
* **One at a time, queued rather than refused.** Two backdrops over each other is a picture with no
  answer in it. Refusing the second ask instead would mean *"your Save prompt was dropped because a
  rename was open"*, which is the failure that loses work.
* **`CancelAll` answers rather than drops.** On shutdown every waiting ask is completed with its
  dismissal, so a command awaiting one unwinds instead of never finishing. A task nobody completes
  is a process that will not go.

⚠ **Drawn, not native, and on purpose.** A modal that is an OS window cannot be screenshotted by a
golden-image suite and cannot be driven by a headless harness. A *file* picker is the opposite case
and belongs to the platform — that one is about the user's disk rather than the application's state,
and a drawn one has none of the places, tags or sandbox permissions a real picker carries.

## Using it

Construct one over the document and keep it for as long as the document lives:

```csharp no-compile="a fragment; `document` is the application's UiDocument"
var dialogs = new DialogService(document);
```

That is the whole of the wiring. **The pump is the document's tick**: the service subscribes to
`UiDocument.Ticked`, so a host that calls `UiDocument.Tick` every frame — which every host must,
whether anything happened or not — has working dialogs without knowing `Pump` exists.

`UiDocument.Update` would have been the wrong half of the frame, for the same reason
`CommandsInvalidated` is not raised from it: `Update` returns early when nothing dirtied the
document, and asking a question dirties nothing. `Pump` stays public because a test wants to step a
frame's worth of dialog without a clock.

**The threading contract is one thread and never a blocking wait.** Everything happens on the thread
that ticks the document, and the task is completed from `Pump` — so `await` is the only correct way
to consume one. A caller that blocks on `.Result` or `.Wait()` blocks the thread that would have
pumped the answer, which is a deadlock rather than a slow frame. Re-entrancy is fine and is the
ordinary case: a command that answers one dialog by opening another is asking from *inside* `Pump`,
and the new ask is presented by that same call.

**Disposal is what stops the queue outliving the application.** The subscription is a strong
reference from the document to the service, and the service holds every awaiting caller's
continuation. `Dispose` unsubscribes and then `CancelAll`s, in that order, so nothing is left
waiting and nothing is left pumping:

```csharp no-compile="a fragment; `dialogs` is the application's DialogService"
dialogs.Dispose();
document.Dispose();
```

⚠ **The default button labels are literals — `OK` and `Cancel` — because the string catalogue is
still in `Vixen.Editor.Ui`.** Until doc 46 § A3 promotes it, an application that needs a translated
confirming button passes one: every label on every method here is a parameter.

## Examples

A command that asks before it destroys something, and one that asks for a name. Both are ordinary
`async` methods — there is no callback, no completion source and no "did the user answer yet" flag:

```csharp compile
using Vixen.Ui.Controls;

public sealed class LayoutCommands(DialogService dialogs) {
    public async Task<bool> DeleteAsync(string layout) =>
        await dialogs.ConfirmAsync(
            $"Delete '{layout}'?",
            "This cannot be undone.",
            confirm: "Delete",
            danger: true
        );

    public async Task<string?> RenameAsync(string layout) =>
        await dialogs.PromptAsync("Rename layout", initial: layout, confirm: "Rename");

    // Save / Don't Save / Cancel: the one modal every application has, and the one that runs while
    // the process is going away. `CancelAll` answers it with -1 rather than leaving this awaiting.
    public async Task<int> OnCloseAsync(string document) =>
        await dialogs.ChooseAsync(
            $"Save changes to '{document}'?",
            null,
            "Cancel",
            "Don't Save",
            "Save"
        );
}
```

A dialog the caller fills in. The result is a *delegate* rather than a value because the confirming
button answers with whatever is in the dialog's field, and that does not exist yet when the button
is declared:

```csharp compile
using Vixen.Ui.Controls;

public static class TargetPicker {
    public static Task<string?> AskAsync(DialogService dialogs, IReadOnlyList<string> targets) =>
        dialogs.ShowAsync<string?>(
            "Build target",
            session => {
                var list = session.Body.Add<ComboBox>();

                foreach (var target in targets) {
                    list.AddOption(target);
                }

                session.AddButton("Cancel", () => null);
                session.AddButton("Build", () => list.Value, ControlVariant.Primary);
            }
        );
}
```

⚠ **`build` runs when the dialog opens, not when it is asked for.** A dialog queued behind another
builds its rows against the state they are in when it finally appears — which for a save prompt
behind a rename is a different set of dirty documents than the one that existed when the command
ran.

Driving one from a test, which is the shape the whole design is for. Nothing here needs a clock,
because `Pump` is public:

```csharp no-compile="a fragment; `dialogs` is a DialogService over a test document"
var answer = dialogs.ConfirmAsync("Delete it?");

dialogs.Pump();                                 // presents it
dialogs.Current!.Footer.Children.OfType<Button>().First(b => b.Label == "OK").Activate();

// ⚠ Not yet. The click ran inside the dialog's own event dispatch.
Assert.False(answer.IsCompleted);

dialogs.Pump();                                 // completes it
Assert.True(await answer);
```

## See also

* [Commands and the focus route](/docs/guide/ui/commands) — where the callers come from. Every one
  of these questions is asked by a command, which is why a dropped ask is a lost edit.
* [The editor shell](/docs/guide/editor/index) — the first consumer, and the place the four
  subtleties above were each learned.
