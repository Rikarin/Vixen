# Vixen.Templates

The `dotnet new` templates, and the one tree of files `vixen new` writes from.

```bash
dotnet new install Vixen.Templates

dotnet new vixen-game -n Asteroids      # a game: a Game subclass, a host, Assets/, a Dockerfile
dotnet new vixen-app  -n Painter        # an application: Vixen.Ui, a window, and no engine
dotnet new vixen-lib  -n Physics        # a library either of them can reference
dotnet new vixen-mmo  -n Kestrel        # a dedicated-server game: contracts, rules, realm, client
dotnet new vixen-plugin -n Kestrel      # an editor plugin: a manifest, an IEditorPlugin, a panel
dotnet new vixen-tool -n Bake           # a batch head: the host, with no window and no device
```

Spec: [docs/plan/17 § Project templates](../../docs/plan/17-app-heads-and-shipping.md),
[docs/plan/02 § Tools/](../../docs/plan/02-repository-layout.md).

## There is one tree, and two things that instantiate it

`templates/` is the source. This package ships it, and [`Vixen.Cli`](../Vixen.Cli/README.md)
**embeds** it — so `vixen new game` and `dotnet new vixen-game` produce the same directory rather
than two directories that happen to look alike. A test asserts they still do.

That matters because the two exist for different people. `dotnet new` is right for somebody who has
installed the SDK; `vixen new` is right before anything is installed, which is the state a person is
in when they are deciding whether to try the engine at all. What would not be right is two copies of
every file.

**The direction is files → C#, and it used to be written down the other way.** `ScaffoldRunner`'s
remarks said the pack should be generated from its string literals. That is wrong: `dotnet new`
consumes real files with real names and a `.template.config/template.json` beside them that no C#
string can produce, so generating the pack means generating something no human reviews. Reading them
the other way is fifty lines of `TemplateCatalog`, and both sides then consume exactly what ships.

## The one substitution

A scaffolded project has to name a version — `<Project Sdk="Vixen.Sdk/x.y.z">` and every
`PackageReference` — and an SDK version may not be an MSBuild property, so there is nowhere for a
template to defer the question to. The templates therefore carry the token `VIXEN_PACKAGE_VERSION`,
and it is resolved twice:

| | resolves it to | where |
|---|---|---|
| `Vixen.Templates.csproj` | the version of the package being packed | project files and the `Dockerfile`, on the way into `content/` |
| `TemplateCatalog` | the version of the `vixen` tool doing the scaffolding | every text file, at scaffold time |

⚠ **The two disagree about which files they rewrite**, deliberately — pack time rewrites a named
list, scaffold time rewrites everything textual — so a token in a `.cs` file would survive into the
package and not into the CLI's output. `Vixen.Templates.Tests` asserts the token appears only where
both handle it.

⚠ **A binary file is copied, never substituted into.** `TemplateCatalog` decides by looking for a
NUL byte, which is how `git` answers the same question. A project name rewritten into the middle of
a SPIR-V word is a device lost rather than a compile error.

## What the templates may use, and what they may not

**Only `sourceName` identity substitution.** The template engine also substitutes *derived forms* —
a lower-cased `vixengame1` in a comment becomes `asteroids` — and `TemplateCatalog` deliberately
implements none of them, because a second, partial implementation of a templating language is a thing
that silently disagrees with the real one. So the templates spell their source name exactly, every
time, and a test fails on any other casing of it.

The same argument rules out conditionals, computed symbols, renames and post actions. A template that
needs one of those has stopped being writable by both paths.

## Testing them

`Vixen.Templates.Tests` **compiles what each template writes**, with Roslyn, against the assemblies
its `PackageReference`s resolve to. Nothing in the repository compiles `templates/**/*.cs` — they are
somebody else's project and are outside every glob — so without this a template naming a constructor
overload that was dropped last month builds a perfectly good package and fails on the machine of the
first person to run `dotnet new`.

What it does not check is whether the project file *restores*: that needs a feed with the engine
packages on it, which is CI's job and doc 14's Phase 11 clean-machine criterion.

## `vixen-game`

`Program.cs`, a `Game` subclass, `Assets/Default.vxgroup`, a `.gitignore` that keeps `Library/` out
of the history, and a `Dockerfile`.

It scaffolds against the **SDK** rather than a package list: `<Project Sdk="Vixen.Sdk/x.y.z">` plus
one `PackageReference` for the host and one for the frame, and the import-before-compile and
content-build-after-build wiring arrives with [`Vixen.Sdk`](../Vixen.Sdk/README.md). A template
listing every package the engine currently needs is a template that is wrong one release later.

The frame is [doc 39](../../docs/plan/39-standard-frame-and-render-presets.md)'s seven-line
`!StandardFrame` document — `Assets/Frame.vxcompositor`, named in `OnConfigure` beside the
`PostEffectFactory` registration that makes it bind — plus an all-default, all-commented
`Assets/RenderQuality.vxpreset` for the day a tier needs overriding. The second
`PackageReference` (`Vixen.Rendering.PostFx`) is what those two files cost, and it is the reason
a scaffolded project renders with shadows and a post chain rather than the engine's bare
one-pass fallback. [Choosing a frame](../../docs/guide/rendering/choosing-a-frame.md) is the
knobs-versus-authoring story.

The `Dockerfile` is [doc 17 § Q5c](../../docs/plan/17-app-heads-and-shipping.md) — multi-stage,
chiselled base, non-root — and it builds the **server** variant, because a client in a container has
no display. Client and server are one project (Q5a); `-p:VixenVariant=Server` is the whole of the
difference.

## `vixen-app`

⚠ **Its reason for existing is an absence.** [Doc 17](../../docs/plan/17-app-heads-and-shipping.md)
makes `vixen-app` "the practical test that the `Vixen.Ui` ⇸ `Vixen.Engine` boundary holds", so it
references neither — and in particular it does not reference `Vixen.App`, which would reach
`Vixen.Engine` the easy way and quietly undo the demonstration. A test asserts the absence.

What that host would have done is [`Vixen.Ui.Desktop`](../../Platform/Vixen.Ui.Desktop/README.md),
and the loop is four steps worth naming: pump the platform's events into the document, run the layout
and draw passes, turn the draw list into geometry, record that geometry into a frame. Only the last
of the four knows what a GPU is — which is why `--frames N` means something on a machine with no
Vulkan at all.

⚠ **That assembly is why the absence above is now free.** The template used to carry the loop itself
— `AppHost.cs`, `AppDocument.cs`, `AppInput.cs`, `AppFonts.cs` and eight committed SPIR-V modules,
four hundred lines a scaffolded project owned and nobody wanted — because the alternative was
referencing a host that drags a scene, an ECS world and a fixed-step accumulator behind it. Avoiding
`Vixen.App` used to cost four hundred lines; it costs one `PackageReference` now, and the template is
two files.

### The interface is markup, and the project file says nothing about it

⚠ **`AppShell.vxml` and `Theme/vixen.ui.vcss` are what a new application starts from**, because
`.vxml`, `.vcss` and the utility classes are the intended way to write a Vixen interface and a
template that shipped three hand-written C# files taught the opposite. `Program.cs` is what is left
of the C#: it names the window, hands `UiApplication` the generated stylesheet and says which
component to mount.

⚠ **`Painter.csproj` gained nothing for any of it.** The VXML compiler, the two item types and the
utility stylesheet step all arrive with the one `<PackageReference Include="Vixen.Ui.Desktop" />` —
it depends on `Vixen.Ui.Controls`, which depends on `Vixen.Ui` — because
`Vixen.Ui` and `Vixen.Ui.Styling.Utilities` ship their MSBuild logic in `buildTransitive/`. Adding a
second `.vxml`, a second `.vcss` or a folder of them is not a project-file change, and
`TheApplicationTemplateIsWrittenInMarkup` asserts the project file stays empty of them — a glob or an
`Import` appearing there is the visible symptom of that packaging regressing.

`WhatEachTemplateWritesCompiles` runs the real VXML generator over the markup, so a `.vxml` that does
not compile fails with the line and column *in the `.vxml`* rather than as a missing type in the C#
that mounts it.

## `vixen-lib`

A plain `Microsoft.NET.Sdk` library. **No `Vixen.Sdk`**: a library has no assets to import and no
content to build, so the SDK would add two no-op build steps and a tool dependency for nothing.

It answers to `vixen-lib` and `vixen-library`, because `library` is what the CLI took before this
package existed and breaking it would buy nothing.

## `vixen-mmo`

The only multi-project template, and the reference graph is the whole of it.
[Doc 27 § The three assemblies a game writes](../../docs/plan/27-mmo-framework.md) says why: *"getting
this graph wrong on day one is the kind of mistake that is discovered in month six"*.

```
Kestrel.Contracts   the wire and the shard vocabulary. Seen by everybody, so: no engine, no Orleans
Kestrel.Shared      the rules the client and the realm both run — once, in one assembly
Kestrel.Realm       a shard. Launched with --realm-spec, reports ready on stdout, drains on stdin
Kestrel.Client      the player's half. Nothing from the control plane (ADR-017)
Kestrel.Content     maps and definitions, built once per profile
```

`Contracts` carrying no Orleans reference is ADR-017 made mechanical rather than remembered: the
client physically cannot reach a grain, because the types are not in an assembly it references. A
test asserts each of those edges by reading the project files, because the Roslyn gate below
compiles a multi-project template as one unit and therefore cannot.

**Three of doc 27's eight projects are not scaffolded**: `.Cluster`, `.Orchestrator` and `.Gate` each
need a package that does not exist yet (milestones L1 and L3), and a template pinning a package
nobody publishes is worse than no template at all — the same judgement `vixen-plugin` waited on. The
template grows when they land.

## `vixen-plugin`

[Doc 11 § `Vixen.Editor.Plugin`](../../docs/plan/11-editor.md)'s shape, scaffolded: a
`plugin.yaml` beside a class library, one `IEditorPlugin`, and a command, a menu entry and a panel
added through [`PluginContext`](../../Editor/Vixen.Editor.Plugin/README.md).

```
Kestrel.csproj       a class library, EnableDynamicLoading, one PackageReference
KestrelPlugin.cs     the one public IEditorPlugin the loader looks for
plugin.yaml          what the editor reads before it loads any of the above
```

⚠ **It registers through the context and not through the shell, and that is the whole lesson.**
`context.AddCommand` and `context.Shell.Commands.Add` both work; the second leaves a lambda over the
plugin's own state in a registry the editor holds, which is a reference into the plugin's assembly
and therefore the whole assembly leaked for the session, silently. A first example is what most
people copy, so this one copies correctly. A test asserts the absence.

⚠ **The `id` in `plugin.yaml` is the one field the template cannot fill in.** It must be lower-case
letters, digits, dots and dashes — a reverse-domain name by convention — and a project name
lower-cased would give every scaffold on earth the same id. It ships as `com.example.plugin` with a
comment saying to change it, and the `name` and `assembly` beside it are substituted normally.

⚠ **`api: 0.1` is a literal and goes stale silently.** Before 1.0 the editor refuses a plugin whose
`api` minor differs from `EditorApi.Version`, so the day that moves, every project scaffolded from
this template produces a plugin the editor will not load — and nothing in a build would say so,
because a manifest is data. `TheEditorPluginTemplateDeclaresTheApiThisEditorImplements` compares
the two, which is what turns that into a red test in this repository instead of a bug report.

`EnableDynamicLoading` is in the project file rather than left out: it is what makes a class library
write the `.deps.json` that the plugin's `AssemblyLoadContext` resolves dependencies through, and
copy them beside the assembly. Without it a plugin runs on the machine that built it and nowhere
else. The `Vixen.*` assemblies it copies are harmless — the loader answers every one of them from
the editor's own copy, deliberately.

No `Vixen.Sdk`, for the reason `vixen-app` and `vixen-lib` do without it: a plugin has no assets to
import and no content to build.

## `vixen-tool`

[Doc 17 § Q5d](../../docs/plan/17-app-heads-and-shipping.md)'s console head: the same boot path a
game takes, minus everything that needs a person in front of it. Content validation, CI screenshot
generation, batch conversion, custom pipeline steps.

```
Bake.csproj      Microsoft.NET.Sdk, Exe, one PackageReference
Program.cs       the two calls VixenApp.Run<T> makes, written out
BakeTool.cs      a Game whose OnConfigure turns the head off and whose OnInitialise is the step
```

**One `PackageReference`, and doc 17's "nearly free" is true.** `Vixen.App` chooses the platform, and
what it chooses under `AppConfig.Headless` is
[`Vixen.Platform.Headless`](../../Platform/Vixen.Platform.Headless/README.md). No `Vixen.Sdk`, for the
reason `vixen-app`, `vixen-lib` and `vixen-plugin` do without it: a tool operates on somebody else's
content and has no `Assets/` of its own to import.

⚠ **`Window = null` rather than an invisible window.** A hidden window still asks the platform for a
surface a swapchain could be built on, which a build agent with no display cannot give — so the
failure would be at start-up rather than at the step. `AppBuilder.Build` handles the null case
deliberately, and says so.

⚠ **The frame budget is a default and not an assignment, and that is the one thing about this
template that would have been silently wrong.** `AppConfig.Apply` runs *before* `Game.OnConfigure`,
so `config.MaxFrames = 1` would throw away a `--vixen-frames 120` the operator typed. It has to be
`if (config.MaxFrames <= 0)`. A budget is needed at all because `ExitWhenAllWindowsClose` cannot end
this run — that check is skipped when there is no window, so that a headless run is not over before
it starts.

`TheToolTemplateIsAHeadlessHeadThatEndsAndStillObeysItsCommandLine` asserts all of it by *running*
it: the scaffolded project is compiled, emitted, loaded, and put through the host's own two calls in
the host's own order. Reading the source as text cannot assert an ordering.

## Still to come

**Platform heads.** Doc 17 describes `vixen-game` as producing "platform heads" as well; today it
produces one project and `vixen build --target Android` publishes it.

⚠ **They are blocked on this package's own rules rather than owed, and the block is worth stating
because "nearly free" is what it looks like.** `Samples/01`'s hand-written Android and iOS heads are
`net10.0-android` and `net10.0-ios` projects that reference `Vixen.Platform.Android` and
`Vixen.Platform.iOS` — which is why they are **out of the solution**, and why `Test`, `CheckFormat`,
`CheckApi` and `Pack` never evaluate them. A template cannot follow them there:

- **No conditionals.** Only `sourceName` identity substitution is available (§ *What the templates
  may use*), so heads cannot be opt-in. Every scaffold would carry them, and `dotnet build` on a
  machine without the workloads would fail on a project its author never asked for.
- **No gate.** `TemplateCompiler` compiles a multi-project template as *one* compilation against the
  assemblies this test project references. A `MainActivity.cs` would need `Vixen.Platform.Android` in
  that list — a `net10.0-android` assembly, so the whole test project would need the workload — and
  without it the heads would be the only scaffolded C# nothing compiles, which is the exact failure
  this package's tests exist to prevent.

Making them possible means one of: a template-engine conditional and a second implementation of it in
`TemplateCatalog`; a separate `vixen-game-android` template, which is a conditional spelled as a name;
or heads that ship ungated. None is free, and the choice is a decision rather than a task.

Licensed under Apache-2.0.
