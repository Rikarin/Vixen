// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>Something that can say where it is, and where its named attachment points are.</summary>
/// <remarks>
///     <para>
///         <b>The other-party seam.</b> What a clip's constraint names is a role — <c>held-item</c>,
///         <c>ground</c>, <c>the person I am shaking hands with</c> — and what fills that role is a
///         question only the game can answer. An entity, a prop with its own skeleton, a moving
///         platform, a table of participants resolved per interaction: all of them can answer "where
///         are you, and where is your <c>grip</c>".
///     </para>
///     <para>
///         ⚠ <b>World space, and asked once per frame per slot.</b> A source is polled during the
///         resolve pass, on whatever thread the animator is being updated on, so it must not touch
///         the ECS world or the physics scene. What it returns is a value somebody else already
///         computed.
///     </para>
/// </remarks>
public interface IBindingSource {
    /// <summary>Where it is.</summary>
    /// <param name="socket">
    ///     Which attachment point, or <see cref="Symbol.None" /> for the thing itself.
    /// </param>
    /// <param name="world">Where, in world space.</param>
    /// <returns>Whether it exists right now, and has that socket.</returns>
    bool TryGetFrame(Symbol socket, out BoneTransform world);
}

/// <summary>A binding that is just a transform somebody writes.</summary>
/// <remarks>
///     The common case, and the one the ECS glue uses: a system copies a world matrix in once a
///     frame and every goal bound to the slot reads it. <see cref="IsValid" /> is what a despawn
///     clears, which is the path <see cref="IConstraintFrame.TryResolve" />'s failure case exists for.
/// </remarks>
public sealed class TransformBinding : IBindingSource {
    /// <summary>Where it is, in world space.</summary>
    public BoneTransform Transform { get; set; } = BoneTransform.Identity;

    /// <summary>Whether it is there at all. Cleared on a despawn rather than unbinding the slot.</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>Its attachment points, in its own space.</summary>
    public Dictionary<Symbol, BoneTransform> Sockets { get; } = [];

    /// <summary>Adds or moves an attachment point.</summary>
    /// <param name="socket">What it is called.</param>
    /// <param name="local">Where it is, relative to the thing itself.</param>
    /// <returns>This, so calls chain.</returns>
    public TransformBinding Socket(string socket, BoneTransform local) {
        Sockets[Symbol.Intern(socket)] = local;
        return this;
    }

    /// <inheritdoc />
    public bool TryGetFrame(Symbol socket, out BoneTransform world) {
        if (!IsValid) {
            world = default;
            return false;
        }

        if (!socket.IsSome) {
            world = Transform;
            return true;
        }

        if (!Sockets.TryGetValue(socket, out var local)) {
            world = default;
            return false;
        }

        world = BoneTransform.Concatenate(local, Transform);
        return true;
    }
}

/// <summary>Who the other parties are, and where the game says things are this frame.</summary>
/// <remarks>
///     <para>
///         Two stores with different lifetimes, deliberately. <b>Bindings persist</b> — a character
///         holding a sword has that slot filled for as long as it holds it — and <b>provided frames
///         do not</b>: they are written during the frame that computed them and cleared with it, so
///         a provider that stops writing produces an unresolved frame and an ease-out rather than a
///         goal pinned to a stale answer.
///     </para>
/// </remarks>
public sealed class ConstraintBindings {
    readonly Dictionary<Symbol, IBindingSource> slots = [];
    readonly Dictionary<Symbol, BoneTransform> provided = [];

    /// <summary>How many slots are filled.</summary>
    public int Count => slots.Count;

    /// <summary>Fills a slot.</summary>
    /// <param name="slot">The role.</param>
    /// <param name="source">What fills it, or <see langword="null" /> to clear it.</param>
    public void Set(Symbol slot, IBindingSource? source) {
        if (source is null) {
            slots.Remove(slot);
            return;
        }

        slots[slot] = source;
    }

    /// <summary>Fills a slot.</summary>
    /// <param name="slot">The role.</param>
    /// <param name="source">What fills it, or <see langword="null" /> to clear it.</param>
    public void Set(string slot, IBindingSource? source) => Set(Symbol.Intern(slot), source);

    /// <summary>What fills a slot, if anything.</summary>
    /// <param name="slot">The role.</param>
    /// <returns>The source, or <see langword="null" />.</returns>
    public IBindingSource? Source(Symbol slot) => slots.GetValueOrDefault(slot);

    /// <summary>Where a slot's thing is, or its socket.</summary>
    /// <param name="slot">The role.</param>
    /// <param name="socket">The attachment point, or <see cref="Symbol.None" />.</param>
    /// <param name="world">Where, in world space.</param>
    /// <returns>Whether it resolved.</returns>
    public bool TryResolve(Symbol slot, Symbol socket, out BoneTransform world) {
        if (slots.TryGetValue(slot, out var source)) {
            return source.TryGetFrame(socket, out world);
        }

        world = default;
        return false;
    }

    /// <summary>Writes a frame the game computed itself.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="world">Where, in world space.</param>
    public void Provide(Symbol name, in BoneTransform world) => provided[name] = world;

    /// <summary>Writes a frame the game computed itself.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="world">Where, in world space.</param>
    public void Provide(string name, in BoneTransform world) => Provide(Symbol.Intern(name), world);

    /// <summary>Reads a frame the game computed.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="world">Where, in world space.</param>
    /// <returns>Whether it was written this frame.</returns>
    public bool TryGetProvided(Symbol name, out BoneTransform world) => provided.TryGetValue(name, out world);

    /// <summary>Forgets every provided frame, keeping the bindings.</summary>
    /// <remarks>
    ///     Called at the end of an animator's update. A provided frame outliving the frame that
    ///     produced it is the difference between a constraint that eases out when its provider stops
    ///     and one that holds a position from four seconds ago.
    /// </remarks>
    public void ClearProvided() => provided.Clear();

    /// <summary>Forgets everything.</summary>
    public void Clear() {
        slots.Clear();
        provided.Clear();
    }
}
