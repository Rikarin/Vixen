# 10 — Voice Chat

Two players talking to each other over real UDP sockets, with the whole audio path joined up:
capture → gate → Opus → `Channel.Sequenced` → jitter buffer → concealment → decode → mixer.

```bash
dotnet run --project Samples/10-VoiceChat -c Release
```

## What this proves that the tests cannot

`Vixen.Audio.Codecs`' tests drive a stand-in for a network, and they have to: losing, reordering and
delaying a packet on purpose is the entire point, and no real transport can be asked to do that on
cue. That leaves exactly one thing unchecked — whether the two halves actually **fit**. Whether what
`VoiceSender` hands out survives a real session, a real socket and a real relay, and comes out of a
mixer as sound.

So the check at the end is not "packets arrived". It is the peak sample that left each peer's mixer.

Both ends run in one process and the sockets are still real: loopback UDP is a genuine datagram path
with genuine framing and a genuine MTU. What it lacks is loss, which is the part already covered by
tests. One process means one `dotnet run`, no second terminal and no firewall prompt.

## The joining is about forty lines

`VoiceLink.cs` is all of it: six bytes of header — sequence, then timestamp — and a channel choice.
Neither `Vixen.Audio.Codecs` nor `Vixen.Net` references the other, and neither needs to.

**`Channel.Sequenced` and not `Reliable`.** Voice that arrives late is worse than voice that never
arrives: a retransmitted packet turns up after its moment has passed, the jitter buffer drops it
anyway, and everything behind it on that channel stalled while it was being retried. Sequenced may be
lost, is never retransmitted, and is never delivered out of order.

## The server decodes nothing

It stamps each packet with who sent it and forwards it. Decoding every talker and re-mixing one
stream per listener would cost a codec per player per player — and it would throw away the thing that
makes voice worth having in a 3D game. A client that receives each talker **separately** can place
them in the world, duck them individually, and put one of them underwater without touching the
others. That last one is what per-instance parameters are for.

One `VoiceReceiver` per talker, because Opus carries state between packets: two people through one
decoder would each be extrapolating from the other's voice whenever a packet went missing.

## The tone is standing in for a microphone

So the sample runs on a machine with no microphone and prints the same numbers every time. Swapping
it for a `CaptureSampleProvider` over a real `IAudioCaptureDevice` is the only change a game makes
here — everything downstream is already what a game would run.

## What it prints

```
Alice  sent  161 packets, suppressed   89 silent frames; heard 1 talker(s), 87424 frames, ... peak 0.325
Bob    sent  149 packets, suppressed  101 silent frames; heard 1 talker(s), 91264 frames, ... peak 0.307

server relayed 306 packets, 19,172 bytes
which is 31 kbit a second, for two people talking most of the time
```

**`suppressed` is the number that matters.** Those are frames the gate decided nobody was talking
through, which cost nothing at all — not a small packet, nothing. Two talkers who are quiet cost the
server zero bandwidth, which is the difference between a thirty-two player voice channel being
affordable and being a feature people turn off.

Licensed under Apache-2.0.
