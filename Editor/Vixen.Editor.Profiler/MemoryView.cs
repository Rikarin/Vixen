// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Profiler;

/// <summary>Doc 13's four memory arenas, each with its rows.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Refreshed on a button rather than every frame, and that is not laziness.</b>
///         <c>GC.GetGCMemoryInfo</c> and a walk of the leak tracker's dictionary are both real work,
///         and a panel that did them sixty times a second would be a memory panel that allocates. It
///         also makes the numbers <i>readable</i>: a heap size that changes every frame is one nobody
///         can compare with the one they wrote down a minute ago.
///     </para>
///     <para>
///         ⚠ <b>Refreshing must not collect.</b> <c>GC.GetTotalMemory(true)</c> would give a tidier
///         number by running a blocking gen-2 collection first — which changes what is being measured
///         and stalls the editor. <see cref="MemorySnapshot" /> passes <c>false</c> and says so.
///     </para>
/// </remarks>
public sealed partial class MemoryView : Control {
    readonly List<UiElement> pool = [];

    /// <inheritdoc />
    protected override string TagName => "memory-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>What takes a fresh reading.</summary>
    public Button Refresh { get; private set; } = null!;

    /// <summary>When the reading on screen was taken.</summary>
    public UiElement Status { get; private set; } = null!;

    /// <summary>Where the rows go.</summary>
    public ScrollView Body { get; private set; } = null!;

    /// <summary>Where the arenas this assembly cannot see into come from.</summary>
    public MemoryProviders Providers { get; } = new();

    /// <summary>The reading on screen.</summary>
    public MemorySnapshot? Snapshot { get; private set; }

    /// <summary>Takes a fresh reading and shows it.</summary>
    public void Take() => Show(MemorySnapshot.Take(Providers));

    /// <summary>Shows a reading somebody else took.</summary>
    /// <param name="snapshot">The reading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot" /> is null.</exception>
    public void Show(MemorySnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);

        Snapshot = snapshot;

        var slot = 0;

        foreach (var arena in Enum.GetValues<MemoryArena>()) {
            var rows = snapshot.Of(arena).ToArray();

            if (rows.Length == 0) {
                continue;
            }

            Heading(ref slot, Title(arena), Bytes(snapshot.BytesOf(arena)));

            foreach (var row in rows) {
                Row(ref slot, row);
            }
        }

        for (var index = slot; index < pool.Count; index++) {
            pool[index].AddClass("parked");
        }

        Status.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Taken at {snapshot.Taken.ToLocalTime():HH\\:mm\\:ss}"
        );
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("memory-toolbar");

        Refresh = Toolbar.Add<Button>();
        Refresh.Size = ControlSize.Small;
        Refresh.Label = "Refresh";
        Refresh.Clicked += _ => Take();

        Status = Toolbar.Add<UiElement>("memory-status");

        Body = Part<ScrollView>();

        Take();
    }

    /// <summary>Formats a byte count the way a person reads one.</summary>
    /// <param name="bytes">How many.</param>
    /// <returns>The text.</returns>
    /// <remarks>
    ///     Binary units, because every allocator underneath reports in them and a panel that rounded
    ///     1 048 576 to "1.05 MB" would disagree with the number in every other tool the reader has
    ///     open.
    /// </remarks>
    public static string Bytes(long bytes) {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];

        double value = Math.Abs(bytes);
        var unit = 0;

        while (value >= 1024d && unit < units.Length - 1) {
            value /= 1024d;
            unit++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;

        return sign + value.ToString(unit == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    void Heading(ref int slot, string title, string total) {
        var row = Take(ref slot, "memory-heading");

        row.Children[0].Text = title;
        row.Children[1].Text = total;
        row.Children[2].Text = null;
    }

    void Row(ref int slot, MemoryRow entry) {
        var row = Take(ref slot, "memory-row");

        row.Children[0].Text = entry.Label;

        row.Children[1].Text = entry.IsCount
            ? entry.Bytes.ToString("N0", CultureInfo.InvariantCulture)
            : Bytes(entry.Bytes);

        row.Children[2].Text = entry.Detail;
    }

    /// <summary>
    ///     Takes the next pooled row, giving it the class this line wants and taking the other one
    ///     off.
    /// </summary>
    /// <remarks>
    ///     One pool for both kinds rather than two, because the rows interleave — a heading, its
    ///     rows, the next heading — and two pools would need the elements ordered by hand to keep
    ///     them in document order.
    /// </remarks>
    UiElement Take(ref int slot, string className) {
        while (pool.Count <= slot) {
            var built = Body.Content.Add<UiElement>("memory-line");

            built.Add<UiElement>("memory-label");
            built.Add<UiElement>("memory-value");
            built.Add<UiElement>("memory-detail");

            pool.Add(built);
        }

        var row = pool[slot++];

        row.RemoveClass("parked");
        row.RemoveClass(className == "memory-row" ? "memory-heading" : "memory-row");
        row.AddClass(className);

        return row;
    }

    static string Title(MemoryArena arena) =>
        arena switch {
            MemoryArena.Managed => "Managed heap",
            MemoryArena.Native => "Native allocations",
            MemoryArena.Gpu => "GPU memory",
            _ => "Asset residency"
        };
}
