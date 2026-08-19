# Vixen.Rendering.Reflections

The reflections of [docs/plan/19](../../docs/plan/19-lighting-and-global-illumination.md) § L5 —
the layer that reuses everything below it, which is the section's whole argument for building it
last. A mirror ray marches L1's tracer, a hit answers through L4's radiance seam, a rough surface
reads L2's field instead of tracing, and a miss asks a fallback that is doc 06's reflection probes'
new job. Written first and device-free, the discipline every layer below followed.

## One method, four answers, and every one of them is somebody else's

`TracedReflections.Reflect(position, normal, view, roughness)` — `view` incident, camera *toward*
surface, because the other convention reflects the camera instead of the scene and looks plausible
everywhere. The mirror direction is the textbook `v − 2(v·n)n`, pinned by a closed form.

- **A sharp hit** answers through `IRadianceSource` — L4's seam. Hand it
  `SurfaceCacheRadiance` and a reflection carries direct light, emissive and every bounce the
  radiosity folded in, with nothing in this package knowing the cache exists; a test holds a
  mirror against a cached wall and reads the store's own outgoing convention back.
- **A sharp miss** asks `IReflectionFallback`. This is where doc 06's probes plug in: their row
  carries "⚠ blended against the sky rather than against a second probe", and traced reflections
  invert the arrangement — the trace answers the near field, the probe becomes what a miss sees,
  the far field it is actually good at. `SkyFallback` is the fallback of a project with no probes,
  honestly.
- **A rough surface** — at and above `RoughnessThreshold` — reads the irradiance field about the
  *mirror direction*: past the threshold a GGX lobe's width is most of the hemisphere, one mirror
  ray through it is a sample rather than an estimate, and the cosine-weighted average the field
  already stores is the wide-lobe limit of the same integral, amortised. The discriminating test
  puts a wall where the mirror ray would hit: below the threshold the wall appears, at it the
  field answers and the wall does not — rough is a different read, not a darker mirror.
- **A rough surface with no field** falls back whole, because a quiet zero would hide that the
  scene never covered the position.

## The bias is why a mirror is not its own reflection

A mirror ray leaves the very surface the march would first sample, at distance nothing — without
`Bias`, every reflection is the reflector's own colour. The test holds both behaviours, so the
why survives the parameter.

⚠ **The `Bias` cannot reach the screen march, and that is a second defect wearing the same face.**
A mirror ray leaves at the angle it arrived at, so on a grazing reflector it hugs the surface it
left; a cell crossing is an *interval*, and the first one begins standing on the origin's own
texel, so the shell test is true before the ray has gone anywhere. What has to be cleared is the
texel's own depth span, not a distance along the ray. Both kernels hold the origin's texel to the
shell (`ScreenSpaceTrace`'s `SurfaceBias`, shared with the screen probes), and
`AMirrorDoesNotReflectItselfOnTheDevice` states it over a depth buffer the reflectors are the
geometry of: a colour that encodes its own pixel, and no texel may come back holding it.

## The band, so a roughness map does not draw the threshold

`RoughnessBlend` cross-fades the traced answer into the field's over a stated band ending at the
threshold: at the band's midpoint the answer is the midpoint of the two, which no single path
produces and which is exactly what the closed form holds — on this reference and on the kernel
alike. Inside the band both reads run, which is the band's honest cost.

## The device half, through the slots everything already shares

`ReflectionTrace` (Raven `Reflections` package, driven by `Vixen.Rendering.Lighting`'s
`ReflectionTraceFill`) reflects per texel: the march through `IDistanceFieldSource`, the hit
through `ISurfaceCacheSource`, the rough read through `IIrradianceSource`, and the miss through
`IReflectionMissSource` — a new slot whose seat is doc 06's reflection probes, taken without the
kernel changing a line; `SkyMissSource` sits in it today, which is honestly what every reflection
sees beyond the probes anyway. The device test holds one dispatch against this reference across
every answer it distinguishes — sharp hit on an emissive wall through the cache, sharp miss to the
sky slot, the band mixing both, the field whole, and an invalid texel — within a stated hundredth,
measured at four thousandths, all of which is the field read's hardware trilinear: the march and
the cache agree to the bit.

## The probes take the miss seat, and the screen answers first

`ReflectionProbeMissSource` is doc 06's probes behind the miss slot — the same parallax
corrections, the same inward-measured weight, first non-zero weight wins — and
`Vixen.Rendering.Lighting.ReflectionProbeMiss` is its CPU pair: one class answers the reference's
misses and writes the kernel's bindings, so the two cannot drift apart quietly. That is the
"blended against the sky" caveat retired for reflections: the fade is between two kinds of far
field, never a reflection vanishing. And the sharp path asks the screen before the field:
`ScreenSpaceTrace` says *where* it stopped a ray, the frame's colour at that pixel is the
reflection — SSR reduced to its arithmetic — and the kernel runs the same march sample for
sample, compared at 1e-4 against this reference.

## Not yet, and named so the absence is a decision

⚠ **Both entries this section used to hold have landed, and one of them landed everywhere except
here.**

- **The HZB screen march** is built. `ScreenSpaceTrace.Pyramid` switches the reference march to a
  hierarchical DDA over `ScreenDepthPyramid`, and the device kernels take a real Hi-Z chain —
  `ReflectionTraceFill` and `ScreenProbeTraceFill` both set `PyramidLevels`, fed from
  `CompositorBuilder.TakeTracePyramid`. ⚠ **Nothing in *this* package sets it**: `TracedReflections`
  never mentions `Pyramid`, so the reference this package exists to be still walks the naive march.
  That is fine as long as it is deliberate — a reference that took the same shortcut as the kernel
  would stop being able to catch the shortcut — but it is a choice nobody has written down, and it is
  the reason the two agree at 1e-4 on a march that is not the one shipping.
- **The compositor node** is `ReflectionRenderer`, in `Vixen.Rendering.PostFx`. It reconstructs
  positions and normals from depth rather than taking planes, `PostEffectAssets` builds it from a
  `!Reflections` asset, `StandardFrame` emits one whenever the frame's reflections mode is `Screen`,
  and `Samples/13-ThirdPersonShooter` draws through it.

**Nothing in this package creates or calls a graphics device** — the kernel's driver lives in
`Vixen.Rendering.Lighting`, where the devices are.
