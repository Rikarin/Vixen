// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using JoltPhysicsSharp;
using Vixen.Physics.Characters;

namespace Vixen.Physics;

public sealed partial class PhysicsWorld {
    /// <summary>The Jolt system, which the character controller has to be handed on every call.</summary>
    internal PhysicsSystem JoltSystem => system;

    /// <summary>How many character controllers this world has.</summary>
    public int CharacterCount => characters.Count;

    /// <summary>Creates a character controller in this world.</summary>
    /// <param name="settings">How it behaves.</param>
    /// <returns>The controller. Disposing it removes it; so does disposing the world.</returns>
    public CharacterController CreateCharacter(CharacterControllerSettings settings) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Shape.IsNone) {
            throw new PhysicsShapeException("A character needs a shape.", nameof(settings));
        }

        var controller = new CharacterController(this, system, settings);
        characters.Add(controller);
        return controller;
    }

    internal void Forget(CharacterController controller) => characters.Remove(controller);
}
