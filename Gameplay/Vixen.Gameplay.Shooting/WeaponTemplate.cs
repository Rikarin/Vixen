// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Gameplay.Shooting;

/// <summary>Where a shot is aimed, relative to where the shooter was looking.</summary>
/// <param name="Pitch">How far up, in degrees.</param>
/// <param name="Yaw">How far sideways, in degrees. Positive is right.</param>
/// <remarks>
///     ⚠ <b>Two angles rather than a direction vector, and that is not laziness.</b> A vector implies
///     a coordinate convention and a basis, and this library has neither — it does not know where the
///     shooter is or which way is up. The caller applies these to its own aim basis, which is the
///     only place that knows what "up" means.
/// </remarks>
public readonly record struct ShotDirection(float Pitch, float Yaw);

/// <summary>A weapon with its names resolved.</summary>
public sealed class WeaponTemplate {
    readonly GameplayTag[] tags;
    readonly ShotDirection[] recoil;

    internal WeaponTemplate(WeaponDefinition definition, GameplayTag[] tags, GameplayTag school, ShotDirection[] recoil) {
        Definition = definition;
        this.tags = tags;
        School = school;
        this.recoil = recoil;
    }

    /// <summary>What it was compiled from.</summary>
    public WeaponDefinition Definition { get; }

    /// <summary>Its id.</summary>
    public DefId Id => Definition.Id;

    /// <summary>How its shot reaches what it hits.</summary>
    public WeaponKind Kind => Definition.Kind;

    /// <summary>What kind of damage it does.</summary>
    public GameplayTag School { get; }

    /// <summary>What this weapon is.</summary>
    public ReadOnlySpan<GameplayTag> Tags => tags;

    /// <summary>Its recoil pattern, one step per shot. Empty is no recoil.</summary>
    public ReadOnlySpan<ShotDirection> RecoilPattern => recoil;

    /// <summary>How many pellets one shot fires, never below one.</summary>
    public int Pellets => Math.Max(1, Definition.Pellets);

    /// <summary>How long between shots, in seconds.</summary>
    public float FireInterval => Definition.RoundsPerSecond > 0f ? 1f / Definition.RoundsPerSecond : 0f;

    /// <summary>How many rounds a magazine holds, never below one.</summary>
    public int Magazine => Math.Max(1, Definition.Magazine);

    /// <summary>How far it reaches, in metres.</summary>
    public float Range => MathF.Max(0f, Definition.Range);

    /// <summary>How long a reload takes, in seconds.</summary>
    public float ReloadTime => MathF.Max(0f, Definition.ReloadTime);

    /// <summary>How many things one shot may pass through.</summary>
    public int MaximumPenetrations => Math.Max(0, Definition.Penetration?.MaximumTargets ?? 0);

    /// <summary>What one pellet is worth at a distance, before the damage pipeline.</summary>
    /// <param name="distance">How far away, in metres.</param>
    /// <returns>The amount.</returns>
    /// <remarks>
    ///     Linear between the two falloff distances, because a curve is a designer's tuning problem
    ///     and two numbers plus a floor is what every shooter's weapon sheet actually holds.
    /// </remarks>
    public float DamageAt(float distance) {
        var amount = Definition.Damage?.Amount ?? 0f;

        if (Definition.Falloff is not { End: > 0f } falloff || distance <= falloff.Start) {
            return amount;
        }

        var floor = Math.Clamp(falloff.Minimum, 0f, 1f);

        if (distance >= falloff.End || falloff.End <= falloff.Start) {
            return amount * floor;
        }

        var travelled = (distance - falloff.Start) / (falloff.End - falloff.Start);

        return amount * (1f - (travelled * (1f - floor)));
    }

    /// <summary>What a pellet is worth after passing through things.</summary>
    /// <param name="distance">How far away, in metres.</param>
    /// <param name="penetrated">How many things it went through first.</param>
    /// <returns>The amount, or zero when it has gone through too many.</returns>
    public float DamageAfter(float distance, int penetrated) {
        if (penetrated <= 0) {
            return DamageAt(distance);
        }

        if (penetrated > MaximumPenetrations) {
            return 0f;
        }

        var fraction = Math.Clamp(Definition.Penetration?.DamageFraction ?? 0f, 0f, 1f);

        return DamageAt(distance) * MathF.Pow(fraction, penetrated);
    }

    /// <summary>Where one pellet of one shot goes.</summary>
    /// <param name="shot">Which shot, counting from one for this holder.</param>
    /// <param name="pellet">Which pellet of it.</param>
    /// <param name="spread">The cone's half-angle right now, in degrees.</param>
    /// <returns>The offset from where the shooter was aiming.</returns>
    /// <remarks>
    ///     ⚠ <b>A pure function of (shot, pellet, spread), so both ends compute the same cone.</b>
    ///     That is what makes a hit claim checkable at all: the server recomputes where the client's
    ///     pellets could have gone rather than taking its word for it. A stream seeded from anything
    ///     else — a clock, an ambient random — would make every claim unfalsifiable.
    /// </remarks>
    public static ShotDirection Deviate(uint shot, int pellet, float spread) {
        if (spread <= 0f) {
            return default;
        }

        var random = GameplayRandom.For(shot, (ulong)pellet);

        // A uniform point in the disc rather than a uniform angle and radius: the second bunches
        // pellets in the middle, which reads as a shotgun that is far more accurate than its cone.
        var angle = random.NextFloat() * MathF.Tau;
        var radius = spread * MathF.Sqrt(random.NextFloat());

        return new(radius * MathF.Sin(angle), radius * MathF.Cos(angle));
    }
}

/// <summary>Every weapon a build knows, compiled once against a catalog.</summary>
public sealed class WeaponLibrary {
    readonly Dictionary<uint, WeaponTemplate> weapons;
    readonly string[] problems;

    WeaponLibrary(Dictionary<uint, WeaponTemplate> weapons, string[] problems) {
        this.weapons = weapons;
        this.problems = problems;
    }

    /// <summary>A library with nothing in it.</summary>
    public static WeaponLibrary Empty { get; } = Compile(DefinitionCatalog.Empty);

    /// <summary>How many weapons it holds.</summary>
    public int Count => weapons.Count;

    /// <summary>Every weapon, in address order.</summary>
    public IEnumerable<WeaponTemplate> All =>
        weapons.Values.OrderBy(weapon => weapon.Definition.Address, StringComparer.Ordinal);

    /// <summary>What a definition said that cannot be true at once.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Compiles every weapon in a catalog.</summary>
    /// <param name="catalog">The definitions.</param>
    /// <returns>The library.</returns>
    public static WeaponLibrary Compile(DefinitionCatalog catalog) {
        ArgumentNullException.ThrowIfNull(catalog);

        var tags = catalog.Tags;
        var problems = new List<string>();
        var compiled = new Dictionary<uint, WeaponTemplate>();

        foreach (var definition in catalog.OfType<WeaponDefinition>()) {
            if (definition.RoundsPerSecond <= 0f) {
                problems.Add($"'{definition.Address}' fires no rounds a second, so it can never be used.");
            }

            if (definition.Damage is null) {
                problems.Add($"'{definition.Address}' has no damage, so hitting with it does nothing.");
            }

            if (definition.Spread is { Maximum: > 0f } spread && spread.Maximum < spread.Base) {
                problems.Add(
                    $"'{definition.Address}' has a base spread of {spread.Base}° and a maximum of "
                    + $"{spread.Maximum}°, so it is at its widest before it has fired."
                );
            }

            if (definition.Reserve > 0 && definition.Reserve < definition.Magazine) {
                problems.Add(
                    $"'{definition.Address}' carries {definition.Reserve} spare rounds for a magazine of "
                    + $"{definition.Magazine}, so it can never be fully reloaded."
                );
            }

            compiled.Add(
                definition.Id.Value,
                new(
                    definition,
                    [.. definition.Tags.Select(tags.Resolve)],
                    definition.Damage is { School.Length: > 0 } damage ? tags.Resolve(damage.School) : GameplayTag.None,
                    [
                        .. (definition.Recoil?.Pattern ?? []).Select(step => new ShotDirection(step.Pitch, step.Yaw))
                    ]
                )
            );
        }

        return new(compiled, [.. problems]);
    }

    /// <summary>Finds a weapon.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It, or null.</returns>
    public WeaponTemplate? Find(DefId id) => weapons.GetValueOrDefault(id.Value);

    /// <summary>Finds a weapon by the address it was authored at.</summary>
    /// <param name="address">The address.</param>
    /// <returns>It, or null.</returns>
    public WeaponTemplate? Find(string address) => Find(DefId.From(address));

    /// <summary>Finds a weapon, and refuses to carry on without it.</summary>
    /// <param name="id">Its id.</param>
    /// <returns>It.</returns>
    /// <exception cref="DefinitionNotFoundException">This build has no such weapon.</exception>
    public WeaponTemplate Get(DefId id) =>
        Find(id) ?? throw new DefinitionNotFoundException($"{id} is not a weapon this build knows.");
}
