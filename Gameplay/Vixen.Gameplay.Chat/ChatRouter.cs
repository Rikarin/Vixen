// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Chat;

/// <summary>Who can hear a channel.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The seam that keeps chat from knowing what a party is.</b> Doc 28's spine forbids
///         <c>Chat → Social</c>, and it is right to: a game with no parties still has chat, and a game
///         whose "party" is a squad of fifty still has one audience resolver. So the router asks a
///         <see cref="ChatAudienceKind" /> and something else answers it — <c>InterestGrid</c> for a
///         scene, a party grain for a group, <c>IGuildGrain</c> for a guild.
///     </para>
///     <para>
///         It is also where doc 27's routing table becomes real: the same resolver answers a party
///         audience whether its members are on this realm or spread across three, and
///         <see cref="ChatChannel.Route" /> is what decides which pipe carries the result.
///     </para>
/// </remarks>
public interface IChatAudience {
    /// <summary>Who hears this message.</summary>
    /// <param name="message">The message, after the filters.</param>
    /// <param name="into">Where to put them. The sender may be included; the router does not mind.</param>
    /// <returns>How many were added.</returns>
    int Resolve(in ChatMessage message, ICollection<PlayerId> into);
}

/// <summary>A message that has passed the filters and is on its way.</summary>
/// <param name="Sender">Who said it.</param>
/// <param name="Channel">Which channel.</param>
/// <param name="Text">What it says now, which is not always what was typed.</param>
/// <param name="Scene">Which map it was said on.</param>
/// <param name="Recipient">Who it is for, on a direct channel.</param>
/// <param name="Sequence">Which message of the router's this is — what a client orders by.</param>
public readonly record struct ChatMessage(
    PlayerId Sender,
    ChatChannel Channel,
    string Text,
    DefId Scene,
    PlayerId Recipient,
    ulong Sequence
);

/// <summary>What came of trying to say something.</summary>
/// <param name="Rejection">Why it did not go out, or <see cref="ChatRejection.None" />.</param>
/// <param name="Reason">What the sender is told.</param>
/// <param name="Message">The message, when it went out.</param>
/// <param name="Audience">Who heard it, minus everybody who has the sender blocked.</param>
/// <param name="Route">Which pipe carries it.</param>
public readonly record struct ChatDelivery(
    ChatRejection Rejection,
    string Reason,
    ChatMessage Message,
    IReadOnlyList<PlayerId> Audience,
    ChatRoute Route
) {
    /// <summary>Whether it went out.</summary>
    public bool IsDelivered => Rejection == ChatRejection.None;
}

/// <summary>What a player says, checked once and fanned out once.</summary>
/// <remarks>
///     <para>
///         <b>Server-side, before fan-out, with the reason returned to the sender</b> — doc 28's
///         sentence, and the ordering in it is the design. A client that filtered its own outgoing
///         messages is a client; a rejection the sender is not told about is a player who thinks the
///         server has hung.
///     </para>
///     <para>
///         ⚠ <b>A block drops the <em>recipient</em> on a fan-out channel and the <em>message</em> on a
///         direct one.</b> Blocking somebody cannot silence them in a zone everybody else can hear —
///         that is a moderator's job — so what a block does to zone chat is stop you seeing it, which
///         is a per-recipient decision made here rather than a rejection made to the sender.
///     </para>
/// </remarks>
public sealed class ChatRouter {
    readonly List<PlayerId> audience = [];

    /// <summary>Makes a router.</summary>
    /// <param name="library">Where the channels come from.</param>
    /// <param name="pipeline">What every message goes through.</param>
    /// <param name="audience">Who can hear each channel.</param>
    public ChatRouter(ChatLibrary library, ChatPipeline pipeline, IChatAudience audience) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(audience);

        Library = library;
        Pipeline = pipeline;
        Audience = audience;
    }

    /// <summary>Where the channels come from.</summary>
    public ChatLibrary Library { get; }

    /// <summary>What every message goes through.</summary>
    public ChatPipeline Pipeline { get; }

    /// <summary>Who can hear each channel.</summary>
    public IChatAudience Audience { get; }

    /// <summary>How many messages have gone out.</summary>
    public ulong Delivered { get; private set; }

    /// <summary>Raised for every message that goes out.</summary>
    public event Action<ChatDelivery>? Sent;

    /// <summary>Says something.</summary>
    /// <param name="sender">Who is speaking.</param>
    /// <param name="channel">Which channel.</param>
    /// <param name="text">What they typed.</param>
    /// <param name="context">What the filters may ask about.</param>
    /// <param name="scene">Which map, for a spatial channel.</param>
    /// <param name="recipient">Who it is for, on a direct channel.</param>
    /// <returns>What came of it.</returns>
    public ChatDelivery Say(
        PlayerId sender,
        DefId channel,
        string text,
        IChatContext context,
        DefId scene = default,
        PlayerId recipient = default
    ) {
        ArgumentNullException.ThrowIfNull(context);

        if (Library.Find(channel) is not { } found) {
            return Refused(ChatRejection.UnknownChannel, "There is no such channel.");
        }

        var draft = new ChatDraft(sender, found, text, scene, recipient);

        // The channel's own gates before the pipeline, because "you may not speak here at all" is a
        // different answer from "not so fast" and the player should get the durable one.
        if (found.Permission.IsSome && context.ContextOf(sender) is var speaker && speaker?.HasTag(found.Permission) != true) {
            return Refused(ChatRejection.NoPermission, $"You cannot speak on {found.DisplayName}.");
        }

        if (context.ContextOf(sender) is { } subject && !found.Requirements.IsMetBy(subject)) {
            return Refused(ChatRejection.Requirements, $"You do not meet what {found.DisplayName} asks for.");
        }

        var verdict = Pipeline.Apply(draft, context);

        if (!verdict.IsAllowed) {
            return Refused(verdict.Rejection, verdict.Message);
        }

        var message = new ChatMessage(sender, found, draft.Text, scene, recipient, Delivered + 1);

        audience.Clear();
        Audience.Resolve(message, audience);

        // Everybody who has the sender blocked simply does not get it, which is what a block means on
        // a channel the sender is entitled to use.
        audience.RemoveAll(listener => listener != sender && context.IsSevered(sender, listener));

        if (audience.Count == 0) {
            return Refused(ChatRejection.NoAudience, "Nobody heard that.");
        }

        Delivered++;

        var delivery = new ChatDelivery(ChatRejection.None, string.Empty, message, [.. audience], found.Route);

        Sent?.Invoke(delivery);

        return delivery;
    }

    static ChatDelivery Refused(ChatRejection rejection, string reason) =>
        new(rejection, reason, default, [], ChatRoute.Realm);
}
