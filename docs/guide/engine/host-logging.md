---
title: Host logging and vixen.log.yaml
slug: engine/host-logging
kind: guide
area: Engine
summary: One filter, several sinks, and a file that turns one subsystem up without a rebuild — plus the two lines in the log that say whether that file was read at all.
api: [T:Vixen.Core.Diagnostics.LogFilter, T:Vixen.Core.Diagnostics.LogSink, T:Vixen.Core.Diagnostics.LogRecordSink, T:Vixen.Core.Diagnostics.LogRecord, T:Vixen.Core.Diagnostics.RingBufferSink, T:Vixen.Core.Diagnostics.ConsoleSink, T:Vixen.Core.Diagnostics.ZLoggerFileSink, T:Vixen.Core.Diagnostics.PlatformSink, T:Vixen.Core.Diagnostics.EventSourceSink, T:Vixen.Core.Diagnostics.LogRateLimiter, L:13034, L:13035]
tags: [logging, diagnostics, host, configuration, support]
since: 0.1
status: preview
related: [engine/booting-an-application, rendering/diagnostic-overlays, ui/stylesheet-diagnostics]
---

## What it is

A host composes some sinks, hands all of them **one** `LogFilter`, and then reads `vixen.log.yaml`
into that filter. So "turn on verbose asset loading without drowning in render spam" is a two-line
file rather than a rebuild, and it means the same thing on the console, in the file and in the ring
the editor console reads.

| Piece | What it is |
|---|---|
| `vixen.log.yaml` | `minimumLevel` plus a `categories:` map of prefix → level. Read from `/app` and then `/data`. |
| `Vixen.Core.Diagnostics.LogFilter` | The rules themselves: one minimum level, and per-category prefixes matched longest-first. |
| `Vixen.Core.Diagnostics.LogSink` | What every sink has in common — the filter, an optional rate limiter, and the `ILoggerProvider` face. |
| `Vixen.Core.Diagnostics.LogRecordSink` | The four sinks that want a formatted `LogRecord` rather than the caller's state. |
| `Vixen.Core.Diagnostics.LogRecord` | Timestamp, level, category, event id, message, exception, suppressed count. |
| `Vixen.Core.Diagnostics.RingBufferSink` | The always-on in-memory ring. The editor console and the log overlay read it live. |
| `Vixen.Core.Diagnostics.ConsoleSink` | The terminal — colourised, aligned, errors to `stderr`. |
| `Vixen.Core.Diagnostics.ZLoggerFileSink` | Rolling JSON-line files. The one a player attaches to a bug report. |
| `Vixen.Core.Diagnostics.PlatformSink` | `logcat` · the Apple unified log · the journal · `OutputDebugString` · the browser console. |
| `Vixen.Core.Diagnostics.EventSourceSink` | `dotnet-trace` / PerfView / ETW, as the `Vixen-Diagnostics-Log` provider. |
| `Vixen.Core.Diagnostics.LogRateLimiter` | Suppression of a repeated `(category, event id)`, so one warning inside the frame loop is not sixty lines a second. |
| `L:13034` | *Log configuration from …* — the file was found and applied. |
| `L:13035` | *Log configuration at … was ignored* — the file was found and was not usable. |

Every log line in the engine is written through a `[LoggerMessage]` method with a numbered event id,
allocated in `docs/manual/log-events.md`. `13034` and `13035` are this page's own.

## What it is for

**Turning one subsystem up.** Levels are per *category prefix*, and a category is the logger's name —
`Vixen.Graphics.VulkanDevice`, `Vixen.Assets.Importers`. A rule on `Vixen.Assets` moves everything
beneath it in one line, and a rule naming one type still beats it, because prefixes are matched
longest-first rather than in the order they were added.

**Saying it without rebuilding.** ⚠ For a long time nothing outside two test files called
`SetCategoryLevel`: the mechanism was complete and there was no way to *say* it, which is why a
support build could only be made talkative by shipping a new one. `/data/vixen.log.yaml` is a file
somebody chasing a bug writes on the machine that has the bug.

**Telling a file that was read from a file that was never found.** This is what `13034` is for, and
it is deliberately said *even when the file agreed with the defaults*. A configuration file nobody
read is otherwise indistinguishable from one that was read and changed nothing — so the question
"did my `vixen.log.yaml` do anything?" is answered by whether the line is in the log at all, not by
whether the behaviour changed.

## Using it

### The file

```yaml
minimumLevel: Information

categories:
  Vixen.Assets: Debug
  Vixen.Assets.Importers.Gltf: Trace
  Vixen.Graphics: Warning
```

Levels are the `Microsoft.Extensions.Logging.LogLevel` names, parsed case-insensitively:
`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. An empty file is legitimate
— it is how a project commits "this exists and says nothing yet" — and applies no rules.

### Two locations, and which one wins

| Path | Whose it is |
|---|---|
| `/app/vixen.log.yaml` | Ships with the build. What a project commits. Read first. |
| `/data/vixen.log.yaml` | The machine's, written by whoever is chasing the bug. Read second, so it wins. |

Both are optional and both are applied when present, in that order — the second is not a replacement
for the first, it is a second pass over the same filter, so it overrides the prefixes it names and
leaves the others alone. `/app` and `/data` are engine mounts, not host directories: on Android the
first is inside the APK and on iOS inside the signed bundle, which is exactly why the machine's copy
lives under the writable one.

### What beats what

⚠ **`--vixen-log-level` beats the file's `minimumLevel`, and the file's per-category rules always
apply.** Those are two different decisions and not one: the flag and the key say the same thing, so
the more explicit one has to win — while the command line has no per-category form at all, and so
has nothing to disagree with.

### When it is read, and what it cannot cover

It is read immediately after the standard mounts exist and before anything else in the boot logs.
That ordering is the point: a per-category rule that arrives after the subsystem it names has
already spoken is a rule that did nothing on the run somebody was watching.

⚠ **It cannot cover the platform layer above it**, because the mounts do not exist until the platform
does. A boot that dies before the file system is mounted is turned up with `--vixen-log-level` and
with nothing else.

### A broken file is a warning, not a stop

A file that is not YAML, is not a mapping, or names something that is not a log level is reported as
`13035` and skipped. The message names the key and the value, because *the log config is broken* is
not a fix:

```
warn  Vixen.App  Log configuration at /data/vixen.log.yaml was ignored: vixen.log.yaml's
      'categories.Vixen.Assets' is 'Chatty', which is not a log level. Use one of Trace, Debug,
      Information, Warning, Error, Critical, None.
```

Refusing to start over a mistyped log level would be the wrong trade on the one path whose whole job
is to make the game easier to watch.

### Which sinks a host actually has

`AppBuilder` composes three, and the last two are conditional:

| Sink | When |
|---|---|
| `RingBufferSink` | Always. It is what the log overlay and the editor console read, and what a crash dump carries. |
| `ConsoleSink` | When `AppConfig.LogToConsole` — resolved from the build variant, so every variant but Release. |
| `ZLoggerFileSink` | When `--vixen-log-file <dir>` or `AppConfig.LogFileDirectory` names a directory. Off otherwise. |

⚠ **`PlatformSink` and `EventSourceSink` are not composed by the host at all.** `PlatformSink` is
added by hand by the two mobile samples, through `WithServices(services =>
services.LoggerFactory.AddProvider(new PlatformSink()))`; `EventSourceSink` is constructed nowhere
outside its own tests. Neither is reached by writing anything in `vixen.log.yaml` — the file
configures the shared filter, and a sink nobody constructed has nothing to filter.

⚠ And a provider added that late reaches only loggers created *after* it: the factory snapshots its
providers into each logger it hands out, and `WithServices` callbacks are the last thing
`AppBuilder.Build` does. So the mobile samples' `PlatformSink` misses the whole host boot —
[#1197](https://github.com/Rikarin/Vixen/issues/1197).

### Reading an id out of a log — and where the id is not

Ids are stable across message rewordings, which is what makes a number in a support ticket worth
more than a quoted sentence: `docs/manual/log-events.md` is the register, one row per id, with the
ranges allocated per assembly. `13034` above is `Vixen.App`'s, in the 13000 block.

⚠ **Today the id is not in the two places a person reads.** The console line is
`HH:mm:ss.fff LEVEL category message` and carries no id, and the rolling JSON line is — verified
against `ZLoggerFileSink` — of exactly this shape:

```json
{"Timestamp":"2026-09-09T22:22:23.73+02:00","LogLevel":"Warning","Category":"Vixen.Graphics.VulkanDevice","Message":"Device lost after 42 ms","Ms":42}
```

The structured fields the call site declared are still fields, and `EventId` is not among them. The
id reaches only `EventSourceSink` (as the ETW event's own id) and `RemoteSink` (as `"id"` in the
inspector's JSON) — neither of which a host composes. So *"grep the log for 13034"* does not work on
a file a player sent you; grep the message text, and use the register to go from the text back to the
id. That gap is [#1196](https://github.com/Rikarin/Vixen/issues/1196).

### ⚠ A disposed logger factory is deaf, not dead

The host's `ILoggerFactory` is disposed at shutdown, and a logger handed out before that keeps
working — it is the *providers* that have closed. A record written after that point used to reach a
fan-out over an empty provider list and vanish with no trace, which made "the last thing before the
crash" the one thing the log could not have. Every record written after disposal is now written to
`Console.Error` prefixed `[vixen: logged after shutdown]` instead.

⚠ Note the consequence for `IsEnabled`: a closed factory answers **true**, deliberately. The
source-generated logging methods check `IsEnabled` before they format anything, so a closed factory
answering false would make the diversion unreachable and put the silence straight back.

### Rate limiting

A message's identity is the pair `(category, event id)` — which is what the id register buys, and
what makes the decision to drop a line cost no formatting. The first few per window get through, the
rest are counted, and the next one that does get through carries `(repeated N times)`. `Critical` is
never suppressed, and neither is the first report of an event the tracking table had no room for: a
limiter that loses a novel error is worse than none.

## Examples

**A verbose asset build, quiet renderer.** `/data/vixen.log.yaml` on the machine reproducing the
bug:

```yaml
minimumLevel: Information

categories:
  Vixen.Assets: Trace
  Vixen.Graphics: Warning
  Vixen.Ui: Warning
```

**Confirming it was read.** The run's first few lines carry `13034` once per file found:

```
info  Vixen.App  Log configuration from /app/vixen.log.yaml: 2 category rule(s), minimum Information.
info  Vixen.App  Log configuration from /data/vixen.log.yaml: 3 category rule(s), minimum Information.
```

⚠ Two lines, and the counts are per file rather than cumulative: the second says how many rules
*that* document set, and `minimum` is the filter's value after it. A run with **no** such line found
no file at all — which is the answer to nine out of ten "my `vixen.log.yaml` does nothing" reports.

**The same rules from code**, for a head that wants them compiled in or a test that wants them
without a file. The filter every sink shares is on the services:

```csharp no-compile="A fragment: `services` is the AppServices a WithServices callback was handed."
services.Logs.Filter.SetCategoryLevel("Vixen.Assets", LogLevel.Trace);
services.Logs.Filter.SetCategoryLevel("Vixen.Graphics", LogLevel.Warning);
```

`services.Logs` is the `RingBufferSink`, and its `Filter` is the object `AppBuilder` handed to every
sink it composed — so a rule set here moves the console and the file with it. Giving one sink a
filter of its own is equally valid and is how the file stays verbose while the console stays quiet.

**Turning a boot up from the command line**, when the failure is above the file system:

```
dotnet run -- --vixen-log-level Debug --vixen-log-file ./logs
```

## See also

- [Booting an application](booting-an-application.md) — where in `AppBuilder.Build` the file is read,
  and the `--vixen-*` arguments in full.
- [Diagnostic overlays](../rendering/diagnostic-overlays.md) — the log overlay, which reads the same
  ring this filter feeds.
- [Stylesheet diagnostics](../ui/stylesheet-diagnostics.md) — a subsystem's own ids, and the register
  rules that keep one stable.
- `docs/manual/log-events.md` — the register itself: every id, its level, its message and the release
  it appeared in.
- `Core/Vixen.Core.Diagnostics/README.md` — why there is one filter and several sinks, and what the
  ring deliberately does not store.
