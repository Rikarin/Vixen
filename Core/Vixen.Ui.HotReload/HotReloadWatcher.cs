// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.HotReload;

/// <summary>Watches a directory of <c>.vcss</c> files and reloads the ones it knows.</summary>
/// <remarks>
///     <para>
///         Only stylesheets. A changed <c>.vxml</c> is not something a watcher can act on — the file
///         has to become a different <c>Build</c> method first, and that is a compile — so the
///         markup channel is driven by <see cref="MetadataUpdate" /> instead. Watching for a file
///         this cannot use would put a spinner on an operation that never happens.
///     </para>
///     <para>
///         ⚠ <b>Editors write files more than once.</b> Save-to-temp-then-rename, a truncate
///         followed by a write, a tool that touches the timestamp afterwards — one save can raise
///         three events, and reloading three times means two rebuilds nobody asked for. Changes are
///         coalesced by path and applied on <see cref="Poll" />, which the frame loop calls; that
///         also puts the reload on the caller's thread, which matters because the element tree has
///         no lock and a <c>FileSystemWatcher</c> callback is on a pool thread.
///     </para>
/// </remarks>
public sealed class HotReloadWatcher : IDisposable {
    readonly HotReloadHost host;
    readonly FileSystemWatcher watcher;
    readonly Dictionary<string, int> sheets = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock gate = new();

    /// <summary>Watches a directory.</summary>
    /// <param name="host">Where reloads are applied.</param>
    /// <param name="directory">The directory to watch, recursively.</param>
    public HotReloadWatcher(HotReloadHost host, string directory) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(directory);

        this.host = host;
        watcher = new FileSystemWatcher(directory, "*.vcss") {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
        };

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += OnChanged;
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>Raised for every reload the watcher applies.</summary>
    public event Action<ReloadReport>? Reloaded;

    /// <summary>Loads a stylesheet and remembers where it came from.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The sheet's index.</returns>
    public int Load(string path) {
        ArgumentNullException.ThrowIfNull(path);

        var full = Path.GetFullPath(path);
        var sheet = host.Document.Load(File.ReadAllText(full));

        lock (gate) {
            sheets[full] = sheet;
        }

        return sheet;
    }

    /// <summary>Applies whatever has changed since the last call.</summary>
    /// <returns>One report per file reloaded.</returns>
    /// <remarks>
    ///     ⚠ <b>One report per <i>file</i>, not per event.</b> That is the coalescing: however many
    ///     times an editor touched a path between two calls, the path is in the set once and its text
    ///     is read once — and the text that is read is the one the file finally holds, rather than
    ///     whatever it held at each intermediate write.
    /// </remarks>
    public IReadOnlyList<ReloadReport> Poll() {
        (string Path, int Sheet)[] changed;

        // ⚠ Both dictionaries are read under the one lock, and the sheet index is taken here rather
        // than in the loop below. `Load` writes `sheets` and a pool thread writes `pending`; reading
        // either outside the gate is the same torn read whichever one happens to be racing.
        lock (gate) {
            if (pending.Count == 0) {
                return [];
            }

            changed = [
                .. pending.Where(sheets.ContainsKey).Select(path => (Path: path, Sheet: sheets[path]))
            ];

            pending.Clear();
        }

        var reports = new List<ReloadReport>(changed.Length);

        foreach (var (path, sheet) in changed) {
            // A file being written when we read it is the normal case, not an exception worth
            // taking the application down for — it will raise another event when it is finished.
            string css;
            try {
                css = File.ReadAllText(path);
            } catch (IOException) {
                lock (gate) {
                    pending.Add(path);
                }

                continue;
            }

            var report = host.ReloadStyles(sheet, css);
            reports.Add(report);
            Reloaded?.Invoke(report);
        }

        return reports;
    }

    /// <summary>Stops watching.</summary>
    public void Dispose() => watcher.Dispose();

    void OnChanged(object sender, FileSystemEventArgs args) => Notify(args.FullPath);

    /// <summary>Records that a path changed, from wherever the notice came.</summary>
    /// <remarks>
    ///     ⚠ <b>A seam, and it is what makes the coalescing testable at all.</b> The claim is that
    ///     three events for one save become one reload, and a test that produced three events by
    ///     writing a real file three times would be asserting on what the operating system chose to
    ///     deliver — which on one machine is three and on the next is one, so the test passes either
    ///     way and proves nothing. Driving the notice directly is the only way the assertion is about
    ///     this class.
    /// </remarks>
    internal void Notify(string path) {
        lock (gate) {
            pending.Add(Path.GetFullPath(path));
        }
    }
}
