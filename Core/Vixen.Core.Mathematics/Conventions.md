# Vixen mathematics conventions

Every disagreement about a sign flip gets settled by pointing at this file. It is short on purpose.
Each line is asserted by a test in `Vixen.Core.Mathematics.Tests/ConventionTests.cs`, so it cannot
quietly stop being true.

The authority is [ADR-003](../../docs/plan/01-technology-decisions.md#adr-003--vixen-owns-its-math-types).
The shader half of the same conventions is
[07 § E](../../docs/plan/07-raven-shader-pipeline.md#e-conventions-raven-must-bake-in), which derives
why a `ColMajor`-decorated shader matrix and our row-major host storage are the same bytes. Read that
one before arguing that the GPU disagrees with this file; it does not.

## The table

| | Vixen |
|---|---|
| Handedness | **Right-handed** |
| Up axis | **+Y** |
| Forward | **−Z** (so +Z is backward, +X is right) |
| Vector convention | **Row vector** — `v' = v · M`, vector on the left |
| Matrix storage | **Row-major**, `M11..M44`, row *i* contiguous |
| Translation | **`M41`, `M42`, `M43`** — the last *row* |
| Composition order | `local * parent`, read left to right |
| Rotation direction | **Counter-clockwise** looking down the axis toward the origin |
| Euler order | **Yaw (Y) → Pitch (X) → Roll (Z)**, applied in that order |
| Depth range | **0 to 1**, and **reverse-Z**: near maps to 1, far to 0 |
| UV origin | **Top-left**, V increasing downward |
| Colour space | Linear working space; sRGB is decoded by the view format, never by hand |

## What each of those actually means

**Right-handed, Y-up, −Z forward.** Point the right hand's thumb along +X and index finger along +Y;
the middle finger points along +Z, which is *out of the screen*, so the direction a camera looks is
−Z. This is what OpenGL, Vulkan-with-a-flipped-viewport, Stride, and Blender use.
`Vector3.Forward` is `(0, 0, -1)`; there is no `ForwardLH` and no flag to change it.

**Row vector, row-major, translation in the last row.** These three go together and are one decision,
not three. `v · M` puts the vector on the left, which means the translation has to be the last row for
`(x, y, z, 1) · M` to add it. In memory the sixteen floats run `M11 M12 M13 M14 M21 …`, so a row is
contiguous and `M41..M43` is the translation triple sitting together at offset 48.

**Composition reads left to right.** `world = local * parent` — the transform closest to the object is
written first. `v * (A * B)` equals `(v * A) * B`, so a chain reads in the order it is applied. If you
have used a column-vector engine (`M · v`, DirectXMath's default, glm), everything is transposed and
composition reads right to left; the arithmetic is identical, the writing order is not.

**Reverse-Z.** `Matrix4x4.PerspectiveFieldOfView` maps the near plane to depth **1** and the far plane
to **0**, and the depth test is `GREATER`. This is not an optimisation to enable later — floating-point
depth has its precision concentrated near zero, and putting the far plane there is what makes distant
geometry stop z-fighting. Anything that clears depth clears it to **0**, not 1.

**Top-left UV origin.** `(0, 0)` is the top-left texel. Raven emits `OriginUpperLeft`, and image
importers write rows top-down, so no flip happens anywhere in the pipeline.

## Why these are `readonly struct` and not `readonly record struct`

ADR-003 says `readonly record struct`. The types are plain `readonly struct`, because the ADR's *other*
requirement — public readonly **fields**, so `ref` returns and `Unsafe.As` reinterpretation are legal
and free — takes everything a record would have given:

- A positional record declares *properties*, not fields, which is the thing the ADR rules out.
- With readonly fields and no positional parameters, `with` has nothing it can set, so it is dead.
- `Equals`, `GetHashCode` and `==` are hand-written anyway (next section), so the synthesised versions
  would be replaced.
- `ToString` is hand-written too, so the `PrintMembers` output is replaced.

That leaves `record` contributing nothing but a keyword. Only the wording of the ADR changes; every
property it was asking for is still here.

## Equality is IEEE, and hashing is not

`operator ==` compares component by component with float `==`. So:

- `NaN != NaN`, as IEEE requires. A vector with a `NaN` in it equals nothing, including itself.
- `-0f == 0f`.

`GetHashCode` uses `float.GetHashCode`, which normalises both zeros to the same hash and all `NaN`
payloads to another. Equal values therefore always hash equally, which is the only direction the
contract runs; two `NaN` vectors hash the same while comparing unequal, which is permitted and is what
every `System.Numerics` type does.

**`==` is exact and is never an approximate comparison.** Approximate comparison is
`MathUtil.NearEqual` and the per-type `NearEqual`, spelled out at the call site so nobody has to
wonder which one a given `==` meant. A tolerance hidden inside `==` is how a physics bug becomes
unreproducible.

## Interop

`System.Numerics` conversions are `implicit` in both directions for `Vector2/3/4`, `Quaternion` and
`Matrix4x4`, so calling BCL APIs costs nothing at the call site. **`Matrix4x4` conversion is a
reinterpretation, not a transpose** — `System.Numerics.Matrix4x4` is also row-major with translation in
`M41..M43`, so the bytes already agree.

`Silk.NET.Maths` conversions deliberately live in `Vixen.Graphics`, not here: this assembly does not
reference Silk.NET, and it is not going to start.
