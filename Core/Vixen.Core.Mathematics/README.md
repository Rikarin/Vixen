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

## Equality is exact; `NearEqual` is the approximate one

`==` compares component by component with float `==`, so `NaN != NaN` and `-0f == 0f`. Approximate
comparison is always spelled out — `Vector3.NearEqual(a, b)` — so nobody has to wonder which kind a
given `==` meant. A tolerance hidden inside `==` is how a physics bug becomes unreproducible.

`GetHashCode` uses `float.GetHashCode`, which normalises both zeros and all NaN payloads, so equal
values always hash equally.

## Still to come

The types ADR-003 lists that are not built yet: `Plane`, `Ray`, `BoundingBox`, `BoundingSphere`,
`BoundingFrustum`, `Rectangle`, `Viewport`, `Color`, `Color3`, `Color4`. They are the next slice and
they build on everything here.

`Half` is **not** planned as a Vixen type: `System.Half` has existed since .NET 5, is
hardware-accelerated, and re-declaring it would buy nothing.

The SIMD paths (matrix multiply, `TransformVector4`, the bulk transforms) are written but not yet
measured — `Benchmarks/Vixen.Benchmarks.Math` is owed, and until it exists the scalar fallbacks are
the reference and the vectorised paths are asserted only to agree with them.

Licensed under Apache-2.0.
