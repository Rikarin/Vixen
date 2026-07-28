// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input.Tests.Assets;
using Xunit;

namespace Vixen.Input.Tests;

/// <summary>
///     The class <c>Vixen.Input.Generators</c> emitted from <c>Assets/SampleInput.vxinput</c>.
/// </summary>
/// <remarks>
///     Most of what the generator promises is checked by this file compiling at all: the class, its
///     namespace, one property per map and one per action, each of the declared type. What is left to
///     assert is that the members are wired to the right actions and that the embedded document is
///     the file on disk.
/// </remarks>
public class GeneratedAccessorTests {
    [Fact]
    public void CreatesAnAssetFromTheEmbeddedDocument() {
        var input = SampleInput.Create();

        Assert.Equal("SampleInput", input.Actions.Name);
        Assert.Equal(2, input.Actions.Maps.Count);
    }

    [Fact]
    public void NamesTheMapsAfterTheAsset() {
        var input = SampleInput.Create();

        Assert.Equal("Player", input.Player.Map.Name);
        Assert.Equal("Menu", input.Menu.Map.Name);
    }

    [Fact]
    public void NamesTheActionsAfterTheAsset() {
        var input = SampleInput.Create();

        Assert.Equal("Move", input.Player.Move.Name);
        Assert.Equal("Fire", input.Player.Fire.Name);
        Assert.Equal("Crouch", input.Player.Crouch.Name);
        Assert.Equal("Submit", input.Menu.Submit.Name);
    }

    [Fact]
    public void ReachesTheSameActionAsAStringLookupWould() {
        var input = SampleInput.Create();

        Assert.Same(input.Actions.FindAction("Player/Move"), input.Player.Move);
    }

    [Fact]
    public void EnablesAndDisablesOneMapAtATime() {
        var input = SampleInput.Create();

        input.Player.Enable();

        Assert.True(input.Player.Map.IsEnabled);
        Assert.False(input.Menu.Map.IsEnabled);

        input.Player.Disable();
        input.Enable();

        Assert.True(input.Player.Map.IsEnabled);
        Assert.True(input.Menu.Map.IsEnabled);
    }

    [Fact]
    public void ReadsTheRoadmapsWorkedExample() {
        // docs/plan/14 § Phase 8: `Input.Player.Move.ReadValue<Vector2>()`, typed and refactor-safe.
        var devices = new InputDeviceSet();
        var input = SampleInput.Create();
        input.Enable();

        devices.SubmitKey(InputKey.D, true);
        input.Actions.Update(devices, 0.016);

        Assert.Equal(new Vector2(1f, 0f), input.Player.Move.ReadValue<Vector2>());
    }

    [Fact]
    public void EmbedsTheDocumentVerbatim() {
        var reread = InputActionAssetReader.Read(SampleInput.Source, "SampleInput");

        Assert.Empty(reread.Diagnostics);
        Assert.Equal("SampleInput", reread.Asset!.Name);
    }

    [Fact]
    public void WrapsAnAssetThatWasLoadedSomewhereElse() {
        // What a game that ships its actions as content does rather than using the embedded copy.
        var input = new SampleInput(InputActions.Load(Sample.Text, "SampleInput"));

        Assert.Equal("Move", input.Player.Move.Name);
    }
}
