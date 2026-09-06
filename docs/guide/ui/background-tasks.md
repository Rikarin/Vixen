---
title: Background tasks
slug: ui/background-tasks
kind: guide
area: Core
summary: Long work that does not freeze the window — a task model with progress, status and cancellation whose properties are signals, a queue that lands every report on one point of the frame, and a manager whose disposal is what stops a cancelled import outliving the application that started it.
api: [T:Vixen.Ui.BackgroundTask, T:Vixen.Ui.BackgroundTaskManager, T:Vixen.Ui.BackgroundTaskState]
tags: [ui, tasks, progress, cancellation, threading, reactivity]
since: 0.2
status: preview
related: [ui/commands, ui/reactive-collections, ui/desktop-application, editor/index]
---

## What it is

`BackgroundTaskManager` is the list of long operations an application is running, and
`BackgroundTask` is one of them: a title, a status line, a progress fraction, a state and a
cancellation token. The work runs wherever the caller put it — usually the thread pool — and
everything it reports is queued and applied at one point in the frame, in
`BackgroundTaskManager.Pump`.

```
work on a pool thread ── Report ──▶ queue ── Pump (once a frame) ──▶ signals ──▶ bindings
```

Three properties are the whole of the design:

- **Reporting is safe from any thread; reading is only safe from the UI one.** A progress bar read
  during layout must not see a title replaced between two reads.
- **Every property is signal-backed.** `float Progress { get; }` keeps the shape it always had, but
  reading it *inside a binding* subscribes to it — so a panel over the model is markup, not a view
  that is told to look once a frame.
- **Cancellation is a token, and it is the reason this exists rather than a `Task`.** A modal
  progress dialog is what an application grows when its long operations cannot be stopped.

## What it is for

Anything that takes long enough that a frozen window would be wrong: an import, a build, a bake, a
clone from source control, a batch upload. The rule it exists to keep is doc 11's — *progress with
cancellation, never a modal progress dialog*. Work that takes forty seconds must not stop somebody
opening a different file while it runs.

It is deliberately **not** a scheduler. It does not decide what runs, or when, or how many at once;
it is the model an interface binds to and the cancellation the user's button is wired to. `Task.Run`
still runs the work.

## Using it

`UiApplication` owns a manager and pumps it once a frame, so an application that uses the standard
loop gets progress for nothing:

```csharp no-compile="a fragment; `application` is the UiApplication handed to a Started handler"
var task = application.Tasks.Start(
    "Importing textures",
    async running => {
        for (var i = 0; i < files.Count; i++) {
            running.Cancellation.ThrowIfCancellationRequested();

            await Import(files[i]);
            running.Report((i + 1f) / files.Count, files[i].Name);
        }
    }
);
```

`Start` does not await the work and its exceptions do not escape: the task ends as
`BackgroundTaskState.Failed` with the exception on it, which is what a notification is made of. A
background task that threw into an unobserved `Task` would take the process down on a later garbage
collection with a stack trace pointing at nothing.

For work that is already asynchronous in its own way — a file watcher, a subprocess, a server
request — `Begin` hands back a task the caller finishes itself with `Complete` or `Fail`.

```csharp compile
using Vixen.Ui;

public sealed class Upload {
    readonly BackgroundTaskManager tasks;

    public Upload(BackgroundTaskManager tasks) => this.tasks = tasks;

    public BackgroundTask Begin(string name) {
        var task = tasks.Begin(name);

        task.Report("connecting");

        return task;
    }

    // Called from the transfer's own callback, on whatever thread it uses.
    public void Advance(BackgroundTask task, long sent, long total) =>
        task.Report((float)sent / total, sent + " / " + total);

    public void Finish(BackgroundTask task) => tasks.Complete(task);
}
```

### Cancelling

`BackgroundTask.Cancel` **asks**. It fires the token and sets `IsCancellationRequested`; the task
does not reach `BackgroundTaskState.Cancelled` until the work notices and stops. A manager that
moved the task out of the list at the moment the button was clicked would let the user start the
import again over the top of one that was still writing files.

The two are separate on purpose. `Cancellation` is the token the *work* watches from a pool thread;
`IsCancellationRequested` is a signal the UI thread writes, so a button can grey itself out without
putting the reactive graph in the way of a cancellation check in a tight loop.

### Pumping

Something has to call `Pump` once a frame or nothing ever changes — the reported numbers are
queued, not applied, so a manager nobody pumps is a list of tasks stuck at nought per cent, and it
fails silently rather than loudly. `UiApplication` does it for the standard loop. A host with its
own loop owns a manager and pumps it itself, which is what the editor's shell does.

`Pump`'s budget is a livelock guard rather than a performance nicety: a task reporting per file over
a hundred thousand files can enqueue faster than a frame can drain, and an unbounded pump would
never return — the application would stop drawing while claiming to show progress.

### Disposing

`BackgroundTaskManager` is `IDisposable`, and an owner going away has to dispose it. Work on the
pool reports through a queue; an owner that simply dropped the manager would leave every running
task enqueueing into a queue nothing drains, so the queue grows without bound and every closure in
it keeps the task, the manager and — the case that matters — the assembly the work's delegate came
from alive. A plugin unloaded while one of its imports is running is exactly that shape: the
delegate pins the collectible load context and the reload leaks the whole assembly.

Disposal asks and does not wait. Every task is cancelled and finished as `Cancelled`, but the work
is on a thread this has no handle on and keeps running until it notices its token. What disposal
guarantees is that nothing reported afterwards is kept: reports are dropped rather than enqueued, so
work that ignores cancellation for another minute costs one thread and no memory. Blocking instead
would be a frame thread waiting on a file copy.

## Examples

A panel over the model needs no subscription, because the model holds signals. This is the whole of
the editor's task centre, in markup:

```vxml no-compile="the body of a component; the whole file is Editor/Vixen.Editor.Ui/Tasks/TaskCenter.vxml"
@for (var task in Running) {
    <task-row key="@task">
        <task-title class="truncate min-w-0">@task.Title</task-title>

        <IconButton Label="Cancel"
                    Disabled="@task.IsCancellationRequested"
                    on:click.stop="@(() => task.Cancel())" />

        <ProgressBar Value="@task.Progress" IsIndeterminate="@task.IsIndeterminate" />
        <task-status>@(task.Status ?? string.Empty)</task-status>
    </task-row>
}
```

`BackgroundTaskManager.Tasks` is a `CollectionSignal`, so the `@for` re-runs when a task starts or
stops; every property read inside the row subscribes to that task's own signal, so a row's bar moves
without the list being rebuilt. Nothing here hangs a handler on an event, which means nothing here
can outlive the panel.

A status bar wants one number for all of it:

```csharp no-compile="a fragment; `tasks` is a BackgroundTaskManager and `bar` a ProgressBar"
bar.IsIndeterminate = tasks.Progress <= 0f;
bar.Value = tasks.Progress * bar.Maximum;
```

`BackgroundTaskManager.Progress` is the mean over the *determinate* tasks. A task that has not
reported progress is left out rather than counted as zero: three imports of which two are
indeterminate would otherwise sit at a third of the way along and never move.

`Ended` is raised once per task after it stops, whatever it stopped as, and is where a notification
comes from — the manager knows a build failed and the notification centre knows how to say so, and
joining them directly would make one of them need the other.

## See also

- [Commands and the focus route](commands.md) — the other half of what a menu item does: `commands`
  decides who handles *start the import*, and this is what the import reports into.
- [Reactive collections](reactive-collections.md) — what `CollectionSignal` guarantees, which is
  what makes a task list a panel rather than a rebuild.
- [The editor shell](../editor/index.md) — the task centre and the status bar built over this model,
  and a host that pumps its own manager rather than using `UiApplication`'s.
- [Loading when a panel appears](async-loading.md) — the feature this one is mistaken for. A panel's
  own arrival hook, cancelled when its region goes and with no progress and no queue; reach for that
  when the work belongs to one panel rather than to the application.
