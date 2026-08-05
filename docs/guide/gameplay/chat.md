---
title: Chat channels, routing and moderation
slug: gameplay/chat
kind: guide
area: Gameplay
summary: Channels authored as definitions and routed by audience, a filter pipeline applied before fan-out with the reason returned to the sender, and an audience seam so chat never learns what a party is.
api: [T:Vixen.Gameplay.Chat.ChatRoute, T:Vixen.Gameplay.Chat.ChatAudienceKind, T:Vixen.Gameplay.Chat.ChatChannelDefinition, T:Vixen.Gameplay.Chat.ChatChannel, T:Vixen.Gameplay.Chat.ChatLibrary, T:Vixen.Gameplay.Chat.ChatRejection, T:Vixen.Gameplay.Chat.ChatVerdict, T:Vixen.Gameplay.Chat.ChatDraft, T:Vixen.Gameplay.Chat.IChatContext, T:Vixen.Gameplay.Chat.IChatFilter, T:Vixen.Gameplay.Chat.ChatPipeline, T:Vixen.Gameplay.Chat.ChatFilters, T:Vixen.Gameplay.Chat.ChatFilters.Empty, T:Vixen.Gameplay.Chat.ChatFilters.Length, T:Vixen.Gameplay.Chat.ChatFilters.Repeat, T:Vixen.Gameplay.Chat.ChatFilters.Words, T:Vixen.Gameplay.Chat.ChatFilters.Muted, T:Vixen.Gameplay.Chat.ChatFilters.Blocked, T:Vixen.Gameplay.Chat.ChatFilters.RateLimit, T:Vixen.Gameplay.Chat.MuteList, T:Vixen.Gameplay.Chat.ChatRateLimiter, T:Vixen.Gameplay.Chat.IChatAudience, T:Vixen.Gameplay.Chat.ChatMessage, T:Vixen.Gameplay.Chat.ChatDelivery, T:Vixen.Gameplay.Chat.ChatRouter, T:Vixen.Gameplay.Chat.ChatModule]
tags: [gameplay, chat, channels, moderation, rate-limit, mmo]
since: 0.1
status: preview
related: [gameplay/social, gameplay/tags, gameplay/requirements]
---

## What it is

A **`ChatChannel`** is authored: a route, an audience kind, a radius, a length cap, a rate limit, a
permission tag and a requirement list. A **`ChatPipeline`** is the moderation filters every message
goes through. A **`ChatRouter`** checks a message once and fans it out once.

## What it is for

Say, yell, emote, zone, party, squad, guild, whisper, global and trade — one type with different
numbers, so a game adds a channel by writing a `.vxdef`.

## Using it

Compile a `ChatLibrary`, build a pipeline, supply an `IChatAudience`, and call `Say`. What comes back
is either a `ChatDelivery` with its audience and its route, or a rejection with a sentence for the
sender.

⚠ **It cannot resolve an audience and does not try.** Doc 28's spine forbids `Chat → Social`, and it
is right to: a game with no parties still has chat. The channel names a `ChatAudienceKind` and the
game answers it — `InterestGrid` for a scene, a party grain for a group, `IGuildGrain` for a guild.

⚠ **A block drops the recipient on a fan-out channel and the message on a whisper.** Blocking somebody
cannot silence them in a zone everybody else can hear; what it does is stop *you* seeing it.

⚠ **A word filter censors and passes.** Rejecting for one word tells the sender exactly which word is
on the list — which is why `ChatDraft` is mutable.

⚠ **Too long is refused, not truncated.** A message cut in half says something its sender did not.

⚠ **A refused message does not count against the rate window**, or a client that retries locks itself
out for ever.

⚠ **`ChatRateLimiter` is not a second copy of `RpcRouter`'s.** That one is per connection and cannot
tell a whisper from guild chat; this one is per `(player, channel)`. They bound different things.

## Examples

Two channels:

```yaml
# Assets/Chat/say.vxdef
!ChatChannelDefinition
displayName: Say
command: /s
route: Realm
audience: Scene
radius: 30
maximumLength: 256

# Assets/Chat/trade.vxdef
!ChatChannelDefinition
displayName: Trade
command: /t
route: Gate
audience: Global
rateLimit: 3
rateWindow: 60
requires: [ { kind: Value, subject: Level, comparison: AtLeast, value: 10 } ]
```

Saying something:

```csharp compile
using Vixen.Gameplay;
using Vixen.Gameplay.Chat;

static class Speaking {
    public static string Say(ChatRouter router, PlayerId who, DefId channel, string text, IChatContext world) {
        var delivery = router.Say(who, channel, text, world);

        // The reason goes back to the sender — a rejection nobody is told about is a client that
        // looks hung.
        return delivery.IsDelivered ? $"heard by {delivery.Audience.Count}" : delivery.Reason;
    }
}
```

The pipeline, in the order it runs:

```csharp compile
using Vixen.Gameplay.Chat;

static class Moderation {
    public static ChatPipeline Build(ChatRateLimiter limiter, MuteList mutes, string[] words) =>
        // Cheap structural checks first; the game's word list last, because there is no point
        // scanning a message that was too long anyway.
        new ChatPipeline()
            .Add(new ChatFilters.Empty())
            .Add(new ChatFilters.Length())
            .Add(new ChatFilters.Muted(mutes))
            .Add(new ChatFilters.RateLimit(limiter))
            .Add(new ChatFilters.Repeat())
            .Add(new ChatFilters.Blocked())
            .Add(new ChatFilters.Words(words));
}
```

A game's own filter, through the seam the engine's own use:

```csharp compile
using Vixen.Gameplay.Chat;

public sealed class NoLinksFilter : IChatFilter {
    public string Name => "links";

    public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
        ArgumentNullException.ThrowIfNull(draft);

        return draft.Text.Contains("http", StringComparison.OrdinalIgnoreCase)
            ? new ChatVerdict(ChatRejection.Filtered, "Links are not allowed here.")
            : ChatVerdict.Pass;
    }
}
```

## See also

- [Social](gameplay/social) — what answers a group or guild audience, from the other side of the seam.
- [Requirements](gameplay/requirements) — what gates a channel.
- [Gameplay tags](gameplay/tags) — what a channel permission is.
