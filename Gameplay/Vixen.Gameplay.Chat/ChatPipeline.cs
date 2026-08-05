// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Chat;

/// <summary>Why a message did not go out.</summary>
public enum ChatRejection {
    /// <summary>It did.</summary>
    None,

    /// <summary>This build has no such channel.</summary>
    UnknownChannel,

    /// <summary>There was nothing in it.</summary>
    Empty,

    /// <summary>It was longer than the channel takes.</summary>
    TooLong,

    /// <summary>They have said too much too fast.</summary>
    RateLimited,

    /// <summary>The same thing again.</summary>
    Repeated,

    /// <summary>They are muted.</summary>
    Muted,

    /// <summary>One of them has blocked the other.</summary>
    Blocked,

    /// <summary>Their rank or their tags do not let them speak here.</summary>
    NoPermission,

    /// <summary>A requirement on the channel is not met.</summary>
    Requirements,

    /// <summary>A game's own filter said no.</summary>
    Filtered,

    /// <summary>Nobody was there to hear it.</summary>
    NoAudience
}

/// <summary>What a filter decided.</summary>
/// <param name="Rejection">Why it said no, or <see cref="ChatRejection.None" />.</param>
/// <param name="Message">What the sender is told, in a sentence.</param>
public readonly record struct ChatVerdict(ChatRejection Rejection, string Message = "") {
    /// <summary>The verdict of a filter with nothing to say.</summary>
    public static ChatVerdict Pass { get; } = new(ChatRejection.None);

    /// <summary>Whether the message may go on.</summary>
    public bool IsAllowed => Rejection == ChatRejection.None;
}

/// <summary>A message on its way through the pipeline, before anybody has heard it.</summary>
/// <remarks>
///     ⚠ <b>Mutable, because a word filter censors rather than rejects.</b> Rejecting a message for one
///     word tells the sender exactly which word the filter has, which is how a filter gets worked
///     around within a day; replacing it and sending it on does not. So a filter may rewrite
///     <see cref="Text" /> and still pass.
/// </remarks>
public sealed class ChatDraft {
    /// <summary>Starts one.</summary>
    /// <param name="sender">Who is speaking.</param>
    /// <param name="channel">Which channel.</param>
    /// <param name="text">What they said.</param>
    /// <param name="scene">Which map they said it on.</param>
    /// <param name="recipient">Who it is for, on a direct channel.</param>
    public ChatDraft(PlayerId sender, ChatChannel channel, string text, DefId scene = default, PlayerId recipient = default) {
        ArgumentNullException.ThrowIfNull(channel);

        Sender = sender;
        Channel = channel;
        Text = text ?? string.Empty;
        Scene = scene;
        Recipient = recipient;
    }

    /// <summary>Who is speaking.</summary>
    public PlayerId Sender { get; }

    /// <summary>Which channel.</summary>
    public ChatChannel Channel { get; }

    /// <summary>What they said, as the filters have left it.</summary>
    public string Text { get; set; }

    /// <summary>Which map they said it on.</summary>
    public DefId Scene { get; }

    /// <summary>Who it is for, on a direct channel.</summary>
    public PlayerId Recipient { get; }

    /// <summary>Whether any filter changed the words.</summary>
    public bool WasRewritten { get; internal set; }
}

/// <summary>What a filter is allowed to ask about the world.</summary>
/// <remarks>
///     Three questions and no more, for <see cref="IRequirementContext" />'s reason. Anything a filter
///     needs beyond these is a filter the game writes with its own collaborators in hand.
/// </remarks>
public interface IChatContext {
    /// <summary>The clock, in seconds.</summary>
    float Now { get; }

    /// <summary>Whether either of two players has blocked the other.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns>Whether they are severed.</returns>
    bool IsSevered(PlayerId left, PlayerId right);

    /// <summary>What a player's requirements are evaluated against.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Their context, or null.</returns>
    IRequirementContext? ContextOf(PlayerId player);
}

/// <summary>One rule a message has to pass.</summary>
/// <remarks>
///     Doc 28 names this seam: <em>"moderation is a pipeline of <c>IChatFilter</c>s… applied
///     server-side before fan-out, with the rejection reason returned to the sender"</em>. The shipped
///     filters are ordinary implementations of it, which is G-R1's discipline — the extension point is
///     the one the engine itself uses.
/// </remarks>
public interface IChatFilter {
    /// <summary>What it is called, in a report.</summary>
    string Name { get; }

    /// <summary>Judges a message, and may rewrite it.</summary>
    /// <param name="draft">The message.</param>
    /// <param name="context">What it may ask about.</param>
    /// <returns>The verdict.</returns>
    ChatVerdict Apply(ChatDraft draft, IChatContext context);
}

/// <summary>The filters a message goes through, in order.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ordered, and the order is a policy rather than an accident.</b> The cheap structural
///         checks come first because they reject most of what is rejected and cost nothing; a
///         game-supplied word filter comes last because it is the expensive one and there is no point
///         scanning a message that was too long anyway.
///     </para>
///     <para>
///         ⚠ <b>A rejection stops the pipeline.</b> Running the rest to collect every reason would mean
///         telling a rate-limited spammer which of their words are also on the list.
///     </para>
/// </remarks>
public sealed class ChatPipeline {
    readonly List<IChatFilter> filters = [];

    /// <summary>How many filters it holds.</summary>
    public int Count => filters.Count;

    /// <summary>Them, in the order they run.</summary>
    public IReadOnlyList<IChatFilter> Filters => filters;

    /// <summary>Which filter refused the last message, or null.</summary>
    public IChatFilter? LastRefusedBy { get; private set; }

    /// <summary>Adds a filter at the end.</summary>
    /// <param name="filter">The filter.</param>
    /// <returns>The pipeline, so calls chain.</returns>
    public ChatPipeline Add(IChatFilter filter) {
        ArgumentNullException.ThrowIfNull(filter);

        filters.Add(filter);

        return this;
    }

    /// <summary>Runs a message through.</summary>
    /// <param name="draft">The message, which filters may rewrite.</param>
    /// <param name="context">What they may ask about.</param>
    /// <returns>The first refusal, or <see cref="ChatVerdict.Pass" />.</returns>
    public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);

        LastRefusedBy = null;

        foreach (var filter in filters) {
            var before = draft.Text;
            var verdict = filter.Apply(draft, context);

            if (!string.Equals(before, draft.Text, StringComparison.Ordinal)) {
                draft.WasRewritten = true;
            }

            if (verdict.IsAllowed) {
                continue;
            }

            LastRefusedBy = filter;

            return verdict;
        }

        return ChatVerdict.Pass;
    }
}
