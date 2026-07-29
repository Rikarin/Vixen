// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Rendering.IrradianceFields.Tests;

/// <summary>
///     Light where it should not be, which is the defect users actually report — doc 19's risk G3.
/// </summary>
/// <remarks>
///     <para>
///         Every field here is 16 × 16 × 16 world units over four bricks an axis, so a probe spacing
///         is exactly one unit and a wall's thickness can be stated in probes. That is the only unit
///         any of this is really in: a leak is not about how thick a wall is, it is about how thick a
///         wall is <i>compared to the probes</i>.
///     </para>
///     <para>
///         Probes carry one number and the exterior's is ten, so a leak is not a subtle discrepancy —
///         it is the outside's brightness turning up in a closed room, and the assertions say so in
///         absolute terms rather than as a ratio.
///     </para>
/// </remarks>
public class LeakTests {
    const float Outside = 10f;

    static IrradianceField Room() =>
        new(new BoundingBox(new(0f), new(16f)), new(4));

    /// <summary>
    ///     <b>Doc 19's stated exit criterion for L2: a closed box lit from outside stays dark.</b> The
    ///     walls are three probes thick, which is the case this scheme handles — the trilinear stencil
    ///     of any interior surface cannot reach an exterior probe, and dilation repairs each face of a
    ///     wall inward from its own side without the two ever mixing.
    /// </summary>
    /// <param name="passes">
    ///     How far dilation is allowed to travel. Varied deliberately: it is what people assume the
    ///     leak knob is, and it is not. A repair never overwrites a valid probe, so once the face
    ///     touching the room has been filled from the room, no number of further passes can carry the
    ///     outside's light past it. The knob is how thick the wall is in probes.
    /// </param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    public void AClosedBoxStaysDark(int passes) {
        var field = Boxed(interior: 0f);

        field.Dilate(passes);
        field.SyncBorders();

        foreach (var (point, normal) in InteriorSurfaces()) {
            var lit = field.Irradiance(point, normal).X;

            Assert.True(lit < 0.01f, $"{Outside} of outside light reached {point} facing {normal}: {lit}");
        }
    }

    /// <summary>
    ///     <b>And the case it does not handle, stated rather than hidden.</b> A wall exactly one probe
    ///     thick is one plane of invalid probes touching the room on one side and the outside on the
    ///     other, so its repair is the average of the two — a leak at full strength, in a single pass.
    ///     Refinement is what fixes this: finer bricks near geometry make the same wall more probes
    ///     thick.
    /// </summary>
    [Fact]
    public void AWallOneProbeThickLeaksWhenItIsDilated() {
        var field = Room();

        field.AllocateAll();

        // Bright on one side, dark on the other, and a single invalid plane between them.
        Fill(field, position => position.X switch {
            < 8f => Probes.Of(Outside),
            > 8f => Probes.Of(0f),
            _ => IrradianceProbe.Empty
        });

        field.Dilate();
        field.SyncBorders();

        var lit = field.Irradiance(new(8.5f, 8f, 8f), new(1, 0, 0)).X;

        Assert.True(lit > 1f, $"the one-probe wall did not leak, so this test no longer says anything: {lit}");
    }

    /// <summary>
    ///     Worse than a leak, because nothing here can even see it: a wall thinner than the probe
    ///     spacing holds no probes at all, so every probe is valid, dilation has nothing to repair, and
    ///     a trilinear stencil spans straight through the wall from one side to the other. The normal
    ///     bias does not help either — it moves along the surface's own normal, and a floor's normal is
    ///     not the direction the wall is thin in.
    /// </summary>
    [Fact]
    public void AWallThinnerThanTheProbeSpacingIsNotThere() {
        var field = Room();

        field.AllocateAll();

        // The wall occupies 8.1 to 8.7, so probes at 8 and 9 are both outside it and both valid.
        Fill(field, position => Probes.Of(position.X < 8.4f ? Outside : 0f));

        Assert.Equal(0, field.Dilate(4));

        field.SyncBorders();

        var lit = field.Irradiance(new(8.75f, 8f, 8f), new(0, 1, 0)).X;

        Assert.True(lit > 1f, $"the sub-spacing wall did not leak, so this test no longer says anything: {lit}");
    }

    /// <summary>
    ///     <b>What dilation is actually for.</b> A probe inside a wall holds nothing, and nothing is a
    ///     colour — so without dilation every surface within a probe spacing of a wall reads part of a
    ///     hole and comes out dark. That rind is what people describe as "the GI looks dirty".
    /// </summary>
    [Fact]
    public void DilationRemovesTheDarkRindBesideAWall() {
        const float Ambient = 4f;

        var unrepaired = Boxed(Ambient);

        unrepaired.SyncBorders();

        var beside = new Vector3(4f, 8f, 8f);
        var inward = new Vector3(1, 0, 0);

        Assert.True(
            unrepaired.Irradiance(beside, inward).X < Ambient * 0.5f,
            "the rind was not there to begin with, so removing it proves nothing"
        );

        var repaired = Boxed(Ambient);

        repaired.Dilate();
        repaired.SyncBorders();

        Assert.Equal(Ambient, repaired.Irradiance(beside, inward).X, 3);
    }

    /// <summary>
    ///     One pass reaches one probe, because a repair is applied after the sweep that decided it —
    ///     which is also what stops the result depending on which way the loops run.
    /// </summary>
    [Fact]
    public void DilationReachesOneProbeFurtherPerPass() {
        var once = Boxed(interior: 1f);
        var twice = Boxed(interior: 1f);

        once.Dilate();
        twice.Dilate(2);

        // The wall's probes run from 2 to 4; 4 touches the room and 3 is one further in.
        Assert.True(once.TryGetLattice(new(4, 8, 8), out var touching) && touching.Validity > 0f);
        Assert.True(once.TryGetLattice(new(3, 8, 8), out var deeper) && deeper.Validity == 0f);
        Assert.True(twice.TryGetLattice(new(3, 8, 8), out deeper) && deeper.Validity > 0f);
    }

    [Fact]
    public void DilationRepairsNothingWhenNothingIsBroken() {
        var field = Room();

        field.AllocateAll();
        Fill(field, _ => Probes.Of(1f));

        Assert.Equal(0, field.Dilate(4));
    }

    /// <summary>A repair travels only as far as there are holes, and stops rather than looping.</summary>
    [Fact]
    public void DilationStopsWhenItRunsOutOfHoles() {
        var field = Boxed(interior: 1f);

        var first = field.Dilate(16);
        var second = field.Dilate(16);

        Assert.True(first > 0);
        Assert.Equal(0, second);
    }

    /// <summary>
    ///     The bias moves the lookup along the normal and nowhere else, by the fraction of a probe
    ///     spacing it says. A surface stands exactly on the boundary between the probes that saw the
    ///     room and the probes inside the wall it is part of, and this is what puts the lookup on the
    ///     side it faces.
    /// </summary>
    [Fact]
    public void TheNormalBiasMovesTheLookupAlongTheNormal() {
        var field = Room();

        field.AllocateAll();
        Fill(field, position => Probes.Of(position.X));
        field.SyncBorders();

        var at = new Vector3(8f, 8f, 8f);

        field.NormalBias = 0f;

        Assert.True(field.TrySample(at, new(1, 0, 0), out var unbiased));
        Assert.Equal(8f, unbiased.Value(), 3);

        field.NormalBias = 0.5f;

        Assert.True(field.TrySample(at, new(1, 0, 0), out var forward));
        Assert.Equal(8.5f, forward.Value(), 3);

        Assert.True(field.TrySample(at, new(-1, 0, 0), out var backward));
        Assert.Equal(7.5f, backward.Value(), 3);

        Assert.True(field.TrySample(at, new(0, 1, 0), out var sideways));
        Assert.Equal(8f, sideways.Value(), 3);
    }

    /// <summary>
    ///     A room with walls three probes thick: solid from 2 to 14, air from 5 to 11 in probes.
    /// </summary>
    /// <param name="interior">What the probes in the room saw.</param>
    /// <returns>The field, filled but neither dilated nor synced.</returns>
    static IrradianceField Boxed(float interior) {
        var field = Room();

        field.AllocateAll();

        Fill(field, position => {
            if (Air(position)) {
                return Probes.Of(interior);
            }

            return Solid(position) ? IrradianceProbe.Empty : Probes.Of(Outside);
        });

        return field;
    }

    /// <summary>Whether a probe stands in the room's air.</summary>
    static bool Air(Vector3 position) =>
        position.X is >= 5f and <= 11f
        && position.Y is >= 5f and <= 11f
        && position.Z is >= 5f and <= 11f;

    /// <summary>Whether a probe stands in the shell, which is everything from 2 to 14 that is not air.</summary>
    static bool Solid(Vector3 position) =>
        position.X is >= 2f and <= 14f
        && position.Y is >= 2f and <= 14f
        && position.Z is >= 2f and <= 14f;

    /// <summary>Points on the room's inner faces and in its middle, each facing into the air.</summary>
    static IEnumerable<(Vector3 Point, Vector3 Normal)> InteriorSurfaces() {
        for (var a = 5f; a <= 11f; a += 2f) {
            for (var b = 5f; b <= 11f; b += 2f) {
                yield return (new(4.5f, a, b), new(1, 0, 0));
                yield return (new(11.5f, a, b), new(-1, 0, 0));
                yield return (new(a, 4.5f, b), new(0, 1, 0));
                yield return (new(a, 11.5f, b), new(0, -1, 0));
                yield return (new(a, b, 4.5f), new(0, 0, 1));
                yield return (new(a, b, 11.5f), new(0, 0, -1));
                yield return (new(8f, a, b), new(0, 1, 0));
            }
        }
    }

    /// <summary>Fills every probe of a field from what stands where it does.</summary>
    static void Fill(IrradianceField field, Func<Vector3, IrradianceProbe> what) {
        var lattice = field.LatticeResolution;

        for (var z = 0; z < lattice.Z; z++) {
            for (var y = 0; y < lattice.Y; y++) {
                for (var x = 0; x < lattice.X; x++) {
                    var at = new Int3(x, y, z);

                    field.SetLattice(at, what(field.LatticePosition(at)));
                }
            }
        }
    }
}
