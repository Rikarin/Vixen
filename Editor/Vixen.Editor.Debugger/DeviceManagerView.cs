// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Debugger;

/// <summary>What a build can be deployed to, and which of them is answering.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What this panel is honest about is how much of it is a provider away.</b> The list,
///         the statuses, the selection and the hand-off to the remote inspector are all here; what is
///         not is anything that knows how to <i>find</i> an Android phone, which is <c>adb</c>, or a
///         console, which is a vendor SDK. Doc 20 puts device discovery behind those tools rather
///         than behind this window, and a panel listing the local machine and saying so is a truer
///         state than one that pretends to scan.
///     </para>
///     <para>
///         ⚠ <b>Deploy is not here, and is not merely unimplemented.</b> Copying a build to a device
///         needs a build, which is doc 20's E6 — <c>build.settings</c> and <c>build.run</c> are
///         declared-and-disabled for exactly that reason. What this window contributes to that
///         milestone is the list the deploy target is chosen from.
///     </para>
/// </remarks>
public sealed partial class DeviceManagerView : Control {
    Action<DeviceManager>? onChanged;
    DeviceManager? manager;

    /// <inheritdoc />
    protected override string TagName => "device-manager";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>Asks every provider what it can see.</summary>
    public Button Discover { get; private set; } = null!;

    /// <summary>Points the remote inspector at the selected device.</summary>
    public Button AttachButton { get; private set; } = null!;

    /// <summary>What went wrong, or what was found.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>The devices.</summary>
    public DataGrid Devices { get; private set; } = null!;

    /// <summary>Raised when somebody asks to attach to a device that is listening.</summary>
    /// <remarks>
    ///     An event rather than a call into <see cref="RemoteInspectorClient" />, because which
    ///     transport reaches an endpoint is the application's choice — a phone over USB is a
    ///     forwarded port and a machine on the LAN is a UDP socket, and this panel must not be the
    ///     place that decides.
    /// </remarks>
    public event Action<DeviceManagerView, DeviceEntry>? AttachRequested;

    /// <summary>Points the panel at a manager.</summary>
    /// <param name="devices">The manager.</param>
    /// <exception cref="ArgumentNullException"><paramref name="devices" /> is null.</exception>
    public void Show(DeviceManager devices) {
        ArgumentNullException.ThrowIfNull(devices);

        if (manager is not null && onChanged is not null) {
            manager.Changed -= onChanged;
        }

        manager = devices;
        onChanged ??= _ => Restate();

        devices.Changed += onChanged;
        Restate();
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("debugger-toolbar");

        Discover = Toolbar.Add<Button>();
        Discover.Size = ControlSize.Small;
        Discover.Label = "Discover";
        Discover.Clicked += _ => manager?.Discover();

        AttachButton = Toolbar.Add<Button>();
        AttachButton.Size = ControlSize.Small;
        AttachButton.Variant = ControlVariant.Primary;
        AttachButton.Label = "Attach";

        AttachButton.Clicked += _ => {
            if (manager?.Selected is { Endpoint: not null } device) {
                AttachRequested?.Invoke(this, device);
            }
        };

        Status = Part("debugger-status");

        Devices = Part<DataGrid>();
        Devices.AddColumn("Device", item => ((DeviceEntry)item).Name).Width = 180f;
        Devices.AddColumn("Kind", item => ((DeviceEntry)item).Kind.ToString());
        Devices.AddColumn("Platform", item => ((DeviceEntry)item).Platform).Width = 200f;
        Devices.AddColumn("Status", item => ((DeviceEntry)item).Status.ToString());
        Devices.AddColumn("Endpoint", item => ((DeviceEntry)item).Endpoint ?? "—").Width = 160f;

        Devices.SelectionChanged += grid => {
            if (manager is null) {
                return;
            }

            foreach (var index in grid.Selection) {
                if (index >= 0 && index < grid.Items.Count) {
                    manager.Selected = (DeviceEntry)grid.Items[index];
                    break;
                }
            }

            Restate();
        };

        Restate();
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        if (manager is not null && onChanged is not null) {
            manager.Changed -= onChanged;
        }

        base.OnRemoved();
    }

    void Restate() {
        if (manager is not { } devices) {
            Status.Text = "No device providers.";
            AttachButton.Disabled = true;

            return;
        }

        Devices.SetItems(devices.Devices.Cast<object>());

        // Only a device that says where to attach, because "Attach" against a device with no
        // endpoint is a button whose failure would be a silent no-op.
        AttachButton.Disabled = devices.Selected is not { Endpoint: not null };

        Status.Text = devices.Problems.Count > 0
            ? string.Join(" · ", devices.Problems)
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{devices.Devices.Count:N0} device(s) from {devices.Providers.Count:N0} provider(s)."
            );
    }
}
