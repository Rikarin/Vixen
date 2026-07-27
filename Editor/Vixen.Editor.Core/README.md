# Vixen.Editor.Core

The editor's model of a project: the asset database, the GUID index it is built on, and the
reverse-reference index that answers "what breaks if I delete this".

Spec: [docs/plan/08](../../docs/plan/08-asset-pipeline-and-addressables.md) § "GUID index and
conflict handling", [docs/plan/11](../../docs/plan/11-editor.md) § Asset database.

```csharp
var database = new AssetDatabase(new ProjectPaths("/path/to/MyGame"));

if (!database.TryLoad() || database.IsStale()) {
    foreach (var issue in database.Scan().Issues) { … }
    database.Save();
}
```

## The GUID is the identity; the path is a fact about today

Everything stored in a file is a GUID, so moving, renaming or reorganising folders changes nothing
anywhere. This is what makes that true: the one place that knows which GUID is currently at which
path.

**Rebuilt by reading only envelopes.** Doc 08 budgets a hundred-thousand-asset rebuild at under ten
seconds. That is achievable because `MetaScanner` reads three lines of each sidecar and stops, and
because the files are read in parallel — an I/O walk over thousands of small files leaves the cores
idle otherwise. Ten thousand assets are measured in the test suite; the assertion is loose on purpose,
because it exists to fail when someone makes the scan read whole documents again, not to police a
machine's disk.

**Insertion is sequential and in path order.** Duplicate resolution has to give the same answer on
two machines scanning one checkout, and directory enumeration order is not a promise any filesystem
makes.

## Nothing is silently tolerant

Every one of these is a thing that happens to real projects weekly, and silent tolerance is how
projects rot.

| Found | Done |
|---|---|
| A file with no sidecar | One is created with a fresh GUID |
| A sidecar with no file | Moved to `Library/OrphanMeta/`, **never deleted** |
| Two assets claiming one GUID | The one whose recorded `sourceHash` still matches its bytes keeps it; the other is re-GUIDed |
| A sidecar with no readable GUID | Reported and left alone |

The orphan is moved rather than deleted because a mis-ordered git operation is recoverable if the
GUID is still somewhere on disk, and is not if the editor helpfully tidied it away. The unreadable
sidecar is left alone because minting a new GUID would break every reference to that asset — an asset
the editor refuses to touch until a person looks at it is the better outcome.

When no hash settles a duplicate, the first path in order keeps the GUID. A rule, so that two
machines agree, rather than whichever file the filesystem handed over first.

`ScanOptions.ReadOnly` reports all of it and changes nothing, because a build server asking "is this
project clean?" wants the answer and not a working tree with edits in it.

## The reference index is a grep, and that is deliberate

`ReferenceIndex` scans text for `vx:` followed by thirty-two hex digits. That is sound *because of
how the reference format was chosen*: doc 08 picked a single prefixed scalar over Unity's three-key
flow mapping partly so that `rg 'vx:9e8a44c9'` finds every referrer. This is that grep, done once and
kept.

Parsing instead would mean binding every scene, material and prefab in the project — the expensive
half of opening one — to answer a question asked about one asset at a time. It would also fail on
exactly the files most likely to matter: an asset whose importer has been uninstalled cannot be
bound, but it can be read, and "which of my scenes still references this" is the question you ask
about *that* asset.

What a scan can do that a parse cannot is find a reference inside a comment. That is a false positive
in a report nobody is harmed by; the alternative is a missed reference in a "safe to delete" answer,
which corrupts a project.

Sidecars are scanned as part of the asset they belong to — a model importer's `materialMapping` holds
references, so a `.meta` is as much a referrer as a scene.

Licensed under Apache-2.0.
