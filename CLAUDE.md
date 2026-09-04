# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

`README.md` covers the layout, the documentation split and the non-negotiables — read it first. This
file is what the README does not say: how to run things, how this codebase decides a claim is proved,
and the architecture that only emerges from reading several files at once.

## The working agreement

Standing, for every session, unless the current session says otherwise.

1. **Audit before fixing.** When pointed at a subsystem, survey it first and report findings with
   `file:line` before changing anything. Turn the findings into tasks and show the list.

2. **Use parallel agents in isolated worktrees for anything decomposable.** Write briefs that name the
   specific failure classes to look for, grounded in bugs this engine has actually had. ⚠ A brief
   saying *"look for bugs"* finds nothing; one saying *"check whether the first sample shell can be
   smaller than a texel"* finds the bug.

3. **Every agent verifies before it fixes.** Claims Jiu makes, claims in code comments, claims in plan
   documents — confirm or refute each with evidence, and say which turned out wrong. **A refuted claim
   is worth as much as a fix.** Where practical, sabotage-test: show the new test fails against the old
   code.

4. **Merge each task as it lands** — merge the branch, run the affected suites, merge to master. Do not
   batch them.

5. **Gates: targeted `dotnet test` while working; the full `./build.sh` gates once, on master, at the
   end.** Per-branch runs cannot see cross-branch drift. Run each gate as its own unpiped command and
   read its tail — do not pipe a batch through `grep`, it misreports.

6. **Report honestly**: what landed versus where you stopped, what stayed owed and why, what you could
   not verify. *"I stopped here"* beats a half-finished claim of done. Commit before any optional
   verification step, so an interruption cannot cost the work.

7. **"Tests pass" is not evidence for a visual defect.** If the symptom is visual, get a picture —
   build the pre-fix commit and A/B the same view.

8. **Check memory first** for conventions and traps; write new ones when something durable is learned.

9. **When Jiu asks a question rather than for a change, answer it.** Do not start fixing until asked.

10. **Ask when a decision needs domain knowledge you do not have.**

## Build and test

`./build.sh <targets>` (`build.cmd` on Windows) is the entry point; targets are in `build/Build*.cs`.

```bash
./build.sh Compile                                  # whole solution
./build.sh Test                                     # every test project
dotnet test Core/Vixen.Ecs.Tests -c Debug --nologo   # one project — prefer this
dotnet test Core/Vixen.Ecs.Tests --filter "FullyQualifiedName~TheNameOfIt"
```

Test projects are siblings of what they test (`Vixen.Ecs` / `Vixen.Ecs.Tests`), so the project path is
derivable from the assembly name in any failure.

⚠ **Do not pipe `build.sh` through `tail`, and do not append a command after it.** The reported exit
code becomes the pipeline's or the last command's, so a run that printed `Build failed` exits 0.
Redirect to a file and read the target-status table:

```bash
./build.sh CheckApi > /tmp/gate.log 2>&1
sed -n '/Target.*Status/,/═══/p' /tmp/gate.log
```

⚠ **Gates are per-tree, so run them after the last merge, not before the first.** A `CheckFormat`
regression reached master twice this way; three separate agents rediscovered it independently.

`--since <ref>` narrows a target to what changed — `./build.sh CheckFormat --since master` formats
only the projects that own the diff, `./build.sh AffectedTests --since master` runs only the test
projects reachable from them (one at a time), and `./build.sh AffectedProjects --since master` prints
both sets without running anything. These are inner-loop conveniences and **never the gate**:
narrowing cannot see a file a neighbour's merge broke, and a `ProjectReference` closure cannot see a
golden image, a content bundle, an `.rvn` import closure or a test that walks the repository.
⚠ `--include` is *not* what makes `CheckFormat --since` fast — scoping the input scopes the analysis
and not the workspace load, and the load is essentially all of the cost.

⚠ **Run gates and test projects one at a time** on a developer machine. The whole-solution `Test`
target runs ~176 assemblies concurrently and will saturate a laptop; runs have been SIGTERM'd
mid-compile under that load. `build.sh` now enforces the *between-checkouts* half of that: the
expensive targets take a machine-wide advisory lock, so a second sweep — an agent in another
worktree — waits and says what it is waiting for instead of competing. The cheap targets
(`CheckStrings`, `AffectedProjects`, …) never queue, CI never locks, and `VIXEN_NO_BUILD_LOCK=1` is
the escape hatch. Nothing serialises `dotnet test` run directly, which is the point: that is the one
you are supposed to be able to run.

### Configuration, which is not uniform

`Configuration` defaults to **Debug locally and Release in CI** (`build/Build.cs`). But `Docs`,
`CheckApi` and `CheckShaders` **hard-code Release** whatever you pass — the doc generators resolve
through `ProjectReference` and silently drop ~300 types in any other configuration.

⚠ So a manual `dotnet test -c Release` is not what the `Test` gate does locally, and a conclusion like
"this test never runs in CI" drawn from one is wrong. Reproduce a gate result in the gate's own
configuration.

### GPU and golden tests

- ⚠ **`--vixen-capture` or `--vixen-offscreen` is what buys a real GPU device.** Without one a
  headless run falls back to the Null device on *every* platform, exits 0, and prints
  character-for-character identical healthy counters. `--vixen-offscreen` is the one to reach for
  when the run wants numbers rather than a picture. Confirm the adapter
  (`Vulkan device created on '<name>'`) before trusting any GPU number — though a run given either
  flag now *refuses* the Null device rather than falling through to it.
- `VIXEN_REQUIRE_VULKAN=1` turns a missing device into a failure rather than a silent skip. It does
  **not** cover missing *capabilities* — several suites skip legitimately on MoltenVK.
- ⚠ **Read the `Total`, not the pass count.** A crashed suite still prints `Passed! Failed: 0`; only a
  short total reveals it. Read the skip count too.

## How this codebase decides something is proved

These are enforced by review and by the commit record, and ignoring them produces work that looks done
and is not.

- **A build error is not a red test.** If a sabotage fails to compile, that attempt proved nothing.
- ⚠ **A sabotage that leaves the test green proves just as little.** Both forms recur constantly. If
  you break the thing a test covers and the test still passes, the test is the defect.
- **Prefer a property expressed as work or order over elapsed time.** Wall-clock budgets calibrated on
  an idle machine are this repo's single largest flake source; the established replacements are a
  deterministic counter, then a differential measured on the same machine at the same moment, then an
  absurd ceiling whose comment says plainly it is a hang check and not a bound.
- **A predicate that cannot be false is worse than the flake it replaced.** For any new assertion, show
  both halves: false before the work completes, *and* red under sabotage.
- **Verify the instrument first.** Ask what a gate prints on the day it does not run. If the answer is
  "success", fix that before trusting it. Real examples: a comparator that called three empty manifests
  identical, a parity test that checked a C# re-implementation rather than the shader, a test double
  more permissive than the runtime, an architecture rule with no false positives that was satisfied by
  exactly the defect it was meant to catch.
- **Verify with a picture where the output is a picture.** Three separate wrong-frame bugs shipped past
  clean counters. Prefer a closed-form oracle (a shape whose covered area must halve) over eyeballing.

## Architecture worth knowing before editing

**The frame is data.** A `StandardFrame` asset expands into a compositor document, which builds a
render graph of passes; `RenderQuality` tiers (`.vxpreset`) and `.vxlook` profiles layer over it.
Changing what draws usually means changing a document or a node, not a call site.

⚠ **There are two renderers and both must be wired.** `WorldRenderer` (game) and
`EditorWorldRenderer` (editor) assemble features separately — the editor's extraction is hand-built and
`Register` never runs there. A feature added to one and not the other silently does nothing in the
other, which has happened repeatedly.

**Render features compose.** `MeshRenderFeature` hosts `SubRenderFeature`s (skinning, morph). A
sub-feature that rewrites draw state must rewrite it consistently — e.g. `VertexBuffer` and
`VertexOffset` together, since an index is relative to the offset.

**Virtualized geometry is per-mesh, shared by instances.** A cluster page is registered once per mesh
and every instance reads the same bytes — that sharing is what makes streaming affordable. So
per-instance deformation on that path is a **gather** (decode a shared vertex, transform by this
instance's data), never a scatter. A mesh with a cluster hierarchy takes an entirely different path and
does not reach the suballocated features.

**Assets flow** importer → `AssetDatabase` (with `.meta` sidecars) → content build → catalog/bundles.
Importers are `async ValueTask` and block on I/O; that is why they use the thread pool rather than the
job scheduler, which cannot replace a blocked worker.

**`Vixen.Ui` is reactive and process-wide in one place:** `Core/Vixen.Ui/Strings.cs` holds a
`static readonly Signal<StringCatalog>`, the only static reactive node. Every `@expr` showing a word
attaches an `Effect` to it, so panel churn is graph churn — and the graph is single-threaded by
contract.

**The renderer works in cd/m².** ⚠ A pass lit by an authored 0–1 tint is pixel-identical to a pass that
never ran. Scale by ~1e4 before concluding a pass is broken.

**Zero often means "off".** A frame that draws but looks wrong is usually a zeroed struct field whose
zero is a valid-looking value.

## Raven (the shader language)

- ⚠ **A newline ends a statement.** `x = x` on one line and `+ y` on the next is *two* statements, the
  second discarded. Trailing the operator is caught by `RVN1001`; this arrangement was not, and was
  silently wrong in shipped shaders.
- ⚠ **Unregistered permutation trap**: a value in `Permutations` that is not *also* in
  `PermutationKeys[shader]` never reaches the compiler — the variant silently takes the `.rvn` default.
- After editing a published `.rvn`, regenerate with `VIXEN_REGENERATE=1` on `LibraryReflectionTests`;
  generated binding keys come from the `reflect.json`.
- `./build.sh CheckShaders` recompiles the shaders whose `.spv` is **committed** — the three the editor
  loads out of `Raven/Library/Terrain` (from their import closure), plus the four `.rvn` written beside
  `Vixen.Editor.Host` and `Vixen.Ui.Desktop` — and reports whether any committed `.spv` drifted. ⚠ It
  is **not** every library shader: the library has over a hundred and `LibraryReflectionTests` is what
  binds it whole. `ci.yml`'s `checks` leg runs the target, so drift no longer depends on remembering.
- Diagnostics: a new rule needs a positive test **and** an id-named negative, and the negative is proved
  by widening the rule in the compiler until the fixture goes red, then reverting. `Raven/README.md`
  carries the ranking heuristic and the traps.

## Gates that fail for non-obvious reasons

- **`Docs`** refuses any new public type with no guide page *and* no line in `docs/DocsExempt.txt`.
- **`CheckApi`** fails on an unapproved public addition **and** on a silent removal; the baseline moves
  with the code.
- **`CheckStrings`** fails on a declared string id used nowhere, and on a call site that rebuilds an id
  a declaration class already declares.
- **`CheckArchitecture`** globs directories rather than reading the solution, so it sees the
  out-of-solution mobile/web projects that `Test`, `CheckFormat`, `CheckApi` and `Pack` never evaluate.

## Conventions

- **`docs/overview.md` is the state and wins** where a `docs/plan/` document disagrees; plan documents
  record intent, not what exists. Per-module `README.md` files carry the reasoning for that subsystem,
  including what it deliberately does not do — they are the best entry point into an unfamiliar area.
- **GitHub issues on `Rikarin/Vixen` are the task tracker**, and every new task is filed as one —
  `gh issue create --repo Rikarin/Vixen …`. ⚠ **Always pass `--repo`**; `gh` otherwise infers it from
  the working directory, which is wrong inside an agent's worktree.
  - **Work an agent turns up is filed too, not just work Jiu names.** An agent that finds a second
    defect while fixing the first, or refutes a claim and uncovers the real one, files an issue for it
    rather than widening its own change or leaving it in a report nobody re-reads. Search the open
    issues before filing, so a finding two agents hit independently lands once.
  - **An issue is closed when the work is finished *and merged to master*** — not when a branch is
    green and not when an agent says it is done. Close it with the real outcome, including
    "refuted, not fixed" and "closed by deciding not to".
  - ⚠ **If it is not complete, the issue says what is left**, in the issue rather than in a session
    that ends. Partly-done work is a comment naming what landed, what did not, and why — and the issue
    stays open. An issue closed on a half-landed change is how a finished-looking thing nothing calls
    gets created.
- **Commit messages are declarative and explain the insight, not the diff**, and mark anything
  surprising or previously believed false with ⚠. Read `git log` before writing one.
- **The commonest defect here is a finished thing nothing calls.** Before assuming a feature works,
  grep for *callers*, not for the type.
