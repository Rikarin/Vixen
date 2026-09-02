// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Frames;

namespace Vixen.Samples.AiVillage.Tests;

/// <summary>The sample's village on a real <see cref="EngineLoop" />, with no window and no device.</summary>
/// <remarks>
///     <para>
///         <b>An <c>EngineLoop</c> and not a hand-written stepping loop, which is the whole reason
///         this suite is worth having beside <c>VillageSampleTests</c>.</b> That suite steps
///         perception, then the agents, then navigation, in the order it decided on — so it can
///         never fail because the engine put them in a different one. Here the systems arrive with
///         their phases and their <c>[UpdateBefore]</c> and the scheduler sorts them, which is the
///         arrangement the game actually runs.
///     </para>
///     <para>
///         ⚠ <b>A fixed step handed in by the caller, and no clock anywhere.</b> The sample's
///         intruder is a pure function of accumulated delta, so a suite that slept and measured
///         would be asserting about this machine.
///     </para>
/// </remarks>
public sealed class VillageRun : IDisposable {
    /// <summary>The step every frame advances by: sixty a second, exactly.</summary>
    public const double Step = 1.0 / 60.0;

    long frame;

    /// <summary>Builds the loop, the village and the log.</summary>
    /// <remarks>
    ///     ⚠ <b>This constructor is the host, and that is the point of it.</b> The sample's
    ///     <c>IntruderSystem</c> carries <c>[GameSystem]</c>, so in the game
    ///     <c>VixenApplication.Initialise</c> resolves it against <c>AppServices.Registry</c>; here
    ///     the same declaration is resolved against a registry this fixture owns. If the two ever
    ///     disagreed the sample would run one frame and the suite another, which is exactly the
    ///     failure a hand-stepped fixture cannot see.
    /// </remarks>
    public VillageRun() {
        Loop = new EngineLoop();
        Village = new Village(Loop.World);
        Village.Register(Loop, Services);

        // Only this project's own, because the suite links the sample's sources and nothing else
        // here declares a frame — naming the assembly is what a host embedding two projects does.
        Declared = Loop.AddDeclaredSystems(Services, Ours);

        Decisions = new DecisionLog(Village);

        Village.Agents.Debug.Enabled = true;
    }

    /// <summary>Where the intruder is looked up from — a game's <c>AppServices.Registry</c> stands in.</summary>
    public ServiceRegistry Services { get; } = new();

    /// <summary>What the declaration bought: the systems that were added, and any that were not.</summary>
    public FrameActivation Declared { get; }

    /// <summary>The engine's frame loop.</summary>
    public EngineLoop Loop { get; }

    /// <summary>The three agents and everything they share.</summary>
    public Village Village { get; }

    /// <summary>What they decided.</summary>
    public DecisionLog Decisions { get; }

    /// <summary>How far into the intruder's script the run has got.</summary>
    public double Elapsed => Village.Script.Elapsed;

    /// <summary>Runs frames.</summary>
    /// <param name="count">How many.</param>
    public void Frames(int count) {
        for (var index = 0; index < count; index++) {
            Loop.Frame(TimeSpan.FromSeconds(Step));
            Decisions.Observe(frame, Village.Script.Elapsed);
            frame++;
        }
    }

    /// <summary>Runs until a moment in the intruder's script.</summary>
    /// <param name="seconds">Which moment.</param>
    public void Until(double seconds) {
        while (Village.Script.Elapsed < seconds) {
            Frames(1);
        }
    }

    /// <inheritdoc />
    public void Dispose() => Loop.Dispose();

    static bool Ours(GameSystemDeclaration declaration) =>
        declaration.SystemType.Assembly == typeof(Village).Assembly;
}
