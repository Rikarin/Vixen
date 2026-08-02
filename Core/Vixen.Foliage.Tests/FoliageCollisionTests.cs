// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Foliage.Tests;

/// <summary>Collision within an activation radius — [docs/plan/31 § D10].</summary>
public sealed class FoliageCollisionTests {
    static FoliageInstance At(float x, float z) => new(new(x, 0f, z), Quaternion.Identity, 1f);

    static FoliageType Colliding =>
        Types.Tree with { CollisionShape = "Shapes/trunk", ActivationRadius = 30f };

    static (FoliageVolume Volume, int Type) Built(FoliageType? type = null) {
        var volume = new FoliageVolume(new(32f));

        return (volume, volume.AddType(type ?? Colliding));
    }

    [Fact]
    public void Only_instances_inside_the_radius_get_a_body() {
        var (volume, type) = Built();

        volume.Add(type, At(10f, 0f));
        volume.Add(type, At(25f, 0f));
        volume.Add(type, At(200f, 0f));

        var collision = new FoliageCollision();

        Assert.Equal(2, collision.Update(volume, [Vector3.Zero]));
        Assert.Equal(2, collision.Activated.Count);
        Assert.Empty(collision.Deactivated);
    }

    /// <summary>The radius is the type's, so one source gives a different answer per type.</summary>
    /// <remarks>
    ///     ⚠ A boulder wants a body from further away than a fern does. A single global radius makes
    ///     one of them wrong, which is the setting a project reaches for when a vehicle drives through
    ///     a rock it should have hit.
    /// </remarks>
    [Fact]
    public void Each_type_uses_its_own_radius() {
        var volume = new FoliageVolume(new(32f));

        var near = volume.AddType(Colliding with { Name = "Fern", ActivationRadius = 10f });
        var far = volume.AddType(Colliding with { Name = "Boulder", ActivationRadius = 100f });

        volume.Add(near, At(50f, 0f));
        volume.Add(far, At(50f, 0f));

        var collision = new FoliageCollision();
        collision.Update(volume, [Vector3.Zero]);

        Assert.Single(collision.Active);
        Assert.Equal(far, collision.Active.Single().Type);
    }

    /// <summary>What comes out is the difference, so a pool can act on four addresses not ten thousand.</summary>
    [Fact]
    public void Moving_the_source_reports_only_what_changed() {
        var (volume, type) = Built();

        for (var x = 0; x < 200; x += 10) {
            volume.Add(type, At(x, 0f));
        }

        var collision = new FoliageCollision();

        collision.Update(volume, [Vector3.Zero]);
        var first = collision.Count;

        Assert.True(first > 0);

        collision.Update(volume, [new(40f, 0f, 0f)]);

        Assert.NotEmpty(collision.Activated);
        Assert.NotEmpty(collision.Deactivated);

        // And the ones that were in range both times are reported neither way.
        Assert.True(
            collision.Activated.Count + collision.Deactivated.Count < first + collision.Count,
            "everything was reported as changed, so the difference is not a difference."
        );
    }

    [Fact]
    public void An_unchanged_source_reports_nothing() {
        var (volume, type) = Built();

        volume.Add(type, At(10f, 0f));

        var collision = new FoliageCollision();

        collision.Update(volume, [Vector3.Zero]);
        collision.Update(volume, [Vector3.Zero]);

        Assert.Empty(collision.Activated);
        Assert.Empty(collision.Deactivated);
        Assert.Equal(1, collision.Count);
    }

    [Fact]
    public void A_type_with_no_shape_never_allocates_anything() {
        var (volume, type) = Built(Types.Tree);

        volume.Add(type, At(1f, 1f));

        Assert.Equal(0, new FoliageCollision().Update(volume, [Vector3.Zero]));
    }

    /// <summary>Grass never collides, whatever it declares.</summary>
    [Fact]
    public void A_derived_type_is_never_asked() {
        var (volume, type) = Built(Colliding with { Storage = FoliageStorage.Derived });

        volume.Add(type, At(1f, 1f));

        Assert.Equal(0, new FoliageCollision().Update(volume, [Vector3.Zero]));
    }

    [Fact]
    public void Two_sources_both_activate() {
        var (volume, type) = Built();

        volume.Add(type, At(0f, 0f));
        volume.Add(type, At(300f, 0f));

        var collision = new FoliageCollision();

        Assert.Equal(2, collision.Update(volume, [Vector3.Zero, new(300f, 0f, 0f)]));
    }

    /// <summary>Clearing hands the whole set back, so a pool can return it.</summary>
    /// <remarks>
    ///     ⚠ Clearing silently leaks every body the caller had allocated — into a physics world that
    ///     is about to be handed a different volume's instances at the same addresses.
    /// </remarks>
    [Fact]
    public void Clearing_reports_every_body_as_deactivated() {
        var (volume, type) = Built();

        volume.Add(type, At(1f, 1f));
        volume.Add(type, At(2f, 2f));

        var collision = new FoliageCollision();
        collision.Update(volume, [Vector3.Zero]);

        collision.Clear();

        Assert.Equal(2, collision.Deactivated.Count);
        Assert.Equal(0, collision.Count);
    }

    /// <summary>An erased instance's address has to be forgotten, not left to the next update.</summary>
    /// <remarks>
    ///     ⚠ <b>An erased instance's address now belongs to whichever instance shifted down into
    ///     it.</b> The next update would find that one already active and never give it a body of its
    ///     own — a tree with a hole where its collision should be, for as long as the level runs.
    /// </remarks>
    [Fact]
    public void Forgetting_an_erased_address_lets_its_successor_get_a_body() {
        var (volume, type) = Built();

        var first = volume.Add(type, At(1f, 1f));
        volume.Add(type, At(3f, 3f));

        var collision = new FoliageCollision();
        collision.Update(volume, [Vector3.Zero]);

        Assert.Equal(2, collision.Count);

        // Erase the first; the second slides into index 0.
        volume.Remove([first]);
        Assert.True(collision.Forget(new(type, new(0, 0), 1)));

        collision.Update(volume, [Vector3.Zero]);

        Assert.Equal(1, collision.Count);
        Assert.Empty(collision.Activated);
    }

    [Fact]
    public void Forgetting_a_whole_cell_forgets_only_that_cell() {
        var (volume, type) = Built();

        volume.Add(type, At(1f, 1f));
        volume.Add(type, At(40f, 1f));

        var collision = new FoliageCollision();
        collision.Update(volume, [Vector3.Zero]);

        var before = collision.Count;

        Assert.Equal(1, collision.ForgetCell(type, new(0, 0)));
        Assert.Equal(before - 1, collision.Count);
    }
}
