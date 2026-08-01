---
title: Lighting a scene in lux and lumens
slug: rendering/physical-lighting
kind: guide
area: Rendering
summary: Colour temperature, photometric units, an analytic daylight sky, and the one exposure that brings them back to a display.
api: [T:Vixen.Rendering.Photometry, T:Vixen.Rendering.LightUnit, T:Vixen.Rendering.Lighting.PhysicalSky, T:Vixen.Rendering.Lighting.SkyParameters, T:Vixen.Rendering.Lighting.EnvironmentTexture, T:Vixen.Rendering.PostFx.SkyRenderer, T:Vixen.Rendering.PostFx.SkyAsset, R:PostFx/Sky]
tags: [rendering, lighting, exposure, sky]
since: 0.1
status: stable
related: [rendering/shadows]
---

## What it is

Four things that only make sense together:

| Piece | Says |
|---|---|
| `LightUnit` beside a light's intensity | what the number means — lumens, candela, lux, nits |
| `Light.Temperature` | what colour the emitter is, in kelvin, as a tint that does not brighten |
| `PhysicalSky` | what the sky and the sun are, from the sun's direction alone |
| `!Tonemap`'s `ev100` | which luminance comes out as middle grey |

## What it is for

A scene where changing one thing changes one thing. Lit in arbitrary multipliers, every value is
coupled to every other: move the sun down and the lamps are suddenly wrong, add a lamp and the sun
is. In photometric units a 2700 K lantern emitting 900 lumens is that lantern under a noon sky and
under a sunset, and what changes between them is the *exposure* — one number for the whole frame.

You do not want it for a stylised project whose lighting is a palette rather than a simulation.
Nothing here stops you multiplying numbers together, and `LightUnit.Native` is what a light that
says nothing about its unit gets — so a scene authored before any of this existed loads unchanged.

## Using it

A light says what its number means and what colour its filament is:

```yaml
- !Light { kind: Directional, unit: Lux, colour: 1 0.44 0.14, intensity: 19000 }
- !Light { kind: Point, unit: Lumen, intensity: 4200, temperature: 1900, range: 11, radius: 0.14 }
```

⚠ The unit a kind converts *to* is fixed by the shader, not by the author. A directional light has no
falloff, so what reaches a surface is what it emits and the answer is lux; a punctual light's
contribution is divided by the square of the distance, so the answer has to be candela; an area light
is integrated over its own solid angle, so the answer is nits. `Photometry.Intensity` is the one
place those three conversions live.

⚠ A spot's cone is part of its brightness. Lumens are power, and a reflector puts that power through
the cone rather than over the whole sphere — so narrowing a 600-lumen lamp makes it brighter, exactly
as a real fitting does.

Then the exposure, once, at the end of the frame:

```yaml
- !Tonemap
  name: Tonemap
  source: SceneHdr
  output: SceneColour
  operator: 1
  ev100: 13.4
```

15 is bright sun, 12 is overcast, 10 is a lit room after dark, and each step of one is a stop.
`exposure` is still there and is still a bare multiplier; `ev100` wins where a document sets it,
because with a scene lit in lux and lumens the multiplier is not a number anybody can pick.

## Examples

**The sky and the sun are one fact.** A scene that names a sun direction *and* a sun brightness can
be a sunset sky over a noon sun, and nothing reports it. `PhysicalSky` takes the direction and
answers everything else — the cube the scene reflects, the illuminance the disc delivers, and the
colour it delivers it in, all from the same air mass:

```csharp no-compile="a fragment; the device and the sun's direction are the caller's"
var sky = new SkyParameters(sunDirection, Turbidity: 2.6f, GroundAlbedo: 0.15f);
var cube = EnvironmentTexture.Bake(device, PhysicalSky.Bake(48, sky), mipCount: 5);

light.Unit = LightUnit.Lux;
light.Intensity = PhysicalSky.SunIlluminance(sky);   // ~19 klx six degrees up, ~95 klx overhead
light.Colour = PhysicalSky.SunTint(sky);             // deep orange down there, neutral up here
```

Preetham's analytic daylight model, in cd/m². The sun's colour and brightness come from Rayleigh and
Mie transmittance along the same path the sky above is computed for — the blue is attenuated forty
times harder than the red over nine air masses, which *is* the sunset. ⚠ It is a daylight model and
stops being one below about a degree of elevation; `Radiance` clamps rather than extrapolating, so a
night sky is dark and not inverted.

**The background is the same cube.** A `!Sky` node fills an existing colour target with the
environment, sampled along the view ray, before the pass that draws the scene loads it:

```yaml
- !Sky
  name: Sky
  output: SceneHdr
  view: Camera

- !RenderPass
  name: Main
  colourTargets: [SceneHdr, SceneNormals]
  depthTarget: SceneDepth
  loaded:
    - SceneHdr
```

⚠ `loaded:` is per target and it has to be. `SceneHdr` keeps what the sky put there; `SceneNormals`
has no earlier producer, and loading a target no pass wrote is a read of whatever was in that memory
last frame — which the render graph refuses by name.

⚠ The cube itself is the host's, handed to `SkyRenderer.Environment` — it is baked before the frame
graph exists and outlives every frame, so there is no graph resource for a document to name. That
also means the host owes the transition; nothing in the frame will move it into `ShaderRead`.

## See also

- [Making everything cast a shadow](shadows.md) — the other half of a directional light.
- [Turning on dynamic global illumination](lit-path.md) — where the bounced light comes from.
