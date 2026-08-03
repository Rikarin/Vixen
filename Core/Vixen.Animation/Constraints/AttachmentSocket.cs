// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>An attachment point on a character, whose offset from the bone is itself solved.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A socket is adapted, not just read, and this is what makes a held prop work across
///         bodies.</b> A pistol hangs off an attachment point that is a child of a hand bone, and the
///         offset from bone to socket was authored against one hand. Put the same pistol in a hand
///         20 % larger and preserving that offset drives the grip into the palm; preserving the
///         <em>grip contact</em> and letting the offset move is what a person would do.
///     </para>
///     <para>
///         So the socket names a <see cref="SurfaceCoordinate" /> on the hand's own proxy shape, and
///         the solve moves the socket rather than a joint: the point on the prop that must touch the
///         palm is placed on the palm's surface, wherever that surface turned out to be for this body.
///         One authored socket, every hand.
///     </para>
///     <para>
///         ⚠ <b>This is also how a prop becomes solvable at all.</b> Without it, a goal on a held
///         object is a goal on something with no chain to move, and the only recourse is to move the
///         character's arm — which is right for reaching and wrong for adjusting a grip.
///     </para>
/// </remarks>
public sealed class AttachmentSocket {
    /// <summary>What it is called — <c>right-hand-grip</c>.</summary>
    public required Symbol Name { get; init; }

    /// <summary>Which joint it hangs off.</summary>
    public required int Joint { get; init; }

    /// <summary>Where it sits relative to that joint, as authored.</summary>
    public BoneTransform Offset { get; init; } = BoneTransform.Identity;

    /// <summary>
    ///     Where on the body it should touch, or <see langword="null" /> to leave the offset alone.
    /// </summary>
    public SurfaceCoordinate? Contact { get; init; }

    /// <summary>
    ///     The point on the attached thing that touches the body, in the socket's own space.
    /// </summary>
    /// <remarks>
    ///     Usually the middle of a grip. Zero means the socket's own origin is the contact, which is
    ///     right for a socket authored at the touching point in the first place.
    /// </remarks>
    public Vector3 Grip { get; init; }

    /// <summary>How much of the correction to apply, in <c>[0, 1]</c>.</summary>
    public float Weight { get; set; } = 1f;

    /// <summary>Where it ended up, in the character's model space.</summary>
    public BoneTransform Solved { get; private set; } = BoneTransform.Identity;

    /// <summary>Whether the last solve resolved its contact.</summary>
    /// <remarks>
    ///     False leaves <see cref="Solved" /> at the authored offset composed onto the joint — the
    ///     answer a game with no proxy shapes would get, which is the right thing to fall back to.
    /// </remarks>
    public bool IsAdapted { get; private set; }

    /// <summary>How far the contact had to move, in metres.</summary>
    public float Adjustment { get; private set; }

    /// <summary>Places the socket for the pose it is in.</summary>
    /// <param name="context">The pose and the shapes to resolve against.</param>
    /// <returns>Where it ended up.</returns>
    internal BoneTransform Solve(in ConstraintContext context) {
        IsAdapted = false;
        Adjustment = 0f;

        if ((uint)Joint >= (uint)context.Model.Length) {
            Solved = BoneTransform.Identity;
            return Solved;
        }

        var authored = BoneTransform.Concatenate(Offset, context.Model[Joint]);

        Solved = authored;

        if (Contact is not { } contact || Weight <= 0f) {
            return Solved;
        }

        if (!new SurfaceFrame(contact).TryResolve(context, out var frame)) {
            return Solved;
        }

        // The socket keeps the orientation the rig gave it and moves so its grip point lands on the
        // contact. Rotating it as well would make a hand's proxy shape decide which way a pistol
        // points, which is the rig's business and not the body's.
        var grip = authored.Translation + Quaternion.Transform(Grip * authored.Scale, authored.Rotation);
        var move = (frame.Origin - grip) * MathUtil.Saturate(Weight);

        Adjustment = move.Length();
        IsAdapted = true;
        Solved = new(authored.Translation + move, authored.Rotation, authored.Scale);

        return Solved;
    }
}

/// <summary>The attachment points one character carries.</summary>
/// <remarks>
///     ⚠ <b>Solved twice a frame, deliberately.</b> Once before the goals resolve, so a goal
///     expressed against a socket gets this frame's answer rather than last frame's, and once after
///     the chains have moved, so the game reads where the prop actually ended up. Solving only after
///     would leave an <see cref="AttachmentFrame" /> goal a frame behind — small, and exactly the kind
///     of small that reads as a held object jittering.
/// </remarks>
public sealed class AttachmentSockets {
    readonly List<AttachmentSocket> sockets = [];

    /// <summary>How many there are.</summary>
    public int Count => sockets.Count;

    /// <summary>One of them.</summary>
    /// <param name="index">Which.</param>
    /// <returns>The socket.</returns>
    public AttachmentSocket this[int index] => sockets[index];

    /// <summary>Adds one.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>The socket, so calls chain.</returns>
    public AttachmentSocket Add(AttachmentSocket socket) {
        ArgumentNullException.ThrowIfNull(socket);
        sockets.Add(socket);

        return socket;
    }

    /// <summary>Removes one.</summary>
    /// <param name="socket">The socket.</param>
    /// <returns>Whether it was there.</returns>
    public bool Remove(AttachmentSocket socket) => sockets.Remove(socket);

    /// <summary>The socket with a name, or <see langword="null" />.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The socket, or <see langword="null" />.</returns>
    public AttachmentSocket? Find(Symbol name) {
        foreach (var socket in sockets) {
            if (socket.Name == name) {
                return socket;
            }
        }

        return null;
    }

    /// <summary>The socket with a name, or <see langword="null" />.</summary>
    /// <param name="name">Its name.</param>
    /// <returns>The socket, or <see langword="null" />.</returns>
    public AttachmentSocket? Find(string name) => Find(Symbol.Intern(name));

    /// <summary>Places every socket for the pose it is in.</summary>
    /// <param name="context">The pose and the shapes to resolve against.</param>
    internal void Solve(in ConstraintContext context) {
        foreach (var socket in sockets) {
            socket.Solve(context);
        }
    }
}

/// <summary>One of the character's own attachment sockets, as a place a goal can be.</summary>
/// <param name="Socket">Which socket.</param>
/// <param name="Offset">Where, relative to it.</param>
/// <remarks>
///     Distinct from <see cref="SocketFrame" />, which names a socket on something <em>else</em> the
///     character is interacting with. This one names the character's own, after
///     <see cref="AttachmentSocket.Solve" /> has adapted it — so a second goal on the same prop is
///     expressed against where the grip actually ended up rather than against where it was authored.
/// </remarks>
public sealed record AttachmentFrame(Symbol Socket, BoneTransform Offset) : IConstraintFrame {
    /// <summary>A socket by name.</summary>
    /// <param name="socket">The socket.</param>
    public AttachmentFrame(string socket) : this(Symbol.Intern(socket), BoneTransform.Identity) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        if (context.Sockets?.Find(Socket) is not { } socket) {
            frame = default;
            return false;
        }

        frame = new(BoneTransform.Concatenate(Offset, socket.Solved));
        return true;
    }
}
