# references/

Read-only third-party source, cloned here so that "how did *they* solve this" is a `grep` away
rather than a browser tab away. **Nothing here is built, restored, referenced or shipped** — the
directory is excluded from the solution, from every glob, and from CI.

## Why this is not a set of submodules

[Doc 02](../docs/plan/02-repository-layout.md) describes these as submodules. They are deliberately
not committed as such, because a submodule makes every clone of Vixen pull several gigabytes of other
people's history before it can build — including on a CI runner that will never read a line of it.
The trade only pays if you are actually consulting them, so cloning is left as a local decision.

The `.gitignore` already excludes everything here except this file, so a clone leaves no trace in
`git status`.

## Getting them

```bash
cd references

git clone --depth 1 https://github.com/stride3d/stride.git         stride
git clone --depth 1 https://github.com/genaray/Arch.git            arch
git clone --depth 1 https://github.com/facebook/yoga.git           yoga
git clone --depth 1 https://github.com/ru-ace/Flexbox.git          flexbox
git clone --depth 1 https://github.com/fedeAlterio/SignalsDotnet.git signals-dotnet
git clone --depth 1 https://github.com/PurrNet/PurrNet.git         purrnet
```

## What each one is for

| Clone | Consulted for | ADR |
|---|---|---|
| `stride` | The architectural spine — asset pipeline, render features, effect system. Read, not copied. | — |
| `arch` | The archetype-chunk ECS model, and the benchmark suite Vixen's ECS has to match. | ADR-004 |
| `yoga` | **The conformance fixtures**, which get ported before the flexbox implementation is written. The most valuable thing in this list. | ADR-006 |
| `flexbox` | A C# flexbox implementation, as an algorithm reference. It is .NET Framework 4.6 and allocation-heavy; the *algorithm* is what is wanted. | ADR-006 |
| `signals-dotnet` | The reactive graph model behind the UI framework's signals. | ADR-007 |
| `purrnet` | Networking: replication, interest management, the RPC surface. MIT. | doc 16 |

[Doc 14](../docs/plan/14-roadmap.md) singles these out for a reason: *external oracles judge
correctness without you having to*, which is the specific defence against AI-assisted code that reads
plausibly and is wrong. The Yoga conformance suite is the clearest case — a red test suite driving an
implementation is a completely different experience from writing three thousand lines and then
finding out.

Every repository above is under its own licence. Nothing here is redistributed; the attribution that
matters is for what Vixen actually *depends on*, which is tracked separately per ADR-015.
