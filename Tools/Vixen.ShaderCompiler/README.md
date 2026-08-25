# Vixen.ShaderCompiler

The one place Raven and the runtime are allowed to be in the same room.

Neither may reference the other. `Vixen.Raven` is a compiler that should be usable without the
engine, and `Vixen.Shaders` ships in every game — linking a parser, a lowerer and two backends into
that is exactly what "zero runtime shader compilation" is supposed to make impossible. So this is a
build-time library that references both, and nothing that ships references it.

## What is in it

| | |
|---|---|
| `EffectTranslator` | Raven's `CompiledEffect` → the engine's `EffectData`. Every rename lives here: a bare `exposure` becomes `Lighting.exposure`, and `lights[].color` becomes one key per element |
| `RavenEffectCompiler` | An `IEffectSource` that compiles a variant in process. Sources are parsed once and the compilation is redone per variant, because that is exactly what differs. `FromSources` takes the texts rather than paths, for a caller whose shader was never a file — the shader graph generates one per node preview, and a temporary file per keystroke is not a compilation's business |
| `PermutationClosure` | Every variant a shader actually has, found by compiling until the answer stops changing |
| `EffectBundleBuilder` | The bundle a shipping build loads, from a manifest, a closure, or both |

## The closure is a fixed point, and it has to be

Raven reports which permutation keys a compilation *read*, not which were declared — that is the
difference between twenty flags meaning eight shaders and twenty flags meaning a million. The
complication is that the answer depends on the values: a flag guarded by another flag is unread until
the outer one is on. So one compilation with the declared defaults undercounts, and the cross product
of everything declared overcounts by orders of magnitude.

Compile the defaults, see which keys were read, enumerate over those, and if any of *those*
compilations read a key that was not in the set, put it in and start again. The set only grows and is
bounded by what the shader declares, so it terminates — having compiled exactly the variants that
exist.

A shader that needs the third pass is reported as `Dependent`, and that is a warning rather than a
statistic: the engine picks the keys for its own cache key out of one checked-in reflection, and that
reflection came from a compilation that never reached the inner flag. Such a shader has variants no
draw can ask for. It wants restructuring so the inner key is read unconditionally.

## Numbers need help

A `bool` has two values and enumerating them is complete. An `int` does not, and which values matter
is project knowledge — a light-count bucket is 4, 16 and 64 because of what the project's scenes look
like, which is not in the shader. Unless a caller supplies a domain, a numeric key contributes its
declared default alone. That is honest rather than clever: the bundle comes out missing the variants
nobody asked for, and a run against it reports them as misses by name.

## Where the manifest comes from

`EffectSystem.Requests` is every key a run asked for. Play the game against a compiler, write the
manifest, build the bundle, and the next run compiles nothing — which is the exit criterion in
docs/plan/06, and is asserted as a test in `Vixen.ShaderCompiler.Tests`.

`vixen content build` is what a project actually runs: `ProjectSettings/Shaders.effects.json` in,
`shaders.effects` beside the catalog out. See [`Vixen.Cli`](../Vixen.Cli/README.md).
