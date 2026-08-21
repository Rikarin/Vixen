// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.Assets.Scenes;
using Vixen.Editor.Core.Scenes;
using Vixen.Engine.Scenes;
using Vixen.Net.Engine;
using Vixen.Net.Engine.Players;
using Xunit;

namespace Vixen.Editor.Assets.Tests;

/// <summary>
///     The two network markers, all the way from a <c>.vxprefab</c> to entities in a world.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim being asserted is that a designer can say it at all.</b> Which of a prefab's
///         nodes get a <c>NetworkId</c> was decided by asking whether the node carried one — and a
///         compiled scene may only name a component that is <c>[Component]</c> <b>and</b>
///         <c>[DataContract]</c>, so the marker was dropped by <c>SceneContent.Capture</c> without a
///         word and every prefab out of a content build had exactly one networked node. This is the
///         other end of that path from <c>ANetworkedMarkerSurvivesTheContentBuild</c>: authored keys,
///         through the YAML tag, into a block, and onto an entity.
///     </para>
///     <para>
///         ⚠ <b>The constructor touches a <c>Vixen.Net.Engine</c> type on purpose</b>, for the reason
///         <c>SceneRenderComponentTests</c> gives at length: components are declared by a
///         <c>[ModuleInitializer]</c> in the assembly that owns them, and one runs when its assembly
///         is loaded. A test that only ever named these in a YAML string would be asserting against a
///         registry that had never heard of them.
///     </para>
/// </remarks>
public sealed class SceneNetworkComponentTests {
    // ⚠ typeof and not `default(NetworkObject)`. A discarded default of an empty struct is an
    // expression with no side effects, which Roslyn is free to emit no IL for at all — and the whole
    // job of this line is to be a reference the runtime has to resolve. `ldtoken` is one.
    public SceneNetworkComponentTests() => _ = typeof(NetworkObject).Name;

    /// <remarks>
    ///     ⚠ The empty flow mapping is load-bearing. A tag has no members, and a node that is only a
    ///     type tag binds as a scalar rather than as a mapping — which fails, naming the member.
    /// </remarks>
    const string Turret = """
                          version: 1
                          name: Turret
                          roots:
                            - id: 0123456789abcdef0123456789abcdef
                              name: Turret
                              children:
                                - id: 11111111111111111111111111111111
                                  name: Barrel
                                  position: 0 1 0
                                  components:
                                    - !NetworkObject {}
                                - id: 22222222222222222222222222222222
                                  name: Sight
                                  position: 0 1 0.5
                                - id: 33333333333333333333333333333333
                                  name: Driver
                                  components:
                                    - !PlayerPawn {}
                          """;

    [Fact]
    public void AnAuthoredNetworkObjectCompilesAndLoadsOntoAnEntity() {
        var content = Compile();

        using var world = new World();
        var created = new Entity[4];
        content.Instantiate(world, created);

        // The root is index 0; the children follow in the order the file lists them.
        Assert.True(world.Has<NetworkObject>(created[1]));
        Assert.False(world.Has<NetworkObject>(created[0]));
        Assert.False(world.Has<NetworkObject>(created[2]));
    }

    /// <remarks>
    ///     <c>PlayerPawn</c> carried both attributes from the day it was written and was registered by
    ///     nothing, because analyzers do not flow through a <c>ProjectReference</c> and its assembly
    ///     named none. Asserted here rather than assumed: the failure is a prefab that compiles and a
    ///     client that never works out which body is its own.
    /// </remarks>
    [Fact]
    public void AnAuthoredPlayerPawnCompilesToo() {
        var content = Compile();

        using var world = new World();
        var created = new Entity[4];
        content.Instantiate(world, created);

        Assert.True(world.Has<PlayerPawn>(created[3]));
    }

    /// <remarks>
    ///     A tag has no bytes, so its column is empty and its presence in the block's component set is
    ///     the whole of what it says. Worth its own assertion: a binder that wrote a byte for a tag
    ///     would round-trip through this test's world and corrupt every column after it.
    /// </remarks>
    [Fact]
    public void ATagCostsNoBytesInItsColumn() {
        var content = Compile();

        var marked = content.Blocks
            .SelectMany(block => block.Columns)
            .Where(column => column.Component is "NetworkObject" or "PlayerPawn")
            .ToList();

        Assert.Equal(2, marked.Count);
        Assert.All(marked, column => Assert.Empty(column.Data));
    }

    static SceneContent Compile() {
        var problems = new List<string>();

        var content = SceneCompiler.Compile(
            SceneFile.FromYaml(Turret),
            (severity, message) => {
                if (severity == ImportSeverity.Error) {
                    problems.Add(message);
                }
            }
        );

        Assert.Empty(problems);
        Assert.NotNull(content);

        return content;
    }
}
