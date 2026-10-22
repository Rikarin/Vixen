// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.IO;
using Vixen.Core.IO.Watch;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.Assets;
using Vixen.Editor.Assets.Materials;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Testing;
using Vixen.Editor.Texturing;
using Vixen.Editor.Ui.Documents;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>What the editor writes into <c>Assets/</c> itself does not come back through the watcher as an outside change (#1497).</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#1488 suppressed one writer's sidecars: <c>ProjectSession.Rescan</c>'s.</b> Every other
///         path into <c>Assets/</c> — Create ▸, a file brought in, a rename, a move, a delete, a new folder,
///         an import's write-back of what it learned, a bake — wrote, and a quarter of a second later the
///         watcher reported it as somebody else's edit: a second scan, a source-control sweep and a
///         <c>ChangedOnDisk</c> to every module.
///     </para>
///     <para>
///         ⚠ <b>A count, not a wait.</b> The watcher here is <see cref="ReplayWatcher" />: the real
///         <see cref="FileChangeCoalescer" /> with no debounce, fed the disk's changes as the platform
///         would report them — every file and folder that appeared, went or was rewritten since the last
///         look. A real <c>FileSystemWatcher</c> could only prove "nothing came back" by sleeping past its
///         debounce and hoping. ⚠ <b>The instrument is checked before any claim:</b> a file written
///         from outside is one burst, so a replay that reported nothing would turn the first test red.
///     </para>
/// </remarks>
public sealed class EditorWriteEchoTests {
    /// <summary>The instrument: an outside write is one burst, and the sidecar its rescan minted is none.</summary>
    [Fact]
    public void A_file_written_from_outside_is_heard_once() {
        using var fixture = Fixture.Start();

        Assert.Equal(1, fixture.Echoes(() => File.WriteAllText(Path.Combine(fixture.Assets, "outside.txt"), "hello")).Bursts);

        // That burst's rescan minted `outside.txt.meta`, which is the editor's own write.
        Assert.Equal(0, fixture.Echoes(() => { }).Bursts);
    }

    /// <summary>Create ▸ writes the file and its sidecar, and neither is heard.</summary>
    [Fact]
    public void Creating_an_asset_echoes_nothing() {
        using var fixture = Fixture.Start();
        var kind = new NewAssetKind("test.create-thing", new("editor.command.test.create-thing", "Thing"), ".thing", "New Thing", "name: thing\n");

        fixture.AssertSilent(() => Assert.NotNull(fixture.Library.Create(kind)));
    }

    /// <summary>A file brought in from outside the project, then imported, echoes nothing — the copy, the sidecar or the write-back.</summary>
    [Fact]
    public void Importing_a_file_into_the_project_echoes_nothing() {
        using var fixture = Fixture.Start();
        var outside = Path.Combine(Path.GetTempPath(), "vixen-echo-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(outside);

        try {
            var source = Path.Combine(outside, "notes.txt");

            File.WriteAllText(source, "from elsewhere");

            fixture.AssertSilent(
                () => {
                    Assert.Equal(1, fixture.Library.Import([source], "Assets/Brought"));
                    fixture.AwaitImport();
                }
            );
        } finally {
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>Reimport All rewrites the sidecar of each asset it learns something about, and none is heard.</summary>
    [Fact]
    public void Reimporting_echoes_nothing() {
        using var fixture = Fixture.Start();

        // From outside, drained: the import below is what is measured.
        fixture.Echoes(() => File.WriteAllText(Path.Combine(fixture.Assets, "plain.txt"), "plain"));

        fixture.AssertSilent(
            () => {
                fixture.Library.ReimportAll();
                fixture.AwaitImport();
            }
        );
    }

    /// <summary>A rename, a move, a new folder and a delete each echo nothing.</summary>
    [Fact]
    public void Rearranging_assets_echoes_nothing() {
        using var fixture = Fixture.Start();

        fixture.Echoes(() => File.WriteAllText(Path.Combine(fixture.Assets, "crate.txt"), "crate"));

        var crate = fixture.Guid("Assets/crate.txt");

        fixture.AssertSilent(() => Assert.True(AssetOperations.CreateFolder(fixture.Project, "Assets", "Props").Ok));
        fixture.AssertSilent(() => Assert.True(fixture.Library.Rename(crate, "box").Ok));
        fixture.AssertSilent(() => Assert.Equal(1, fixture.Library.Move([crate], "Assets/Props")));
        fixture.AssertSilent(() => Assert.Equal(1, fixture.Library.Delete([crate])));
    }

    /// <summary>A block-out bake writes a mesh into a folder it makes, and nothing is heard.</summary>
    [Fact]
    public void Baking_a_mesh_echoes_nothing() {
        using var fixture = Fixture.Start();

        fixture.AssertSilent(() => new ProjectMeshBaker(fixture.Project).Bake("Wall", ".obj", "v 0 0 0\n"));
    }

    /// <summary>
    ///     ⚠ <b>The texturing starter shelf, written at activation, echoes nothing (#1497).</b> A module
    ///     activates while the watcher runs — a reload, here — and the five shipped smart materials it
    ///     writes onto a shelf that has lost them came back as somebody else's edit.
    /// </summary>
    [Fact]
    public void The_starter_shelf_echoes_nothing() {
        using var fixture = Fixture.Start();
        var shelf = Path.Combine(fixture.Assets, "SmartMaterials");

        Assert.True(Directory.Exists(shelf), "the shelf was not written when the editor started, so its rewrite below proves nothing");

        // From outside, drained: the reload's rewrite is what is measured.
        fixture.Echoes(() => Directory.Delete(shelf, recursive: true));
        fixture.Elapse();

        fixture.AssertSilent(() => Assert.Empty(fixture.Plugins.Reload(TexturingModule.ModuleId).Diagnostics));

        Assert.True(Directory.Exists(shelf), "the reload did not write the shelf back");
    }

    /// <summary>
    ///     ⚠ <b>The parallax ask's rewrite of the material a bake wrote echoes nothing (#1497)</b>, however
    ///     long after the bake's own announcement it lands.
    /// </summary>
    /// <remarks>
    ///     The bake announced the file, and a watcher suppresses a path for a window of time rather than
    ///     for one event — so the rewrite was covered only while it landed inside that window, and a bake
    ///     whose scan of a large project ran past it echoed. <see cref="Fixture.Elapse" /> is that scan,
    ///     without the wait.
    /// </remarks>
    [Fact]
    public void The_parallax_rewrite_echoes_nothing() {
        using var fixture = Fixture.Start();
        MaterialBakeSet? set = null;

        fixture.Echoes(
            () => set = new ProjectMaterialBaker(fixture.Project).Write(
                "Hull",
                MaterialBake.Encode(
                    new Dictionary<MaterialMapUsage, Bitmap> {
                        [MaterialMapUsage.BaseColor] = Flat(10),
                        [MaterialMapUsage.Height] = Flat(10)
                    }
                ),
                new() { Source = "Assets/hull.vxtexgraph" }
            )
        );

        fixture.Elapse();

        fixture.AssertSilent(
            () => {
                MaterialBakeParallax.Requested(set!, out var refused, fixture.Project.Assets);
                Assert.Null(refused);
            }
        );

        static Bitmap Flat(byte value) {
            var pixels = new byte[4 * 4 * 4];

            Array.Fill(pixels, value);
            return new(4, 4, pixels);
        }
    }

    /// <summary>
    ///     ⚠ <b>Analyse build in the addressable groups view echoes nothing (#1497).</b> It scans a
    ///     workspace of its own over the editor's <c>Assets/</c>, and that scan mints the sidecar of any
    ///     file the editor has not indexed yet.
    /// </summary>
    /// <remarks>
    ///     The file is the editor's own — announced, as a writer that had not yet rescanned would have —
    ///     so that the only thing left for the watcher to hear is the sidecar Analyse writes. The clock is
    ///     frozen so that the announcement cannot lapse while the analysis runs.
    /// </remarks>
    [Fact]
    public void Analysing_a_build_echoes_nothing() {
        using var fixture = Fixture.Start();
        var file = Path.Combine(fixture.Assets, "unindexed.txt");
        var analyse = fixture.Plugins.Services.Require<IWorkspace>().Views.OfType<AddressableGroupEditorView>().Single().Analyser;

        Assert.NotNull(analyse);

        fixture.Freeze();
        fixture.Project.Assets.WillWrite(file);
        File.WriteAllText(file, "written by the editor");

        fixture.AssertSilent(() => Assert.NotNull(analyse()));

        Assert.True(File.Exists(file + ".meta"), "Analyse's scan wrote no sidecar, so nothing it wrote was measured");
    }

    /// <summary>An editor with the replaying watcher in place of the platform's.</summary>
    sealed class Fixture : IDisposable {
        readonly EditorSession editor;
        readonly ProjectSession session;
        readonly ReplayWatcher disk;
        readonly List<FileChange> heard = [];
        int bursts;

        Fixture(EditorSession editor) {
            this.editor = editor;
            session = (ProjectSession)editor.Plugins.Services.Require<IProjectSession>();
            disk = new(editor.Project.Paths.Assets);
            session.WatchWith(disk);

            session.ChangedOnDisk += (changes, _) => {
                bursts++;
                heard.AddRange(changes);
            };
        }

        public static Fixture Start() => new(EditorSession.Start());

        public EditorProject Project => editor.Project;

        public string Assets => editor.Project.Paths.Assets;

        public IContentLibrary Library => editor.Editor.Library;

        public PluginHost Plugins => editor.Plugins;

        /// <summary>Lets more than the watcher's suppression window pass, as a slow scan or a later operation does.</summary>
        public void Elapse() => disk.Elapse();

        /// <summary>Stops the watcher's clock, so that a suppression cannot lapse while an operation runs.</summary>
        public void Freeze() => disk.Freeze();

        public AssetId Guid(string path) {
            Assert.True(Project.Assets.TryGetByPath(path, out var entry), path + " is not indexed");
            return entry.Guid;
        }

        /// <summary>Runs an operation and says what the watcher reported of it, after the rescans it caused.</summary>
        public (int Bursts, IReadOnlyList<FileChange> Heard) Echoes(Action operation) {
            Settle();
            bursts = 0;
            heard.Clear();

            operation();
            Settle();

            return (bursts, [.. heard]);
        }

        /// <summary>Runs an operation that writes into <c>Assets/</c> and asserts the watcher heard none of it.</summary>
        public void AssertSilent(Action operation) {
            var before = Snapshot.Of(Assets);
            var (count, changes) = Echoes(operation);

            // ⚠ The operation must have written something, or "nothing was heard" proves nothing.
            Assert.NotEqual(before, Snapshot.Of(Assets));
            Assert.True(count == 0, $"{count} echoed burst(s): {string.Join(", ", changes.Select(change => $"{change.Kind} {change.Path}"))}");
        }

        /// <summary>Waits for the import an operation queued, replaying the disk as the platform would report it meanwhile.</summary>
        /// <remarks>
        ///     ⚠ <b>Replayed during the wait, not only after it</b>, because a platform reports a write within
        ///     milliseconds while the suppression it must meet lasts two seconds: a replay held back until
        ///     the end of a slow import would be stricter than any watcher.
        /// </remarks>
        public void AwaitImport() {
            var tasks = editor.Editor.Content;

            // A hang check and not a budget: what is waited for is the pool reaching a task over a
            // project of three files.
            Assert.True(
                SpinWait.SpinUntil(
                    () => {
                        tasks.Pump();
                        disk.Replay();
                        return !tasks.IsBusy;
                    },
                    TimeSpan.FromSeconds(60)
                ),
                "the import never finished"
            );

            tasks.Pump();
        }

        /// <summary>Reports what changed on disk and follows it until nothing more does — each rescan can write.</summary>
        void Settle() {
            for (var pass = 0; pass < 8; pass++) {
                disk.Replay();

                if (!session.FollowDisk()) {
                    return;
                }
            }

            Assert.Fail("the watcher never went quiet: every rescan it caused wrote something it heard again");
        }

        public void Dispose() => editor.Dispose();
    }

    /// <summary>A watcher whose events are the disk's changes since it last looked, drained with no debounce.</summary>
    /// <remarks>
    ///     ⚠ <b>Its clock is the machine's plus whatever <see cref="Elapse" /> added</b>, so a test can put
    ///     an operation past the suppression window of the one before it without sleeping, and
    ///     <see cref="Freeze" /> stops it, so a test can keep an announcement alive without racing it.
    /// </remarks>
    sealed class ReplayWatcher(string assets) : IFileWatcher {
        readonly FileChangeCoalescer coalescer = new() { Debounce = TimeSpan.Zero };
        readonly Lock gate = new();
        Dictionary<string, Snapshot.Stamp> seen = Snapshot.Of(assets).Entries;
        long skew;
        long? frozen;

        long Now => frozen ?? Stopwatch.GetTimestamp() + skew;

        /// <summary>Moves the clock past the suppression window, so every suppression made so far has lapsed.</summary>
        public void Elapse() {
            lock (gate) {
                var window = (long)(coalescer.SuppressionWindow.TotalSeconds * Stopwatch.Frequency) + 1;

                if (frozen is { } at) {
                    frozen = at + window;
                } else {
                    skew += window;
                }
            }
        }

        /// <summary>Stops the clock where it is.</summary>
        public void Freeze() {
            lock (gate) {
                frozen = Now;
            }
        }

        public VirtualPath Root => VirtualPath.Root;

        public TimeSpan Debounce { get => TimeSpan.Zero; set { } }

        public bool HasOverflowed => false;

        public void Suppress(VirtualPath path) {
            lock (gate) {
                coalescer.Suppress(path, Now);
            }
        }

        public void Observed() {
            lock (gate) {
                coalescer.Observed();
            }
        }

        public int Drain(ICollection<FileChange> into) {
            lock (gate) {
                return coalescer.Drain(Now, into);
            }
        }

        public void ClearOverflow() { }

        public void Dispose() { }

        /// <summary>Records what appeared, went or was rewritten since the last call, as a platform would report it.</summary>
        /// <remarks>
        ///     A folder's own write time is not reported, because the real watcher's filters raise nothing for
        ///     it — measured on Windows: a file created in a subfolder is a Created and a Changed for the file
        ///     and no event for the folder.
        /// </remarks>
        public void Replay() {
            var now = Snapshot.Of(assets).Entries;

            lock (gate) {
                var timestamp = Now;

                foreach (var (path, stamp) in now) {
                    if (!seen.TryGetValue(path, out var was)) {
                        coalescer.Record(new(new VirtualPath(path), FileChangeKind.Created), timestamp);
                    } else if (!stamp.IsFolder && was != stamp) {
                        coalescer.Record(new(new VirtualPath(path), FileChangeKind.Changed), timestamp);
                    }
                }

                foreach (var path in seen.Keys.Where(path => !now.ContainsKey(path))) {
                    coalescer.Record(new(new VirtualPath(path), FileChangeKind.Deleted), timestamp);
                }
            }

            seen = now;
        }
    }

    /// <summary>Every file and folder under a directory, by virtual path, with what would show a rewrite.</summary>
    sealed record Snapshot(Dictionary<string, Snapshot.Stamp> Entries) {
        public static Snapshot Of(string root) {
            Dictionary<string, Stamp> entries = new(StringComparer.Ordinal);

            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)) {
                var key = "/" + Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, VirtualPath.Separator);
                var info = new FileInfo(path);

                entries[key] = Directory.Exists(path)
                    ? new(true, 0, 0)
                    : new(false, info.Length, info.LastWriteTimeUtc.Ticks);
            }

            return new(entries);
        }

        public bool Equals(Snapshot? other) =>
            other is not null && Entries.Count == other.Entries.Count
            && Entries.All(pair => other.Entries.TryGetValue(pair.Key, out var stamp) && stamp == pair.Value);

        public override int GetHashCode() => Entries.Count;

        public readonly record struct Stamp(bool IsFolder, long Length, long WrittenTicks);
    }
}
