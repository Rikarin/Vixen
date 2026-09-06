---
title: Loading when a panel appears
slug: ui/async-loading
kind: guide
area: Core
summary: Context.Load is the arrival hook a panel had no way to write — a tracked request, an untracked load, a token cancelled when the region goes, and a fault that arrives as a value markup can draw instead of an exception an effect swallows.
api: [T:Vixen.Ui.Reactive.AsyncValue`1, T:Vixen.Ui.Reactive.AsyncStatus, T:Vixen.Ui.Reactive.AsyncComputed`2]
tags: [ui, markup, async, cancellation, reactivity, loading]
since: 0.2
status: preview
related: [ui/markup-panels, ui/background-tasks, ui/reactive-collections]
---

## What it is

`BuildContext.Load` runs asynchronous work for as long as whatever declared it is in the document,
and hands back its state as one signal:

```csharp no-compile="a fragment; `Context` is the BuildContext a component is built with"
IReadOnlySignal<AsyncValue<Row[]>> rows = Context.Load(token => api.FetchRowsAsync(token));
```

`AsyncValue<T>` is `Status`, `Value` and `Error` in a single record struct, so the three states of a
load are one thing a binding reads. There are two overloads. The one-argument form above is the
arrival case: it asks for nothing, so it runs once. The two-argument form splits the work in two —

```csharp no-compile="a fragment; `generation` is a Signal<int> on the panel"
IReadOnlySignal<AsyncValue<Row[]>> rows = Context.Load(
    () => generation.Value,                      // the request: synchronous, tracked
    (_, token) => api.FetchRowsAsync(token)      // the work: asynchronous, tracked by nothing
);
```

— and the split is the feature rather than ceremony. The request is what the reactive graph watches;
the load is what the thread pool runs.

## What it is for

Panels need to fetch things when they appear, and before this there was nowhere to put that. A
component's `OnComposed` is synchronous and has no token, so a panel that had to load something
either blocked the build or started a task whose completion had nowhere safe to land and whose
cancellation on unmount was the author's to remember.

`Load` answers all three at once, and each answer is worth knowing about on its own.

⚠ **What cancels it is what cancels an effect.** The work is tracked on the region being built, so
the token is cancelled when that region goes. *Unmount* is the obvious case. *Rebuild* is the one
that is easy to miss, and a `.vxml` save is a rebuild: `Rebuild` clears the component's region before
re-entering `Build`, so the load a hot-reloaded panel started is cancelled by the reload rather than
left racing the one that replaces it.

⚠ **A re-request supersedes and cancels, and that is what a refresh is.** The request expression runs
with tracking on, so a signal read inside it re-asks when it is bumped, and the overtaken run's token
is cancelled by the same machinery that cancels it on unmount. A refresh is therefore two lines — a
signal, and something that increments it — rather than a task the panel owns and has to remember to
cancel.

⚠ **A failure is a value, not a throw.** `Effect.Run` catches, suspends and logs, so work that threw
into an effect would be a panel that silently stopped. The exception arrives as `AsyncStatus.Failure`
on the signal instead, which is a thing markup can render. An author who does not know this writes a
`try` around the call that never fires, because the call returns a signal and returns immediately.

⚠ **This is not [background tasks](../ui/background-tasks.md).** `BackgroundTaskManager` is progress
reporting for long work the application owns — an import, an export, a bake, with a percentage and a
cancel button somebody presses. `Load` is a panel's own arrival hook, scoped to the panel's lifetime,
with no progress and no user-visible queue. An author looking for the second finds the first by name,
which is why this paragraph is here.

## Using it

**From markup, `Context` is the way in.** A `.vxml`'s code-behind is a partial of a `Component`, and
`Component.Context` is the `BuildContext` it was built with — so `partial void OnComposed()` is where
a load is declared, and the field it assigns is what the markup reads.

```vxml
<guest-panel>
    @if (rows.Value.IsLoading) {
        <busy>Loading…</busy>
    }

    @for (var row in rows.Value.Value ?? []) {
        <row key="@row">@row</row>
    } @empty {
        <none>Nothing to show.</none>
    }
</guest-panel>
```

**Read the one value, never three.** `AsyncValue<T>` is one record struct precisely so that a panel
cannot draw a spinner over a stale list over an error message. `IsLoading` is `Status ==
AsyncStatus.Loading`; `HasValue` is true for a success *and* for a reload that still has the previous
result, which is what stops a list blanking on every keystroke of a search box.

**A refresh is a signal the request reads.**

```csharp no-compile="a fragment from a component's code-behind"
readonly Signal<int> generation = new(0);

public void Again() => generation.Value++;

partial void OnComposed() =>
    loaded = Context.Load(() => generation.Value, (_, token) => FetchAsync(token));
```

Bumping `generation` re-runs the request, which starts the work again and cancels what was in flight.
Nothing about that is the trigger's business: the same panel refreshes from a button, a menu item, a
key or a pointer gesture without a line of this changing.

⚠ **The request must be synchronous, and the compiler is what enforces it.** Dependency tracking
stops at the first `await` — the ambient consumer is thread-local and the continuation is on another
thread — so an `async` function that read signals after awaiting would silently record half its
dependencies. Making the tracked half a separate `Func<TRequest>` means what the graph can observe is
what the signature allows.

⚠ **A stamp, not just the token, decides who publishes.** A task that has already produced its value
cannot be cancelled, so every run is numbered and a result whose number is not the current one is
dropped. That is why an overtaken request cannot publish a stale answer even when it wins the race.

⚠ **Results come back through the document's scheduler.** They are applied on the owning thread at a
defined point in the frame, which is why nothing here needs a lock and why an assertion about a load
belongs after a flush rather than after a `Task.Delay`.

## Examples

**The arrival case**, from `Core/Vixen.Ui.Controls.Tests/Markup/AsyncGuest.vxml`, which draws the
three states through one binding:

```csharp no-compile="the @code block of AsyncGuest.vxml; `Context` and `OnComposed` are the component's"
IReadOnlySignal<AsyncValue<string>> loaded = null!;

string Describe() =>
    loaded.Value.Status switch {
        AsyncStatus.Success => loaded.Value.Value ?? string.Empty,
        AsyncStatus.Failure => "failed",
        _ => "loading"
    };

partial void OnComposed() => loaded = Context.Load(token => FetchAsync(token));
```

**The refreshing case**, from `Core/Vixen.Ui.Controls.Tests/Markup/RefreshableSheet.vxml`. The whole
of it is a loop over an async value, an `@if` over the in-flight state, an `@empty` arm and one
button:

```vxml
<Button Label="Refresh" on:click="@(() => generation.Value++)" />

@if (Rows.IsLoading) {
    <refresh-busy>Loading…</refresh-busy>
}

@for (var row in Rows.Value ?? []) {
    <refresh-row key="@row">@row</refresh-row>
} @empty {
    <refresh-none>Nothing yet.</refresh-none>
}
```

**Watching cancellation.** ⚠ Register on the token once and never dispose the registration. The
obvious shape — an `async` lambda with `using (token.Register(…))` around the await — reports *no*
cancellation for a runtime that is cancelling correctly, because `CancellationTokenSource.Cancel`
runs its callbacks last-registered-first: `WaitAsync`'s own registration resumes the state machine,
the `using` disposes this one, and a disposed registration is skipped when the walk reaches it.

```csharp no-compile="a fragment; `Gate` is a TaskCompletionSource a test completes"
partial void OnComposed() =>
    loaded = Context.Load(
        token => {
            Starts++;
            token.Register(() => Cancellations++);

            return Gate.Task;
        }
    );
```

That is also the shape to write a test against: a gate the test opens and counters it reads, rather
than an elapsed-time budget.

## See also

- [Background tasks](../ui/background-tasks.md) — the other async feature, and the one an author
  reaches for by mistake: progress reporting for work the application owns, not a panel's arrival.
- [Panels in markup](../ui/markup-panels.md) — where `OnComposed`, `Context` and the `@for`/`@empty`
  arms above come from.
- [Reactive collections](../ui/reactive-collections.md) — what to put a loaded list into when the
  panel goes on editing it after it arrives.
