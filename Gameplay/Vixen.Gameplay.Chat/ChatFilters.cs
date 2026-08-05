// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Vixen.Gameplay.Chat;

/// <summary>Who is silenced, and until when.</summary>
/// <remarks>
///     A mute is a chat sanction, so chat owns it. Keeping it — a mute that survives a relog is the
///     only kind worth having — is the realm's, on the same terms as everything else durable in these
///     libraries.
/// </remarks>
public sealed class MuteList {
    readonly Dictionary<PlayerId, float> until = [];

    /// <summary>How many mutes are recorded, expired or not.</summary>
    public int Count => until.Count;

    /// <summary>Silences somebody.</summary>
    /// <param name="player">Who.</param>
    /// <param name="expires">When it lifts, on the caller's clock. Use infinity for permanent.</param>
    public void Mute(PlayerId player, float expires) {
        if (player.IsSome) {
            until[player] = expires;
        }
    }

    /// <summary>Lifts a mute.</summary>
    /// <param name="player">Who.</param>
    /// <returns>Whether there was one.</returns>
    public bool Unmute(PlayerId player) => until.Remove(player);

    /// <summary>Whether somebody is silenced right now.</summary>
    /// <param name="player">Who.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Whether they are.</returns>
    public bool IsMuted(PlayerId player, float now) => until.TryGetValue(player, out var expires) && now < expires;

    /// <summary>When a mute lifts.</summary>
    /// <param name="player">Who.</param>
    /// <returns>The time, or zero.</returns>
    public float Until(PlayerId player) => until.GetValueOrDefault(player);
}

/// <summary>How much somebody has said on each channel lately.</summary>
/// <remarks>
///     ⚠ <b>Not a duplicate of <c>RpcRouter</c>'s limiter, and doc 28's "reuse rather than invent" is
///     still honoured.</b> That one is per <em>connection</em> and cannot tell a whisper from guild
///     chat; this one is per <c>(player, channel)</c>, which is the cap a designer actually writes —
///     "three trade posts a minute, and say what you like locally". They bound different things, and a
///     realm hands the connection-wide number to <c>RpcRouter</c> as before.
/// </remarks>
public sealed class ChatRateLimiter {
    readonly Dictionary<(PlayerId Player, uint Channel), Window> windows = [];

    /// <summary>How many windows it is tracking.</summary>
    public int Count => windows.Count;

    /// <summary>Whether somebody may say one more thing, and records it if they may.</summary>
    /// <param name="player">Who.</param>
    /// <param name="channel">Which channel.</param>
    /// <param name="now">The clock.</param>
    /// <returns>Whether they may.</returns>
    /// <remarks>
    ///     ⚠ <b>A refused message does not count against the window.</b> Charging for the refusal is
    ///     what turns a rate limit into a lockout: a client that retries on rejection would push its
    ///     own window out for ever and never recover.
    /// </remarks>
    public bool Take(PlayerId player, ChatChannel channel, float now) {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.RateLimit <= 0) {
            return true;
        }

        var key = (player, channel.Id.Value);

        if (!windows.TryGetValue(key, out var window) || now - window.Start >= channel.RateWindow) {
            windows[key] = new(now, 1);

            return true;
        }

        if (window.Count >= channel.RateLimit) {
            return false;
        }

        windows[key] = window with { Count = window.Count + 1 };

        return true;
    }

    /// <summary>Forgets everything, as a realm does when a player disconnects.</summary>
    /// <param name="player">Who.</param>
    /// <returns>How many windows were dropped.</returns>
    public int Forget(PlayerId player) {
        var keys = windows.Keys.Where(key => key.Player == player).ToArray();

        foreach (var key in keys) {
            windows.Remove(key);
        }

        return keys.Length;
    }

    readonly record struct Window(float Start, int Count);
}

/// <summary>The filters the engine ships, all of them ordinary <see cref="IChatFilter" />s.</summary>
public static class ChatFilters {
    /// <summary>Refuses a message with nothing in it.</summary>
    public sealed class Empty : IChatFilter {
        /// <inheritdoc />
        public string Name => "empty";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);

            return string.IsNullOrWhiteSpace(draft.Text)
                ? new(ChatRejection.Empty, "There was nothing to say.")
                : ChatVerdict.Pass;
        }
    }

    /// <summary>Refuses a message longer than the channel takes.</summary>
    /// <remarks>
    ///     ⚠ <b>Refused rather than truncated.</b> A message cut in half says something its sender did
    ///     not, and on a trade channel that is a price.
    /// </remarks>
    public sealed class Length : IChatFilter {
        /// <inheritdoc />
        public string Name => "length";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);

            return draft.Text.Length > draft.Channel.MaximumLength
                ? new(
                    ChatRejection.TooLong,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{draft.Channel.DisplayName} takes {draft.Channel.MaximumLength} characters and that was {draft.Text.Length}."
                    )
                )
                : ChatVerdict.Pass;
        }
    }

    /// <summary>Refuses a message from somebody who has said too much too fast.</summary>
    public sealed class RateLimit : IChatFilter {
        readonly ChatRateLimiter limiter;

        /// <summary>Makes one over a limiter.</summary>
        /// <param name="limiter">Where the windows are kept.</param>
        public RateLimit(ChatRateLimiter limiter) {
            ArgumentNullException.ThrowIfNull(limiter);

            this.limiter = limiter;
        }

        /// <inheritdoc />
        public string Name => "rate";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(context);

            return limiter.Take(draft.Sender, draft.Channel, context.Now)
                ? ChatVerdict.Pass
                : new(ChatRejection.RateLimited, "That was too fast. Wait a moment.");
        }
    }

    /// <summary>Refuses a message from somebody who is silenced.</summary>
    public sealed class Muted : IChatFilter {
        readonly MuteList mutes;

        /// <summary>Makes one over a mute list.</summary>
        /// <param name="mutes">The list.</param>
        public Muted(MuteList mutes) {
            ArgumentNullException.ThrowIfNull(mutes);

            this.mutes = mutes;
        }

        /// <inheritdoc />
        public string Name => "mute";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(context);

            return mutes.IsMuted(draft.Sender, context.Now)
                ? new(ChatRejection.Muted, "You cannot speak right now.")
                : ChatVerdict.Pass;
        }
    }

    /// <summary>Refuses a direct message between two people one of whom has blocked the other.</summary>
    /// <remarks>
    ///     ⚠ <b>Only the direct channels, because a block is not a mute.</b> Blocking somebody must not
    ///     stop them talking in a zone everybody else can hear — that is a moderator's job, not a
    ///     player's — and the fan-out filter for the rest is
    ///     <see cref="ChatRouter" />'s, which drops the blocked recipient rather than the message.
    /// </remarks>
    public sealed class Blocked : IChatFilter {
        /// <inheritdoc />
        public string Name => "block";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(context);

            if (draft.Channel.Audience != ChatAudienceKind.Direct || !draft.Recipient.IsSome) {
                return ChatVerdict.Pass;
            }

            // ⚠ The same message either way round. Telling the sender "they have blocked you" is how a
            // block stops being invisible, so a severed pair simply cannot reach each other.
            return context.IsSevered(draft.Sender, draft.Recipient)
                ? new(ChatRejection.Blocked, "That player is not accepting messages.")
                : ChatVerdict.Pass;
        }
    }

    /// <summary>Refuses the same message twice inside a window.</summary>
    public sealed class Repeat : IChatFilter {
        readonly Dictionary<PlayerId, (string Text, float When)> last = [];

        /// <summary>Makes one.</summary>
        /// <param name="seconds">How long the same words are refused for.</param>
        public Repeat(float seconds = 30f) => Seconds = MathF.Max(0f, seconds);

        /// <summary>How long the same words are refused for.</summary>
        public float Seconds { get; }

        /// <inheritdoc />
        public string Name => "repeat";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(context);

            if (last.TryGetValue(draft.Sender, out var previous)
                && context.Now - previous.When < Seconds
                && string.Equals(previous.Text, draft.Text, StringComparison.OrdinalIgnoreCase)) {
                return new(ChatRejection.Repeated, "You just said that.");
            }

            last[draft.Sender] = (draft.Text, context.Now);

            return ChatVerdict.Pass;
        }
    }

    /// <summary>Replaces words a game does not want said, and lets the message through.</summary>
    /// <remarks>
    ///     ⚠ <b>It censors rather than rejects, and that is the whole reason a filter may rewrite.</b>
    ///     Refusing a message for one word tells the sender exactly which word is on the list, and a
    ///     list that can be probed a word at a time is a list that is worked around by lunchtime.
    /// </remarks>
    public sealed class Words : IChatFilter {
        readonly string[] words;

        /// <summary>Makes one over a game's list.</summary>
        /// <param name="words">The words. Matched case-insensitively, anywhere in the text.</param>
        /// <param name="replacement">What each is replaced with, character for character.</param>
        public Words(IEnumerable<string> words, char replacement = '*') {
            ArgumentNullException.ThrowIfNull(words);

            this.words = [.. words.Where(word => word.Length > 0)];
            Replacement = replacement;
        }

        /// <summary>What each word is replaced with.</summary>
        public char Replacement { get; }

        /// <inheritdoc />
        public string Name => "words";

        /// <inheritdoc />
        public ChatVerdict Apply(ChatDraft draft, IChatContext context) {
            ArgumentNullException.ThrowIfNull(draft);

            foreach (var word in words) {
                var at = draft.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase);

                while (at >= 0) {
                    draft.Text = string.Concat(
                        draft.Text.AsSpan(0, at),
                        new string(Replacement, word.Length),
                        draft.Text.AsSpan(at + word.Length)
                    );

                    at = draft.Text.IndexOf(word, at + word.Length, StringComparison.OrdinalIgnoreCase);
                }
            }

            return ChatVerdict.Pass;
        }
    }
}
