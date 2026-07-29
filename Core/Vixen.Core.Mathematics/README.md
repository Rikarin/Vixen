# Vixen.Core.Mathematics

The engine's own vector, matrix and quaternion types. Vixen owns them rather than using
`System.Numerics` or `Silk.NET.Maths` — ADR-003 — because the first has no 3×3 matrix, no bounding
volumes, no colour or integer vector types and no say over storage order, and the second is generic
over `IFloatingPoint`, which blocks the SIMD intrinsics and multiplies generic instantiations under
NativeAOT.

## Read `Conventions.md` first

[`Conventions.md`](Conventions.md) states the handedness, the storage order, the multiplication
order, the depth range and the UV origin, once. Every line of it is asserted by
`ConventionTests`, so it cannot quietly stop being true. The short version:

> Right-handed, Y-up, **−Z forward**. **Row vector** (`v * M`) with **row-major** storage and the
> translation in **`M41..M43`**. Composition reads left to right: `world = local * parent`.
> **Reverse-Z** depth over `[0, 1]` — near maps to 1, far to 0, and depth clears to 0.

## What is here

| | |
|---|---|
| `MathUtil` | Constants, `NearEqual`, clamping, the interpolation curves, angle wrapping, power-of-two and alignment helpers. |
| `Vector2`, `Vector3`, `Vector4` | Explicit layout, public readonly **fields**, IEEE operators. `Vector4` reinterprets to `Vector128<float>` for free. |
| `Int2`, `Int3`, `Int4` | Pixel, texel and grid coordinates, where a float would invite a rounding decision with no right answer. |
| `Quaternion` | Rotations. Composition reads left to right, matching the matrices. |
| `Matrix4x4` | The transform. Construction, composition, inversion, decomposition, the projections, and bulk transform helpers. |
| `Matrix3x3` | Rotation and scale without translation — and `Normal`, the inverse transpose a shader needs for normals under non-uniform scale. |
| `Plane`, `Ray` | Signed distances, classification, and ray casts against planes, boxes, spheres and triangles. |
| `BoundingBox`, `BoundingSphere`, `BoundingFrustum` | Culling and spatial queries. The frustum's plane extraction is reverse-Z-correct, which is not the same as the textbook derivation. |
| `Rectangle`, `Viewport` | The 2D half: half-open containment so tiles do not overlap, and `Project`/`Unproject`/`GetPickingRay`. |
| `Color`, `Color3`, `Color4`, `ColorSpace` | 8-bit storage and linear working values, with the sRGB boundary spelled out at every crossing. |
| `ExactPredicates` | Orientation and in-sphere, answered exactly — see below. |
| `DelaunayTetrahedralization` | Bowyer–Watson over those predicates, and the completeness check that says the result is the tetrahedralisation rather than most of one. |

## Two of these answer a sign, not a number

`ExactPredicates` is the one place in the library where "close enough" is not a tolerance to be
chosen but a category error. `Orient3D` asks which side of a plane a point is on and `InSphere` asks
whether a point is inside a circumsphere; both have three possible answers, and a floating-point
determinant that comes back `-1e-19` where the truth is `0` has not made a small error — it has given
the wrong one. A tetrahedralisation built on wrong ones is not a slightly wrong mesh, it is not a
mesh.

Exact does not mean slow. Each predicate evaluates in `double` alongside a bound on its own rounding
error, and takes that answer whenever the value is further from zero than the bound — which is
essentially always. Only when the two overlap does it re-evaluate in `BigInteger` over the inputs
rescaled to integers, which is lossless because a binary float already *is* an integer times a power
of two. `InSphere` also has an overload that breaks the cospherical tie by point index, since zero is
a real and frequent answer — every axis-aligned grid is cospherical eight points at a time — and a
construction that has to decide something there needs the same decision every time it asks.

`DelaunayTetrahedralization` is the consumer that motivated them. It exists here rather than in
`Vixen.Rendering`, where its first caller lives, because nothing about it is rendering: it is a
determinant, a walk and a cavity.

## Equality is exact; `NearEqual` is the approximate one

`==` compares component by component with float `==`, so `NaN != NaN` and `-0f == 0f`. Approximate
comparison is always spelled out — `Vector3.NearEqual(a, b)` — so nobody has to wonder which kind a
given `==` meant. A tolerance hidden inside `==` is how a physics bug becomes unreproducible.

`GetHashCode` uses `float.GetHashCode`, which normalises both zeros and all NaN payloads, so equal
values always hash equally.

## Colour: nothing converts behind your back

`Color` holds four bytes and **does not declare a colour space**, because the bytes on their own do
not have one. Every conversion says which it means: `ToColor4()` divides by 255 and nothing else,
`ToLinear()` also decodes sRGB. Colours a human picked — every hex code, every colour-picker value —
are sRGB and need `ToLinear()`. `#808080` is 0.216 linear, not 0.5, and treating it as 0.5 is what
washes a render out in a way nobody can trace back.

`Color3` and `Color4` are linear and deliberately **unbounded above**: an HDR light is a colour past
1, and clamping before tonemapping throws away the range tonemapping exists to compress.

## Still to come

`Half` is **not** planned as a Vixen type: `System.Half` has existed since .NET 5, is
hardware-accelerated, and re-declaring it would buy nothing.

The SIMD paths are measured, and measuring them found a real problem — the first run said the
vectorised matrix multiply was **1.7× slower** than the scalar fallback it was written to replace,
because reading rows through the `Row1`…`Row4` properties compiles to a gather rather than a
sixteen-byte load. Fixed, and now 2.8× faster than scalar and within 30% of `System.Numerics`. The
numbers and the story are in [`Benchmarks/Vixen.Benchmarks.Math`](../../Benchmarks/Vixen.Benchmarks.Math/README.md).

## One C# wrinkle worth knowing

A target-typed `new(…)` carries no type, so overload resolution cannot use its arguments. Where the
natural call is `new(…)` and the overloads are peers, the API avoids the collision by name —
`Matrix4x4.FromScale(Vector3)` and `Matrix4x4.FromUniformScale(float)`. Where callers normally pass a
variable, the overloads stay (`Rectangle.Contains`, `BoundingBox.Contains`) and a `new(…)` call site
just has to name the type.

Licensed under Apache-2.0.
