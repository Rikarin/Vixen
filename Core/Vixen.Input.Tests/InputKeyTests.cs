// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Input.Tests;

/// <summary>
///     The check that makes <see cref="InputKey" />'s duplication of <c>Vixen.Platform.Key</c> safe.
/// </summary>
/// <remarks>
///     <para>
///         <c>Vixen.Input</c> is a <c>Core/</c> assembly and <c>Vixen.Platform</c> is above it, so the
///         reference the obvious design wants is one <c>CheckArchitecture</c> refuses — and refuses
///         for a reason, because <c>Vixen.Ui</c> consumes this assembly and must stay usable with no
///         platform backend at all. So the key table exists twice, and the bridge between the two
///         casts rather than translating.
///     </para>
///     <para>
///         A test project may reference anything, which is what lets this file hold the two tables
///         against each other. A value that drifted would fail here rather than binding a player's
///         controls to the wrong key on one backend.
///     </para>
/// </remarks>
public class InputKeyTests {
    [Fact]
    public void HasExactlyThePlatformsKeys() {
        var ours = Enum.GetNames<InputKey>().Order().ToArray();
        var platform = Enum.GetNames<Platform.Key>().Order().ToArray();

        Assert.Equal(platform, ours);
    }

    [Fact]
    public void AgreesWithThePlatformOnEveryValue() {
        foreach (var name in Enum.GetNames<InputKey>()) {
            var ours = Enum.Parse<InputKey>(name);
            var platform = Enum.Parse<Platform.Key>(name);

            Assert.Equal((ushort)platform, (ushort)ours);
        }
    }

    [Fact]
    public void CastsFromThePlatformsKeyWithoutATranslationTable() {
        // What the host's twenty-line bridge does, and the whole point of the values agreeing.
        Assert.Equal(InputKey.W, (InputKey)Platform.Key.W);
        Assert.Equal(InputKey.Back, (InputKey)Platform.Key.Back);
    }

    [Fact]
    public void AgreesWithThePlatformOnMouseButtons() {
        Assert.Equal((ushort)MouseControl.Primary, (ushort)Platform.MouseButton.Primary);
        Assert.Equal((ushort)MouseControl.Secondary, (ushort)Platform.MouseButton.Secondary);
        Assert.Equal((ushort)MouseControl.Middle, (ushort)Platform.MouseButton.Middle);
        Assert.Equal((ushort)MouseControl.Extra1, (ushort)Platform.MouseButton.Extra1);
        Assert.Equal((ushort)MouseControl.Extra2, (ushort)Platform.MouseButton.Extra2);
    }

    [Fact]
    public void AgreesWithThePlatformOnGamepadButtons() {
        foreach (var name in Enum.GetNames<Platform.GamepadButton>()) {
            Assert.True(Enum.TryParse<GamepadControl>(name, out var ours), $"GamepadControl has no {name}");
            Assert.Equal((ushort)Enum.Parse<Platform.GamepadButton>(name), (ushort)ours);
        }
    }

    [Fact]
    public void AgreesWithThePlatformOnGamepadAxesByName() {
        // The axes are renumbered rather than copied — they share one enum with the buttons here and
        // sit in their own there — so what has to hold is that every axis the platform reports has a
        // control to arrive at, not that the numbers match.
        foreach (var name in Enum.GetNames<Platform.GamepadAxis>()) {
            if (name == nameof(Platform.GamepadAxis.None)) {
                continue;
            }

            Assert.True(Enum.TryParse<GamepadControl>(name, out var ours), $"GamepadControl has no {name}");
            Assert.True(InputControl.Gamepad(ours).IsAnalogue, $"{name} should read as an axis");
        }
    }
}
