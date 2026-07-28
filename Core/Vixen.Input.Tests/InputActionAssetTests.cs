// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Input.Tests;

/// <summary>Reading, writing and re-reading a <c>.vxinput</c>.</summary>
public class InputActionAssetTests {
    [Fact]
    public void ReadsTheSampleAssetWithoutComplaint() {
        var result = InputActionAssetReader.Read(Sample.Text);

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Succeeded);

        var asset = result.Asset!;
        Assert.Equal("SampleInput", asset.Name);
        Assert.Equal(2, asset.Maps.Count);
        Assert.Equal(2, asset.ControlSchemes.Count);
    }

    [Fact]
    public void ReadsAnActionsTypeAndShape() {
        var move = Sample.Read().Maps[0].Actions[0];

        Assert.Equal("Move", move.Name);
        Assert.Equal(InputActionType.Value, move.Type);
        Assert.Equal(InputControlType.Vector2, move.ControlType);
        Assert.Equal(2, move.Bindings.Count);
    }

    [Fact]
    public void ReadsACompositeAndItsParts() {
        var composite = Sample.Read().Maps[0].Actions[0].Bindings[0];

        Assert.Equal(InputCompositeKind.Vector2, composite.Composite);
        Assert.Equal("WASD", composite.Name);
        Assert.Equal("Keyboard&Mouse", composite.Groups);
        Assert.Equal(4, composite.Parts.Count);
        Assert.Equal(["up", "down", "left", "right"], composite.Parts.Select(part => part.Part));
    }

    [Fact]
    public void ReadsProcessorsAsWritten() {
        var stick = Sample.Read().Maps[0].Actions[0].Bindings[1];

        Assert.Equal("<Gamepad>/leftStick", stick.Path);
        Assert.Equal("stickDeadzone(min=0.125,max=0.925)", stick.Processors);
    }

    [Fact]
    public void ReadsAControlSchemesDevices() {
        var scheme = Sample.Read().ControlSchemes[0];

        Assert.Equal("Keyboard&Mouse", scheme.Name);
        Assert.Equal([InputDeviceKind.Keyboard, InputDeviceKind.Mouse], scheme.Devices.Select(device => device.Device));
    }

    [Fact]
    public void DefaultsAnActionsShapeFromItsType() {
        var asset = Read(
            """
            name: Defaults
            maps:
              - name: Player
                actions:
                  - name: Jump
                  - name: Throttle
                    type: value
            """
        );

        Assert.Equal(InputControlType.Button, asset.Maps[0].Actions[0].ControlType);
        Assert.Equal(InputControlType.Axis, asset.Maps[0].Actions[1].ControlType);
    }

    /// <summary>Write, read, write — and the two documents are the same bytes.</summary>
    /// <remarks>
    ///     The strongest statement available without a hand-written deep comparison: the writer emits
    ///     every field of the model, so two identical documents mean two equivalent models. Record
    ///     equality would <em>not</em> say this — a record's generated <c>Equals</c> compares an
    ///     <see cref="IReadOnlyList{T}" /> member by reference, so two structurally identical assets
    ///     are never equal and a test asserting otherwise would only ever be testing itself.
    /// </remarks>
    [Fact]
    public void SurvivesARoundTripThroughTheWriter() {
        var once = InputActionAssetWriter.Write(Sample.Read());
        var result = InputActionAssetReader.Read(once, "SampleInput");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(once, InputActionAssetWriter.Write(result.Asset!));
    }

    [Fact]
    public void KeepsEveryActionThroughARoundTrip() {
        var original = Sample.Read();
        var reread = InputActionAssetReader.Read(InputActionAssetWriter.Write(original), original.Name).Asset!;

        Assert.Equal(
            original.Maps.SelectMany(map => map.Actions.Select(action => $"{map.Name}/{action.Name}")),
            reread.Maps.SelectMany(map => map.Actions.Select(action => $"{map.Name}/{action.Name}"))
        );
    }

    [Fact]
    public void WritesTheDialectsLineEndingAndTrailingNewline() {
        var written = InputActionAssetWriter.Write(Sample.Read());

        Assert.DoesNotContain('\r', written);
        Assert.EndsWith("\n", written, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotesAValueThatWouldNotReadBackAsItself() {
        var asset = new InputActionAssetData(
            "Odd",
            [new("Map: with a colon", [new("A", Bindings: [new("<Keyboard>/a", "- leading dash")])])]
        );

        var round = InputActionAssetReader.Read(InputActionAssetWriter.Write(asset));

        Assert.Empty(round.Diagnostics);
        Assert.Equal("Map: with a colon", round.Asset!.Maps[0].Name);
        Assert.Equal("- leading dash", round.Asset.Maps[0].Actions[0].Bindings[0].Name);
    }

    [Fact]
    public void ReportsAMissingActionName() {
        var result = InputActionAssetReader.Read(
            """
            name: Broken
            maps:
              - name: Player
                actions:
                  - type: button
            """
        );

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("no 'name'", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsADuplicateActionName() {
        var result = InputActionAssetReader.Read(
            """
            name: Broken
            maps:
              - name: Player
                actions:
                  - name: Fire
                  - name: fire
            """
        );

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("already has an action", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsACompositeWithAMissingPart() {
        var result = InputActionAssetReader.Read(
            """
            name: Broken
            maps:
              - name: Player
                actions:
                  - name: Move
                    controlType: vector2
                    bindings:
                      - composite: vector2
                        parts:
                          - part: up
                            path: <Keyboard>/w
            """
        );

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("'down' part", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsACompositeBoundToAnActionOfTheWrongShape() {
        var result = InputActionAssetReader.Read(
            """
            name: Broken
            maps:
              - name: Player
                actions:
                  - name: Move
                    bindings:
                      - composite: vector2
                        parts:
                          - part: up
                            path: <Keyboard>/w
                          - part: down
                            path: <Keyboard>/s
                          - part: left
                            path: <Keyboard>/a
                          - part: right
                            path: <Keyboard>/d
            """
        );

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("controlType", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsAnAssetFromANewerEditor() {
        var result = InputActionAssetReader.Read(
            """
            name: Future
            version: 99
            maps:
              - name: Player
                actions:
                  - name: Fire
            """
        );

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("newer editor", StringComparison.Ordinal));

        // And it still loaded. Refusing would strand a player on the version they first ran.
        Assert.NotNull(result.Asset);
    }

    [Fact]
    public void KeepsGoingAfterOneBadEntry() {
        var result = InputActionAssetReader.Read(
            """
            name: Partly
            maps:
              - name: Player
                actions:
                  - name: Fire
                  - type: button
                  - name: Jump
            """
        );

        Assert.Single(result.Diagnostics);
        Assert.Equal(["Fire", "Jump"], result.Asset!.Maps[0].Actions.Select(action => action.Name));
    }

    [Fact]
    public void ReportsADocumentThatIsNotYamlAtAllWithoutThrowing() {
        var result = InputActionAssetReader.Read("\tnope\n");

        Assert.Null(result.Asset);
        Assert.Single(result.Diagnostics);
    }

    static InputActionAssetData Read(string text) {
        var result = InputActionAssetReader.Read(text);
        Assert.NotNull(result.Asset);
        return result.Asset!;
    }
}
