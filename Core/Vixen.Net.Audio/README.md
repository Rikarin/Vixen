# Vixen.Net.Audio

Networked audio: **whether a sound is playing, and how loud** — not the sound.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md).

## What this is not for

**A one-shot at a world position is an event, not state.** An explosion, a footstep, a UI click
happens once and reaches whoever was there. Modelling that as replicated state means a player who
joins five minutes later hears the explosion — because replicated state is, by definition, what a
late joiner is caught up on. Those belong on a `BroadcastRouter` message or an RPC.

This is for sounds with a state worth agreeing on: an engine that is running, an alarm that is
sounding, a machine humming until somebody switches it off. If the question "is it still playing?"
has an answer a minute from now, it belongs here.

## No clip on the wire

Not a simplification. The entity carrying `NetworkAudioSource` was spawned from a prefab, and the
prefab carries its `AudioClipRef` — so both peers already agree about which sound this is, by the same
mechanism they agree about which mesh it has. Sending a clip id would re-state a fact the spawn
already established, and it would mean a second asset registry to keep in step with the first.

## `Trigger` is what makes "again" visible

Playing a one-shot twice sets `Playback` to the value it already had. A receiver comparing states sees
nothing and the second shot is silent — a bug that only appears with two players in the room. A
counter that moves is a change even when the state it accompanies has not, which is the same trick
`NetworkTransform.TeleportCount` uses for the same reason.

A game says "again" by adding a `NetworkAudioTrigger` tag. The capture system turns it into a bump and
takes it off, so nothing has to remember to clear it.

The receiving side restarts by setting `Stopped` now and `Playing` on the next pass, because
`AudioSystem` starts a voice only when one is not already alive — telling a playing sound to play is a
no-op. That is one frame, which at sixty is sixteen milliseconds and inaudible. Doing it in one would
mean this system holding an `AudioEngine`, which is the dependency the declarative design exists to
avoid.

## Owed

- **Voice chat**, which doc 16 asks for and which is a different problem entirely: a capture device,
  an encoder, a jitter buffer and a mixing policy. `IAudioCaptureDevice` exists on both backends, so
  the input half is there; nothing above it is.
- **Spatial parameters.** `AudioSpatial` — attenuation, cone, doppler — is not replicated. It is
  usually authored on the prefab and never changed, which is why it costs nothing today; a game that
  animates a cone would need it.
- **Forgetting on despawn.** `NetworkAudioApplySystem.Forget` is the door and nothing walks through it
  yet, so a long-running client keeps one byte per object it has ever heard.
