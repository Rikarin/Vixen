// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Nodes.Ecs;
using Vixen.Core.Mathematics;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>That this assembly's one placeable component actually reaches the engine's registry.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It did not, and nothing said so.</b> The project referenced
///         <c>Vixen.Core.Serialization.Generator</c> and <c>Vixen.Core.Reflection.Generator</c> and a
///         comment beside them said <see cref="PatrolRoute" /> was a scene component — but an
///         analyzer does not flow through a <c>ProjectReference</c>, so the third declaration, the
///         one <c>Vixen.Engine.Generators</c> emits, was never made. The symptom is not an error: a
///         component the Add Component menu cannot offer, a <c>.vxscene</c> that cannot name it, and
///         a captured world with no column for it. #1056, and the same shape as #989 and #1005.
///     </para>
///     <para>
///         <b>The negative is the half that keeps the positive honest.</b> A test that only asked
///         "is <see cref="PatrolRoute" /> declared" would still pass if the conjunction the generator
///         applies were widened to accept <c>[Component]</c> alone — and that widening is exactly
///         what would put <see cref="AiFocus" />, which holds an <c>Entity</c>, into a file.
///     </para>
/// </remarks>
public class SceneComponentTests {
    /// <remarks>
    ///     The touch is the behaviour, not a workaround: a module initializer runs when its assembly
    ///     is loaded, so a component nothing has referenced yet is not declared yet.
    /// </remarks>
    [Fact]
    public void APatrolRouteIsDeclaredByTheAssemblyThatOwnsIt() {
        _ = PatrolRoute.Of(PatrolMode.Loop, Vector3.Zero, Vector3.UnitX);

        Assert.True(SceneComponentRegistry.TryGet(typeof(PatrolRoute), out var binder));
        Assert.Equal(typeof(PatrolRoute), binder.ComponentType);
    }

    /// <remarks>
    ///     ⚠ <b>A focus is not written down, and this is the assertion that says why out loud.</b>
    ///     <see cref="AiFocus.Target" /> is an entity id — a dense, reused slot in one running
    ///     process — so a saved copy names a different entity on the way back, or nothing.
    ///     <c>VXS0416</c> refuses the pair at compile time; this is the same fact from the runtime's
    ///     side, where a regression that removed the analyzer would still be caught.
    /// </remarks>
    [Fact]
    public void AFocusHoldsAnEntityAndSoIsNotDeclared() {
        _ = AiFocus.At(Vector3.Zero);

        Assert.False(SceneComponentRegistry.TryGet(typeof(AiFocus), out _));
    }
}
