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
    public IReadOnlyList<ReloadReport> Poll() {
        string[] changed;

        lock (gate) {
            if (pending.Count == 0) {
                return [];
            }

            changed = [.. pending];
            pending.Clear();
        }

        var reports = new List<ReloadReport>(changed.Length);

        foreach (var path in changed) {
            if (!sheets.TryGetValue(path, out var sheet)) {
                continue;
            }

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

    void OnChanged(object sender, FileSystemEventArgs args) {
        lock (gate) {
            pending.Add(Path.GetFullPath(args.FullPath));
        }
    }
}
