# Vixen.Audio.Physics

Answers `Vixen.Audio`'s occlusion question with a Jolt raycast.

## Why this is a separate assembly

Occlusion is a raycast, and the only thing in this engine that casts rays is `Vixen.Physics`, which
binds Jolt — a native library, with a binary per RID. If `Vixen.Audio` referenced it directly, a game
with sound and no physics would ship and load Jolt to play a footstep, and the browser target would
ship it in order not to be able to call it.

So `Vixen.Audio` declares `IAudioOcclusionProvider` and knows nothing else. This assembly implements
it for games that have physics. A game with its own idea of what blocks sound — a grid, a portal
graph, a designer-authored volume set — implements the same interface and never references this.

## Using it

```csharp
var occluders = new PhysicsLayer(1);   // whatever the level marks as blocking sound

engine.Occlusion.Provider = new PhysicsOcclusionProvider(world) {
    Layers = PhysicsLayerMask.All.Without(dynamicBodies)
};
```

Then draw a curve against `AudioBuiltinParameter.Occlusion` in a parameter sheet. Nothing else is
plumbed: the mixer asks, this answers, and an asset decides what the answer sounds like.

## The two settings that matter

**`Layers` is the whole feature.** Cast against everything solid and a chain-link fence, a handrail
and a lamp post all muffle a conversation happening through a doorway. Occlusion belongs on the
geometry a level designer decided blocks sound. Left at `All` this works immediately and sounds
wrong in a way that is hard to attribute later.

It is also the answer to a sound occluding *itself*: an engine emitter sits inside its vehicle's
collider, so a ray cast at it hits the vehicle. Nothing here can fix that alone — the provider is
handed two points and knows nothing about which body either belongs to. Keep dynamic bodies off the
occluding layer, which is what a level wants anyway. `AnEmitterInsideItsOwnColliderNeedsALayerAndNotMagic`
pins both halves of that so nobody later files it as a raycast bug.

**`Rays` is why occlusion is not a switch.** A single centre-to-centre cast makes the answer binary,
and binary answers flicker: a source a few centimetres to one side of a door frame alternates between
blocked and clear as either end moves. Five rays on a fixed cross give partial values — a doorway
reads about a fifth to a half — which is what the mixer's smoothing then has something useful to
smooth. The pattern is fixed rather than random on purpose: random offsets differ between two frames
that ought to agree, and between two machines.

## What it costs

`Rays` × `AudioOcclusion.Budget` casts per frame — five by eight is forty, which is nothing against a
physics budget. The mixer rations how many voices are asked about and spreads them round-robin, so
the cost does not grow with the number of sounds playing.

Licensed under Apache-2.0.
