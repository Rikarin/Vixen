// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors.Input;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>doc 11 § Input system's input debug panel: four lists over one <c>InputService</c>.</summary>
/// <remarks>
///     ⚠ <b>The first test is the important one, and it is the one a panel like this normally does
///     not have.</b> Every other assertion here is "press something, see it listed"; that one is
///     "publish nothing, and be told that nothing was published". A debug panel whose empty state and
///     whose dead state draw the same picture is an instrument that reports success on the day it did
///     not run — which for this panel is every day in the shipping editor, because the editor process
///     owns no <c>InputService</c>.
/// </remarks>
public class InputDebugViewTests {
    [Fact]
    public void WithNoServiceItSaysSoRatherThanDrawingFourEmptyLists() {
        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<InputDebugView>();

        view.Show(null);
        ui.Frame();

        Assert.Equal(InputDebugView.Unpublished, view.SourceLine);
        Assert.Empty(view.Devices.Children);
        Assert.Empty(view.Actuated.Children);
        Assert.Empty(view.Pointer.Children);
        Assert.Empty(view.Actions.Children);
    }

    /// <summary>A published service is named, and the device families are listed whatever is pressed.</summary>
    [Fact]
    public void APublishedServiceIsNamedAndItsDevicesAreListed() {
        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<InputDebugView>();

        view.Show(new InputService());
        ui.Frame();

        Assert.NotEqual(InputDebugView.Unpublished, view.SourceLine);
        Assert.Contains("InputService", view.SourceLine, StringComparison.Ordinal);

        // Keyboard, mouse, touch and the gamepad count — the three families a device set reports
        // present unconditionally, plus how many pads are plugged in.
        Assert.Equal(4, view.Devices.Children.Count);
    }

    /// <summary>What is held down is listed, by the same name a binding path would print.</summary>
    [Fact]
    public void AKeyThatIsDownIsListedAndOneThatIsUpIsNot() {
        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<InputDebugView>();
        var service = new InputService();

        view.Show(service);
        ui.Frame();

        Assert.Empty(view.Actuated.Children);

        service.Devices.SubmitKey(InputKey.A, true);
        view.Refresh();
        ui.Frame();

        var row = Assert.Single(view.Actuated.Children);

        Assert.Contains("A", Text(row), StringComparison.Ordinal);

        service.Devices.SubmitKey(InputKey.A, false);
        view.Refresh();
        ui.Frame();

        Assert.Empty(view.Actuated.Children);
    }

    /// <summary>
    ///     ⚠ The pointer is its own section because <c>InputDeviceSet.Actuated</c> leaves motion out
    ///     on purpose, so a panel that drew only what is actuated reports a mouse that never moves.
    /// </summary>
    [Fact]
    public void ThePointerIsShownEvenThoughActuatedRefusesToReportIt() {
        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<InputDebugView>();
        var service = new InputService();

        service.Devices.SubmitMouseMove(new(120f, 48f), new(4f, 0f));
        view.Show(service);
        ui.Frame();

        Assert.Empty(view.Actuated.Children);
        Assert.Equal(4, view.Pointer.Children.Count);
        Assert.Contains("120", Text(view.Pointer.Children[0]), StringComparison.Ordinal);
    }

    /// <summary>Every action of every loaded asset is listed with what it reads.</summary>
    [Fact]
    public void EveryActionOfALoadedAssetIsListed() {
        using var ui = UiTest.Create();
        var view = ui.Document.Root.Add<InputDebugView>();
        var service = new InputService();
        var actions = InputActions.Load(Asset, "Player");

        actions.Enable();
        service.Add(actions);
        service.Update(0d);

        view.Show(service);
        ui.Frame();

        var row = Assert.Single(view.Actions.Children);

        Assert.Contains("Player/Gameplay/Fire", Text(row), StringComparison.Ordinal);
    }

    /// <summary>An action asset with one map and one action bound to one key.</summary>
    const string Asset = """
        name: Player
        maps:
          - name: Gameplay
            actions:
              - name: Fire
                type: button
                bindings:
                  - path: <Keyboard>/space
        """;

    /// <summary>Everything a row's cells say, joined — the rows are two text elements deep.</summary>
    static string Text(UiElement row) => string.Join(' ', row.Children.Select(cell => cell.Text));
}
