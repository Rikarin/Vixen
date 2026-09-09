// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Perception.Ecs;
using Vixen.Engine.Scenes;
using Xunit;

namespace Vixen.Ai.Perception.Tests;

/// <summary>That the placeable half of perception reaches the engine's registry.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same gap as #1056's, found in the same pass and equally silent.</b> The project
///         named the serialization and reflection generators and a comment beside them said
///         <see cref="AiStimuliSource" /> was authored in the level editor — but the declaration that
///         puts it in the Add Component menu and lets a <c>.vxscene</c> attach it comes from
///         <c>Vixen.Engine.Generators</c>, and analyzers do not flow through a
///         <c>ProjectReference</c>. Referencing <c>Vixen.Engine</c> bought the registry's type and
///         none of its declarations.
///     </para>
///     <para>
///         <b>Both directions.</b> <see cref="AiPerception" /> carries <c>[Component]</c> alone on
///         purpose, because <see cref="AiPerception.ListenerIndex" /> is the system's own bookkeeping
///         and a saved copy names a slot the loading process does not have. A conjunction widened to
///         one attribute would put it in a file, so the negative is what proves the rule is still a
///         conjunction rather than a marker.
///     </para>
/// </remarks>
public class SceneComponentTests {
    /// <remarks>
    ///     The touch is the behaviour: a module initializer runs on assembly load, so a component
    ///     nothing has referenced yet is not declared yet.
    /// </remarks>
    [Fact]
    public void AStimuliSourceIsDeclaredByTheAssemblyThatOwnsIt() {
        _ = AiStimuliSource.Perceivable();

        Assert.True(SceneComponentRegistry.TryGet(typeof(AiStimuliSource), out var binder));
        Assert.Equal(typeof(AiStimuliSource), binder.ComponentType);
    }

    [Fact]
    public void TheSystemsOwnBookkeepingIsNotDeclared() {
        _ = AiStimuliSource.Perceivable();

        Assert.False(SceneComponentRegistry.TryGet(typeof(AiPerception), out _));
    }
}
