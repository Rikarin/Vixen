// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Vixen.Testing;
using Xunit;

namespace Vixen.App.Tests;

/// <summary>
///     That a project's <c>[GameSystem]</c> declarations reach a shipped game's frame, not only the
///     editor's play session.
/// </summary>
/// <remarks>
///     ⚠ <b>This is the asymmetry that would have made the whole declaration a trap.</b> The editor
///     reads <c>GameSystemRegistry</c> when Play is pressed. If the host did not, a project could
///     mark a system, watch it run in the editor, ship, and find it silently absent — which is worse
///     than never having been able to declare one. So the host calls <c>AddDeclaredSystems</c> the
///     moment <c>OnInitialise</c> returns, and these assert the frame rather than the call.
/// </remarks>
public sealed class HostedDeclaredSystemTests {
    [Fact]
    public void ADeclaredSystemRunsWhenTheGameRegisteredItsService() {
        var game = new SuppliesTheDepot();
        using var app = TestApp.Create(game);

        HostedFrame.Ran = 0;

        app.RunFrames(2);

        Assert.Equal(2, HostedFrame.Ran);

        // The service it was handed is the one the game registered, not one the host invented.
        Assert.Same(game.Depot, HostedFrame.Seen);
    }

    /// <summary>
    ///     ⚠ <b>Absent is a warning, not an exception.</b> One system that cannot be built must not
    ///     stop the game from starting — the same trade the catalog, the shader bundle and the
    ///     startup scene each make — so what this asserts is that the host still boots and runs
    ///     frames while the declared system does not.
    /// </summary>
    [Fact]
    public void ADeclaredSystemWhoseServiceIsAbsentDoesNotStopTheGameStarting() {
        using var app = TestApp.Create(new SilentGame());

        HostedFrame.Ran = 0;

        app.RunFrames(1);

        Assert.Equal(1, app.Services.Engine!.Time.FrameCount);
        Assert.Equal(0, HostedFrame.Ran);
    }

    class SilentGame : Game {
        protected internal override void OnConfigure(AppConfig config) => config.Window = null;
    }

    /// <summary>What a project writes: the service, registered in <c>OnInitialise</c>. Not the system.</summary>
    sealed class SuppliesTheDepot : SilentGame {
        public Depot Depot { get; } = new();

        protected internal override void OnInitialise() => Services.Registry.Add(Depot);
    }
}

/// <summary>A service only this file's game registers.</summary>
public sealed class Depot;

/// <summary>A system this assembly declares, which the host is expected to add.</summary>
[GameSystem]
public sealed class HostedFrame(Depot depot) : SystemBase {
    /// <summary>How many frames reached it.</summary>
    public static int Ran { get; set; }

    /// <summary>What the last construction was handed.</summary>
    public static Depot? Seen { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Ran++;
        Seen = depot;

        return dependency;
    }
}
