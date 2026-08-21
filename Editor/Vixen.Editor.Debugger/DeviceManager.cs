// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Editor.Debugger;

/// <summary>What kind of thing a build can be deployed to.</summary>
public enum DeviceKind : byte {
    /// <summary>The machine the editor is running on.</summary>
    Local,

    /// <summary>Another machine on the network.</summary>
    Network,

    /// <summary>A phone or tablet over a cable.</summary>
    Mobile,

    /// <summary>A console development kit.</summary>
    Console,

    /// <summary>A browser.</summary>
    Browser
}

/// <summary>What a device is doing.</summary>
public enum DeviceStatus : byte {
    /// <summary>Seen and idle.</summary>
    Available,

    /// <summary>A build is being copied to it.</summary>
    Deploying,

    /// <summary>Running a build.</summary>
    Running,

    /// <summary>Seen once and not answering now.</summary>
    Unreachable
}

/// <summary>One thing a build can be deployed to.</summary>
/// <param name="Id">A stable identifier — a serial number, a host name.</param>
/// <param name="Name">What to call it.</param>
/// <param name="Kind">What kind of thing it is.</param>
/// <param name="Platform">What it runs — "Android 14", "Windows 11", "Safari 18".</param>
/// <param name="Status">What it is doing.</param>
/// <param name="Endpoint">
///     Where the remote inspector would attach, or <see langword="null" /> when nothing is listening.
/// </param>
public sealed record DeviceEntry(
    string Id,
    string Name,
    DeviceKind Kind,
    string Platform,
    DeviceStatus Status = DeviceStatus.Available,
    string? Endpoint = null
);

/// <summary>Something that can say which devices are there.</summary>
/// <remarks>
///     ⚠ <b>An interface with no implementation in this assembly, and that is the honest state of
///     it.</b> Finding an Android device means <c>adb</c>, a console means the vendor's SDK, and a
///     machine on the LAN means whatever discovery <c>Vixen.Net</c> grows — none of which the editor
///     can do today, and all of which are one class each behind this. What exists now is the panel,
///     the shape of an entry, and a local provider, so that adding <c>adb</c> is adding a provider
///     rather than building a window.
/// </remarks>
public interface IDeviceProvider {
    /// <summary>What to call this source of devices in the panel.</summary>
    string Name { get; }

    /// <summary>Looks for devices.</summary>
    /// <returns>What it found.</returns>
    IEnumerable<DeviceEntry> Discover();
}

/// <summary>The machine the editor is running on, which is always there.</summary>
/// <remarks>
///     Worth a provider of its own rather than a special case in the panel: "deploy to this machine
///     and attach" is a real workflow — it is how somebody debugs the standalone play mode
///     <c>PlayModeController</c> already supports — and it is the one device that needs no discovery.
/// </remarks>
public sealed class LocalDeviceProvider : IDeviceProvider {
    /// <summary>Names the local provider.</summary>
    /// <param name="endpoint">Where a standalone build would listen, or <see langword="null" />.</param>
    public LocalDeviceProvider(string? endpoint = null) => Endpoint = endpoint;

    /// <summary>Where a standalone build would listen.</summary>
    public string? Endpoint { get; set; }

    /// <inheritdoc />
    public string Name => "This machine";

    /// <inheritdoc />
    public IEnumerable<DeviceEntry> Discover() {
        yield return new(
            "local",
            Environment.MachineName,
            DeviceKind.Local,
            Environment.OSVersion.VersionString,
            DeviceStatus.Available,
            Endpoint
        );
    }
}

/// <summary>Every device every provider can see, in one list.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Rediscovery replaces rather than merges, and a device that has gone becomes
///         <see cref="DeviceStatus.Unreachable" /> rather than disappearing.</b> A phone whose cable
///         is loose vanishing from the list looks like a panel that lost it; a phone marked
///         unreachable is a phone somebody goes and plugs back in.
///     </para>
///     <para>
///         ⚠ <b>A provider that throws is reported and does not stop the others.</b> Discovery runs
///         external tools — <c>adb</c>, a vendor SDK — and one that is not installed must not be the
///         reason the local machine stops being listed.
///     </para>
/// </remarks>
public sealed class DeviceManager {
    // ⚠ Three `CollectionSignal`s and a `Signal` rather than three `List`s and a field, and the four
    // properties below are unchanged because a `CollectionSignal<T>` already *is* an
    // `IReadOnlyList<T>`. Counting one or indexing it inside a binding subscribes, so
    // `DeviceManagerView.vxml` re-reads what it says when a provider is added, a discovery finishes
    // or the selection moves — rather than when somebody remembers to call `Restate`.
    //
    // ⚠ **Additive: `Changed` is still raised by every path that raised it before.** The panel's grid
    // is fed by `SetItems`, which is a method and not a property, so no markup attribute can bind it
    // — that half stays on the event, and every imperative caller (`DiagnosticsModule`,
    // `BuildSettingsTests`) is undisturbed. What the signals buy is the half that *is* expressible:
    // the status line and the two greyed buttons.
    //
    // ⚠ And deliberately not one `Signal<int>` that everything reads to force a re-evaluation. Each
    // of these is a value that genuinely changes, so a counter would be standing in for four
    // notifications that are all perfectly expressible — and it would defeat the equality check that
    // makes an unchanged selection cost nothing.
    readonly CollectionSignal<IDeviceProvider> providers = new();
    readonly CollectionSignal<DeviceEntry> devices = new();
    readonly CollectionSignal<string> problems = new();
    readonly Signal<DeviceEntry?> selected = new(null);

    /// <summary>Raised when the list changed.</summary>
    public event Action<DeviceManager>? Changed;

    /// <summary>Where the devices come from.</summary>
    public IReadOnlyList<IDeviceProvider> Providers => providers;

    /// <summary>What is out there, in the order the providers reported it.</summary>
    public IReadOnlyList<DeviceEntry> Devices => devices;

    /// <summary>What went wrong during the last discovery.</summary>
    public IReadOnlyList<string> Problems => problems;

    /// <summary>Which device is selected, or <see langword="null" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Signal-backed, and deliberately still not a thing that raises <see cref="Changed" />.</b>
    ///     The event means "the list changed" and a selection is not the list; the panel used to
    ///     rebuild its grid on every selection anyway, which is what cost the row its highlight. See
    ///     <c>DeviceManagerView.vxml</c>.
    /// </remarks>
    public DeviceEntry? Selected {
        get => selected.Value;
        set => selected.Value = value;
    }

    /// <summary>Adds a provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider" /> is null.</exception>
    public void Add(IDeviceProvider provider) {
        ArgumentNullException.ThrowIfNull(provider);

        providers.Add(provider);
        Changed?.Invoke(this);
    }

    /// <summary>Says what a device is doing now, without asking its provider again.</summary>
    /// <param name="id">Which device.</param>
    /// <param name="status">What it is doing.</param>
    /// <returns>Whether there was a device with that id.</returns>
    /// <remarks>
    ///     ⚠ <b><see cref="DeviceStatus.Deploying" /> and <see cref="DeviceStatus.Running" /> are not
    ///     things a provider can see.</b> Discovery answers "is it there"; whether a build is being
    ///     copied to it is a fact about what the editor is doing, and it is known here and nowhere
    ///     else. Without this the two states would be enum members no code could ever produce — which
    ///     is a promise the panel breaks the first time somebody deploys and the row says Available.
    /// </remarks>
    public bool Mark(string id, DeviceStatus status) {
        var index = IndexOf(id);

        if (index < 0) {
            return false;
        }

        devices[index] = devices[index] with { Status = status };

        // The selection is by identity elsewhere in this class and has to be here too: a row whose
        // record was replaced would otherwise deselect itself the moment a deploy started.
        if (Selected is { } selected && string.Equals(selected.Id, id, StringComparison.Ordinal)) {
            Selected = devices[index];
        }

        Changed?.Invoke(this);

        return true;
    }

    /// <summary>Asks every provider what it can see.</summary>
    public void Discover() {
        var previous = devices.Peek().ToArray();

        devices.Clear();
        problems.Clear();

        foreach (var provider in providers) {
            try {
                foreach (var found in provider.Discover()) {
                    devices.Add(found);
                }
            } catch (Exception exception) when (exception
                is IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or TimeoutException) {
                problems.Add($"{provider.Name}: {exception.Message}");
            }
        }

        foreach (var gone in previous) {
            if (IndexOf(gone.Id) < 0) {
                devices.Add(gone with { Status = DeviceStatus.Unreachable, Endpoint = null });
            }
        }

        // The selection follows the identity rather than the object, so a rediscovery that reports
        // the same phone with a new status does not silently deselect it.
        if (Selected is { } chosen) {
            var index = IndexOf(chosen.Id);
            Selected = index < 0 ? null : devices[index];
        }

        Changed?.Invoke(this);
    }

    /// <summary>Where a device with an id is, or -1.</summary>
    /// <remarks>
    ///     A loop rather than <c>List.FindIndex</c>, because the store is a <c>CollectionSignal</c>
    ///     now and its surface is <c>IReadOnlyList</c>. Reading it here subscribes nothing: these are
    ///     the write paths, and there is no ambient consumer on one.
    /// </remarks>
    int IndexOf(string id) {
        var items = devices.Peek();

        for (var index = 0; index < items.Length; index++) {
            if (string.Equals(items[index].Id, id, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }
}
