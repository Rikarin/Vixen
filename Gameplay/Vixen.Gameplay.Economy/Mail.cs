// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Economy;

/// <summary>One letter's identity.</summary>
/// <param name="Value">The number.</param>
public readonly record struct MailId(Guid Value) {
    /// <summary>No letter.</summary>
    public static MailId None => default;

    /// <summary>Whether it names one.</summary>
    public bool IsSome => Value != Guid.Empty;

    /// <summary>Mints a fresh one.</summary>
    /// <returns>The id.</returns>
    public static MailId New() => new(Guid.NewGuid());

    /// <inheritdoc />
    public override string ToString() => IsSome ? Value.ToString("N")[..8] : "no mail";
}

/// <summary>Why a mail operation was refused.</summary>
public enum MailRefusal {
    /// <summary>It was not.</summary>
    None,

    /// <summary>There is no such letter, or it is not theirs.</summary>
    NotFound,

    /// <summary>It has already been claimed.</summary>
    AlreadyClaimed,

    /// <summary>It names nobody, or nothing, or itself.</summary>
    Malformed,

    /// <summary>The mailbox is full.</summary>
    Full,

    /// <summary>They cannot pay what is owed on it.</summary>
    Insufficient,

    /// <summary>The ledger refused it.</summary>
    Refused
}

/// <summary>One thing attached to a letter. Currency and goods are the same shape.</summary>
/// <param name="Asset">What.</param>
/// <param name="Amount">How much.</param>
public readonly record struct MailAttachment(DefId Asset, long Amount);

/// <summary>A letter, with whatever is attached to it and whatever is owed on it.</summary>
public sealed class MailMessage {
    readonly MailAttachment[] attachments;

    internal MailMessage(
        MailId id,
        PlayerId from,
        PlayerId to,
        string subject,
        string body,
        MailAttachment[] attachments,
        DefId codCurrency,
        long cod,
        float sentAt,
        float expires
    ) {
        Id = id;
        From = from;
        To = to;
        Subject = subject;
        Body = body;
        this.attachments = attachments;
        CodCurrency = codCurrency;
        Cod = cod;
        SentAt = sentAt;
        Expires = expires;
    }

    /// <summary>Its id.</summary>
    public MailId Id { get; }

    /// <summary>Who sent it, or <see cref="PlayerId.None" /> for the world.</summary>
    public PlayerId From { get; }

    /// <summary>Who it is for.</summary>
    public PlayerId To { get; }

    /// <summary>What it says on the outside.</summary>
    public string Subject { get; }

    /// <summary>What it says inside.</summary>
    public string Body { get; }

    /// <summary>What is attached, in asset order.</summary>
    public ReadOnlySpan<MailAttachment> Attachments => attachments;

    /// <summary>What the recipient must pay to take it, or zero.</summary>
    public long Cod { get; }

    /// <summary>What they must pay it in.</summary>
    public DefId CodCurrency { get; }

    /// <summary>When it was sent, on the caller's clock.</summary>
    public float SentAt { get; }

    /// <summary>When it goes back, on the caller's clock. Infinity for a letter that never does.</summary>
    public float Expires { get; }

    /// <summary>Whether the attachments have been taken.</summary>
    public bool IsClaimed { get; internal set; }

    /// <summary>Whether it has been read.</summary>
    public bool IsRead { get; set; }

    /// <summary>Whether there is anything on it to take.</summary>
    public bool HasAttachments => attachments.Length > 0;
}

/// <summary>Everybody's mailboxes, and the only thing that moves an attachment.</summary>
/// <remarks>
///     <para>
///         <b>Doc 28 builds the auction on this and says so:</b> mail is <em>"the delivery mechanism
///         for auction settlement, so it must exist before the auction does"</em>. Currency and goods
///         are the same shape here, which is what lets an auction pay a seller who is offline.
///     </para>
///     <para>
///         ⚠ <b>An attachment leaves the sender when the letter is sent, not when it is claimed.</b>
///         The obvious arrangement — record what was attached and move it on claim — lets a sender
///         attach a sword, post the letter, sell the sword, and have the recipient claim a second one.
///         So sending escrows into <see cref="EconomyAccount.Mail" /> and every later step moves it out
///         of there. This is the same rule the auction's listing follows, for the same reason.
///     </para>
///     <para>
///         ⚠ <b>Claiming is all-or-nothing and includes the cash on delivery.</b> Taking the goods in
///         one operation and paying in another is a scam with two halves; one intent has neither.
///     </para>
///     <para>
///         ⚠ <b>An expired letter goes back to the sender rather than being destroyed</b> — the same
///         "never silently drop" rule <c>Container.Add</c> and <see cref="Currency.Fit" /> follow, and
///         the one that stops a full mailbox eating a raid drop.
///     </para>
/// </remarks>
public sealed class PostOffice {
    readonly Dictionary<PlayerId, List<MailMessage>> boxes = [];
    readonly Dictionary<MailId, MailMessage> byId = [];
    readonly IEconomyLedger ledger;

    /// <summary>Makes one over a ledger.</summary>
    /// <param name="ledger">Where movements are recorded.</param>
    /// <param name="capacity">How many letters one mailbox holds.</param>
    public PostOffice(IEconomyLedger ledger, int capacity = 100) {
        ArgumentNullException.ThrowIfNull(ledger);

        this.ledger = ledger;
        Capacity = Math.Max(1, capacity);
    }

    /// <summary>How many letters one mailbox holds.</summary>
    public int Capacity { get; }

    /// <summary>How many letters are in the system.</summary>
    public int Count => byId.Count;

    /// <summary>What is in somebody's mailbox, newest first.</summary>
    /// <param name="player">Whose.</param>
    /// <returns>Their letters.</returns>
    public IReadOnlyList<MailMessage> Of(PlayerId player) => boxes.TryGetValue(player, out var box) ? box : [];

    /// <summary>One letter.</summary>
    /// <param name="id">Which.</param>
    /// <returns>It, or null.</returns>
    public MailMessage? Find(MailId id) => byId.GetValueOrDefault(id);

    /// <summary>Sends a letter, escrowing whatever is attached to it.</summary>
    /// <param name="from">Who from, or <see cref="PlayerId.None" /> for the world.</param>
    /// <param name="to">Who to.</param>
    /// <param name="subject">What it says on the outside.</param>
    /// <param name="attachments">What is attached.</param>
    /// <param name="now">The clock.</param>
    /// <param name="operation">What makes this send distinct from the same one retried.</param>
    /// <param name="mail">The letter, when it went.</param>
    /// <param name="body">What it says inside.</param>
    /// <param name="cod">What the recipient must pay, or zero.</param>
    /// <param name="codCurrency">What they must pay it in.</param>
    /// <param name="days">How long before it goes back. Zero for never.</param>
    /// <returns>The refusal, or <see cref="MailRefusal.None" />.</returns>
    public MailRefusal Send(
        PlayerId from,
        PlayerId to,
        string subject,
        IReadOnlyList<MailAttachment> attachments,
        float now,
        string operation,
        out MailMessage? mail,
        string body = "",
        long cod = 0,
        DefId codCurrency = default,
        float days = 30f
    ) {
        ArgumentNullException.ThrowIfNull(attachments);

        mail = null;

        if (!to.IsSome || to == from) {
            return MailRefusal.Malformed;
        }

        if (cod > 0 && (!codCurrency.IsSome || attachments.Count == 0)) {
            return MailRefusal.Malformed;
        }

        if (Of(to).Count >= Capacity) {
            return MailRefusal.Full;
        }

        if (attachments.Count > 0) {
            var escrow = EconomyAccount.Of(EconomyAccount.Mail);
            var sender = from.IsSome ? EconomyAccount.Of(from) : EconomyAccount.Of(EconomyAccount.Vendor);
            var movements = new List<AssetMove>(attachments.Count * 2);

            foreach (var attachment in attachments) {
                if (attachment.Amount <= 0 || !attachment.Asset.IsSome) {
                    return MailRefusal.Malformed;
                }

                movements.Add(new(sender, attachment.Asset, -attachment.Amount));
                movements.Add(new(escrow, attachment.Asset, attachment.Amount));
            }

            var result = ledger.Post(new($"mail/{operation}/send", movements, $"{from} posts to {to}"));

            if (!result.Ok) {
                return result.Verdict == EconomyVerdict.Insufficient ? MailRefusal.Insufficient : MailRefusal.Refused;
            }
        }

        mail = Post(from, to, subject, body, [.. attachments], codCurrency, Math.Max(0, cod), now, days);

        return MailRefusal.None;
    }

    /// <summary>Puts a letter in a box for goods that are already in the mail account.</summary>
    /// <param name="to">Who it is for.</param>
    /// <param name="subject">What it says on the outside.</param>
    /// <param name="attachments">What is attached, already escrowed.</param>
    /// <param name="now">The clock.</param>
    /// <param name="days">How long before it goes back.</param>
    /// <returns>The letter.</returns>
    /// <remarks>
    ///     ⚠ <b>For a caller that has already moved the goods into <see cref="EconomyAccount.Mail" />
    ///     as part of its own intent</b> — an auction settlement, which pays the seller and the buyer
    ///     out of one posting. Routing those through <see cref="Send" /> would move them a second time,
    ///     out of an account that no longer holds them.
    /// </remarks>
    public MailMessage Deliver(PlayerId to, string subject, IReadOnlyList<MailAttachment> attachments, float now, float days = 30f) {
        ArgumentNullException.ThrowIfNull(attachments);

        return Post(PlayerId.None, to, subject, string.Empty, [.. attachments], default, 0, now, days);
    }

    /// <summary>Puts a letter in a box without moving anything.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Send" />, because a return is a letter whose attachments are
    ///     already escrowed.</b> Routing a return through <c>Send</c> would move them a second time,
    ///     out of an account that does not hold them — and the first version of this did exactly that
    ///     and then posted a corrective intent to undo it, which is two writes where the whole library
    ///     is built on there being one.
    /// </remarks>
    MailMessage Post(
        PlayerId from,
        PlayerId to,
        string? subject,
        string? body,
        MailAttachment[] attachments,
        DefId codCurrency,
        long cod,
        float now,
        float days
    ) {
        var mail = new MailMessage(
            MailId.New(),
            from,
            to,
            subject ?? string.Empty,
            body ?? string.Empty,
            attachments,
            codCurrency,
            cod,
            now,
            days > 0f ? now + (days * 86400f) : float.PositiveInfinity
        );

        Box(to).Insert(0, mail);
        byId.Add(mail.Id, mail);

        return mail;
    }

    /// <summary>Takes everything off a letter, paying whatever is owed on it.</summary>
    /// <param name="player">Who.</param>
    /// <param name="id">Which letter.</param>
    /// <param name="operation">What makes this claim distinct from the same one retried.</param>
    /// <returns>The refusal, or <see cref="MailRefusal.None" />.</returns>
    public MailRefusal Claim(PlayerId player, MailId id, string operation) {
        if (Find(id) is not { } mail || mail.To != player) {
            return MailRefusal.NotFound;
        }

        if (mail.IsClaimed) {
            return MailRefusal.AlreadyClaimed;
        }

        if (!mail.HasAttachments) {
            mail.IsClaimed = true;

            return MailRefusal.None;
        }

        var escrow = EconomyAccount.Of(EconomyAccount.Mail);
        var recipient = EconomyAccount.Of(player);
        var movements = new List<AssetMove>((mail.Attachments.Length * 2) + 2);

        foreach (ref readonly var attachment in mail.Attachments) {
            movements.Add(new(escrow, attachment.Asset, -attachment.Amount));
            movements.Add(new(recipient, attachment.Asset, attachment.Amount));
        }

        if (mail.Cod > 0) {
            // In the same intent as the goods, so there is no moment at which one has happened.
            var sender = mail.From.IsSome ? EconomyAccount.Of(mail.From) : EconomyAccount.Of(EconomyAccount.Vendor);

            movements.Add(new(recipient, mail.CodCurrency, -mail.Cod));
            movements.Add(new(sender, mail.CodCurrency, mail.Cod));
        }

        var result = ledger.Post(new($"mail/{operation}/claim", movements, $"{player} claims {id}"));

        if (!result.Ok) {
            return result.Verdict == EconomyVerdict.Insufficient ? MailRefusal.Insufficient : MailRefusal.Refused;
        }

        mail.IsClaimed = true;
        mail.IsRead = true;

        return MailRefusal.None;
    }

    /// <summary>Throws a letter away. Refused while anything is still attached to it.</summary>
    /// <param name="player">Who.</param>
    /// <param name="id">Which letter.</param>
    /// <returns>The refusal, or <see cref="MailRefusal.None" />.</returns>
    /// <remarks>
    ///     ⚠ <b>An unclaimed attachment is not deletable</b>, because "delete" is a button somebody
    ///     presses to clear a full mailbox and a raid drop should not be behind it.
    /// </remarks>
    public MailRefusal Delete(PlayerId player, MailId id) {
        if (Find(id) is not { } mail || mail.To != player) {
            return MailRefusal.NotFound;
        }

        if (mail.HasAttachments && !mail.IsClaimed) {
            return MailRefusal.AlreadyClaimed;
        }

        Box(player).Remove(mail);
        byId.Remove(id);

        return MailRefusal.None;
    }

    /// <summary>Sends back whatever has been sitting too long.</summary>
    /// <param name="now">The clock.</param>
    /// <returns>How many letters went back or were thrown away.</returns>
    /// <remarks>
    ///     ⚠ <b>A returned letter's cash on delivery is dropped.</b> Nobody took the goods, so nobody
    ///     owes anything — carrying the charge onto the return is how a sender ends up being billed for
    ///     their own parcel.
    /// </remarks>
    public int Expire(float now) {
        var returned = 0;

        foreach (var mail in byId.Values.ToArray()) {
            if (now < mail.Expires) {
                continue;
            }

            Box(mail.To).Remove(mail);
            byId.Remove(mail.Id);
            returned++;

            if (!mail.HasAttachments || mail.IsClaimed || !mail.From.IsSome) {
                continue;
            }

            // The goods are still in the escrow account, so the return moves nothing and carries no
            // charge: nobody took them, so nobody owes anything.
            Post(PlayerId.None, mail.From, $"Returned: {mail.Subject}", mail.Body, [.. mail.Attachments], default, 0, now, 30f);
        }

        return returned;
    }

    List<MailMessage> Box(PlayerId player) {
        if (boxes.TryGetValue(player, out var box)) {
            return box;
        }

        box = [];
        boxes.Add(player, box);

        return box;
    }
}
