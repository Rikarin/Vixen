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

## Not yet, and named so the absence is a decision

- **The device half.** A Raven kernel reflecting per pixel through the same composed slots the
  kernels already share — `IDistanceFieldSource`, `ISurfaceCacheSource` — with this reference as
  its texel-by-texel referee, and the reflection-probe fallback handed through
  `IReflectionFallback`'s device analogue. That is also the piece that actually retires doc 06's
  caveat in a frame.
- **The blend band.** The hard threshold is the testable form; the device half owes a band that
  cross-fades trace and field around it, so a roughness map does not draw the threshold as a line.
- **Screen traces first.** The same trace order the screen probes run — screen, then field — and
  for the same reason: the screen holds geometry the field does not.

**Nothing here creates or calls a graphics device.**
