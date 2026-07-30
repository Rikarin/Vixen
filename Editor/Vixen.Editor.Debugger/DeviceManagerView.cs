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
///         ⚠ <b>Deploy is a request rather than a call, for exactly <see cref="AttachRequested" />'s
///         reason.</b> It needs a build — which is doc 20's E6 and lives behind
///         <c>build.settings</c> — and what "deploy" means differs per kind of device: this machine
///         is a publish and a launch, a phone is <c>adb install</c>, a console is a vendor SDK. A
///         debugger assembly that picked one would be a panel that could only deploy to that one, so
///         it raises the intent and the application answers it.
///     </para>
///     <para>
///         ⚠ <b>Which devices can be deployed to is asked rather than assumed</b> — see
///         <see cref="CanDeploy" />. The button is greyed with the reason for the kinds nothing can
///         reach yet, which is the same rule the greyed menu lines follow and is why a device this
///         editor cannot install to is visibly that rather than a button that fails.
///     </para>
/// </remarks>
public sealed partial class DeviceManagerView : Control {
    Action<DeviceManager>? onChanged;
    DeviceManager? manager;
    Func<DeviceEntry, string?>? canDeploy;

    /// <inheritdoc />
    protected override string TagName => "device-manager";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>Asks every provider what it can see.</summary>
    public Button Discover { get; private set; } = null!;

    /// <summary>Builds and installs onto the selected device.</summary>
    public Button DeployButton { get; private set; } = null!;

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

    /// <summary>Raised when somebody asks for a build to be put on a device.</summary>
    /// <inheritdoc cref="AttachRequested" select="remarks" />
    public event Action<DeviceManagerView, DeviceEntry>? DeployRequested;

    /// <summary>Why a device cannot be deployed to, or <see langword="null" /> when it can.</summary>
    /// <remarks>
    ///     <para>
    ///         A sentence rather than a predicate, and unset means "nothing can be deployed to" rather
    ///         than "everything can". A panel opened by a test or by a host with no build settings
    ///         behind it must not offer a button that would silently do nothing, and the reason is
    ///         what the status line shows so that the greying is never a mystery.
    ///     </para>
    ///     <para>
    ///         Pulled rather than pushed, like every other source this assembly's panels read: the
    ///         factory that sets it runs again on every reopen, and an answer captured once would be
    ///         one from a build target the user has since changed.
    ///     </para>
    /// </remarks>
    public Func<DeviceEntry, string?>? CanDeploy {
        get => canDeploy;

        set {
            canDeploy = value;

            // The button's state is derived from this, and a panel whose factory set it after
            // `Show` would otherwise stay greyed until the next selection change.
            if (DeployButton is not null) {
                Restate();
            }
        }
    }

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

        DeployButton = Toolbar.Add<Button>();
        DeployButton.Size = ControlSize.Small;
        DeployButton.Variant = ControlVariant.Primary;
        DeployButton.Label = "Deploy";

        DeployButton.Clicked += _ => {
            if (manager?.Selected is { } device && Refusal(device) is null) {
                DeployRequested?.Invoke(this, device);
            }
        };

        AttachButton = Toolbar.Add<Button>();
        AttachButton.Size = ControlSize.Small;
        AttachButton.Variant = ControlVariant.Subtle;
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
            DeployButton.Disabled = true;

            return;
        }

        Devices.SetItems(devices.Devices.Cast<object>());

        // Only a device that says where to attach, because "Attach" against a device with no
        // endpoint is a button whose failure would be a silent no-op.
        AttachButton.Disabled = devices.Selected is not { Endpoint: not null };

        var refusal = devices.Selected is { } chosen ? Refusal(chosen) : null;
        DeployButton.Disabled = devices.Selected is null || refusal is not null;

        // ⚠ The refusal outranks the count, because it is the sentence about the thing somebody is
        // looking at. "One device from one provider" beside a greyed Deploy button is a panel that
        // answers a question nobody asked and withholds the one they did — and with nothing chosen
        // there is no such question, so the count is what a freshly-opened panel says.
        Status.Text = devices.Problems.Count > 0
            ? string.Join(" · ", devices.Problems)
            : refusal
            ?? string.Create(
                CultureInfo.InvariantCulture,
                $"{devices.Devices.Count:N0} device(s) from {devices.Providers.Count:N0} provider(s)."
            );
    }

    /// <summary>Why the selected device cannot be deployed to, or <see langword="null" />.</summary>
    string? Refusal(DeviceEntry device) =>
        device.Status is DeviceStatus.Deploying
            ? "A build is already on its way to this device."
            : CanDeploy is { } ask
                ? ask(device)
                : "Nothing here knows how to build for a device yet.";
}
