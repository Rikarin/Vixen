# Vixen.Templates

The `dotnet new` templates, and the one tree of files `vixen new` writes from.

```bash
dotnet new install Vixen.Templates

dotnet new vixen-game -n Asteroids      # a game: a Game subclass, a host, Assets/, a Dockerfile
dotnet new vixen-app  -n Painter        # an application: Vixen.Ui, a window, and no engine
dotnet new vixen-lib  -n Physics        # a library either of them can reference
dotnet new vixen-mmo  -n Kestrel        # a dedicated-server game: contracts, rules, realm, client
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

What that host would have done is `Program.cs` and `AppHost.cs`, and the loop is four steps worth
naming: pump the platform's events into the document, run the layout and draw passes, turn the draw
list into geometry, record that geometry into a frame. Only the last of the four knows what a GPU
is — which is why `--frames N` means something on a machine with no Vulkan at all.

It carries four SPIR-V modules, byte for byte the ones `Samples/02-HelloUi` and the golden-image
fixtures use. That is the state of the world rather than a design: turning shader source into modules
belongs to Raven, and until that path is wired a caller hands the renderer whatever it has.

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

## Still to come

**`vixen-plugin` is owed, and it is no longer blocked.** It was left out because there was nothing
to scaffold against: a template pinning a `PackageReference` nobody publishes produces a project
that will not restore, which is worse than no template — it fails at the one moment a person has no
context to debug it. `Vixen.Editor.Plugin` then landed in the same wave (W0-12), so the package
exists and the only thing missing is the template. What it scaffolds is
[doc 11 § `Vixen.Editor.Plugin`](../../docs/plan/11-editor.md)'s shape: a manifest beside an
assembly, and a registration against the extension points for commands, panels, inspectors and
importers.

**`vixen-tool`** — doc 17 § Q5d's headless batch head — is likewise unblocked
(`Vixen.Platform.Headless` is built) and simply not written yet.

**Platform heads.** Doc 17 describes `vixen-game` as producing "platform heads" as well; today it
produces one project and `vixen build --target Android` publishes it. The per-platform sibling
projects `Samples/01` carries by hand are what that line means, and they are owed.

Licensed under Apache-2.0.
