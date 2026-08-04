# Vixen.Gameplay.Chat

Channels authored as definitions and routed by audience, moderation as a pipeline applied before
fan-out, and an audience seam so that chat never learns what a party is.

Spec: [docs/plan/28](../../docs/plan/28-gameplay-framework.md) § Chat and
[27](../../docs/plan/27-mmo-framework.md) § Chat, the second half of **G4**.

## State

**Built: channel definitions with routes and audiences, the filter pipeline with seven shipped
filters, per-channel rate limiting, mute lists, and the router with its two-sided block rule.
27 tests, and G4 with them.**

| | |
|---|---|
| `ChatRoute` · `ChatAudienceKind` · `ChatChannelDefinition` · `ChatChannel` · `ChatLibrary` | Doc 27's routing table, authored. |
| `ChatDraft` · `ChatVerdict` · `ChatRejection` · `IChatContext` · `IChatFilter` · `ChatPipeline` | Moderation, ordered. |
| `ChatFilters` — `Empty` · `Length` · `RateLimit` · `Muted` · `Blocked` · `Repeat` · `Words` | The shipped seven, all ordinary `IChatFilter`s. |
| `MuteList` · `ChatRateLimiter` | The two things a filter needs kept between messages. |
| `IChatAudience` · `ChatMessage` · `ChatDelivery` · `ChatRouter` | Checked once, fanned out once. |
| `ChatModule` | One definition type and no tags of its own. |

## It does not know what a party is, and that is the design

Doc 28's spine forbids `Chat → Social`. That is right rather than merely a rule: a game with no
parties still has chat, and a game whose "party" is a squad of fifty still has one audience resolver.

So a channel declares a **`ChatAudienceKind`** — scene, group, guild, direct, global — and an
**`IChatAudience`** the game supplies answers it. `InterestGrid` resolves a scene, a party grain a
group, `IGuildGrain` a guild. The router never asks who is in what.

`PlayerId` is in the kernel for the same reason: it is the one thing chat and social both have to
name, and there is no edge between them to put it on.

## The five things worth knowing before reading the code

### A block drops the recipient on fan-out and the message on a whisper

Blocking somebody cannot silence them in a zone everybody else can hear — that is a moderator's job.
So what a block does to zone chat is stop *you* seeing it, which is a per-recipient decision the router
makes; on a direct channel there is only one recipient, so it is a rejection.

⚠ **The whisper refusal says the same thing either way round.** "That player is not accepting
messages" both ways; a message that said "they have blocked you" is a block that is no longer
invisible.

### A word filter censors and lets the message through

Rejecting a message for one word tells the sender exactly which word is on the list, and a list that
can be probed a word at a time is worked around by lunchtime. That is the whole reason `ChatDraft` is
mutable and a filter may rewrite.

### A message too long is refused, not truncated

A message cut in half says something its sender did not, and on a trade channel that is a price.

### A refused message does not count against the rate window

Charging for the refusal turns a rate limit into a lockout: a client that retries on rejection pushes
its own window out for ever and never recovers.

⚠ **`ChatRateLimiter` is not a second copy of `RpcRouter`'s limiter**, and doc 28's "reuse rather than
invent" still holds. That one is per *connection* and cannot tell a whisper from guild chat; this one
is per `(player, channel)`, which is the cap a designer actually writes — "three trade posts a minute,
say what you like locally". They bound different things, and the connection-wide number still goes to
`RpcRouter`.

### The pipeline is ordered and a rejection stops it

Cheap structural checks first, because they reject most of what is rejected and cost nothing; a
game-supplied word filter last, because scanning a message that was too long anyway is wasted work.
Running the rest to collect every reason would tell a rate-limited spammer which of their words are
also on the list.

⚠ **The channel's own gates run before the pipeline**, because "you may not speak here at all" is a
durable answer and "not so fast" is not, and the player should get the durable one.

⚠ **A sender with no requirement context cannot use a permissioned channel.** The safe reading: a
caller that has not wired up who somebody is gets a refusal rather than a channel that lets everybody
in.

## What is owed

- **Delivery.** `ChatRouter` produces a `ChatDelivery` and does not send it. What carries it is
  `Channel.ReliableUnordered` on the realm or the gate's WSS, per `ChatRoute` — doc 27's, and the
  wiring belongs where both are present.
- **Durable mutes.** A mute that does not survive a relog is not a mute. `MuteList` is in memory, on
  the same terms as everything else durable in these libraries.
- **Cross-realm party chat.** `ChatRoute.RealmOrGate` is authored and the router reports it; deciding
  *which* per message needs to know whether the audience is co-located, which is the audience
  resolver's answer and is a game's.
- **A translation or language filter**, which is an `IChatFilter` somebody writes and not the engine's.
