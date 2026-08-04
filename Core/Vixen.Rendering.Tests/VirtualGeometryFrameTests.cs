// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Vixen.Core.Mathematics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Rendering.VirtualGeometry;
using Xunit;

namespace Tests;

/// <summary>
///     The host plumbing that turns phase 3's traversal into something a frame runs: the scene-wide
///     buffers, the instance records, the residency bitset and the request loop.
/// </summary>
/// <remarks>
///     <para>
///         <b>These are the claims a device test cannot make and a device test cannot check.</b>
///         Whether the traversal's <em>arithmetic</em> is right is <c>GpuClusterCullingTests</c>'
///         business, against a brute-force cut. What is at stake here is the plumbing around it — page
///         numbering across several meshes, which bindings get filled, and whether the bit that says
///         "this page is here" is set by the same authority that put the bytes there.
///     </para>
///     <para>
///         Every one of them was written because something was actually wrong. The binding roster in
///         particular: the object cull's descriptor set had seven unwritten bindings from the day the
///         traversal was added, because a permutation removes code and not declarations — and nothing
///         on the host could see it, because a set with a hole in it is a validation error on a device
///         and nothing at all in a compilation.
///     </para>
/// </remarks>
public sealed class VirtualGeometryFrameTests {
    /// <summary>
    ///     Every binding the culling shader declares is one the host knows how to fill.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The test that would have caught the seven holes.</b> Both variants of
    ///         <c>Culling.rvn</c> declare all eleven bindings — the <c>Clusters</c> permutation folds
    ///         away the code that reads seven of them and leaves the declarations behind — so the
    ///         descriptor-set layout has eleven entries whichever variant is bound, and a set bound with
    ///         any of them unwritten is undefined on a device.
    ///     </para>
    ///     <para>
    ///         Against the checked-in reflection rather than against a list written twice, so adding a
    ///         binding to the <c>.rvn</c> and forgetting the host is this failing rather than a device
    ///         losing itself the first time somebody profiles on Vulkan. The sampled texture is excluded
    ///         because it is not a storage buffer and is bound by a different call.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_host_fills_every_binding_the_shader_declares() {
        using var document = JsonDocument.Parse(Reflection("Pipeline", "Culling.reflect.json"));

        var declared = document.RootElement.GetProperty("Sets")
            .EnumerateArray()
            .SelectMany(set => set.GetProperty("Bindings").EnumerateArray())
            .Where(binding => binding.GetProperty("Type").GetString() == "StorageBuffer")
            .Select(binding => (uint)binding.GetProperty("Binding").GetInt32())
            .Order()
            .ToArray();

        Assert.Equal(declared, GpuCulling.SetBindings.Order().ToArray());
    }

    /// <summary>
    ///     Two meshes registered into one traversal do not share a page number.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The residency bitset is one bitset for the pool, so a page's identity has to be unique
    ///         across every mesh in it — which the offline format knows nothing about, because a
    ///         <c>MeshletPageSet</c> numbers its own pages from zero. <see cref="GpuClusterVisibility" />
    ///         is where the two numbering schemes meet.
    ///     </para>
    ///     <para>
    ///         What goes wrong without it: both meshes claim pages <c>0..n</c>, and every frame draws the
    ///         second mesh out of the first mesh's bytes for every page the first one happens to have
    ///         resident — geometry from the wrong mesh, at the right place, which looks like a corrupt
    ///         asset rather than like a bug here. This asserts both halves of the offset, because the
    ///         one that reaches the device is the record's and the one a caller can see is the
    ///         <see cref="ClusterMesh" />'s.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Two_meshes_do_not_share_a_page_number() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var (mesh, pages) = Scene();

        var first = visibility.Register(mesh, pages, 0);
        var second = visibility.Register(mesh, pages, 1);

        Assert.Equal(0, first.FirstPage);
        Assert.Equal(pages.Pages.Length, second.FirstPage);
        Assert.Equal(pages.Pages.Length * 2, visibility.PageCount);

        Assert.Equal(first.ClusterCount, second.FirstCluster);

        // And the records carry it, which is the half that actually reaches the device: no cluster of the
        // second mesh may name a page the first mesh owns.
        var records = visibility.Records;

        for (var i = second.FirstCluster; i < second.FirstCluster + second.ClusterCount; i++) {
            Assert.True(
                records[i].Page >= (uint)second.FirstPage,
                $"Cluster {i} of the second mesh names page {records[i].Page}, which belongs to the first."
            );
        }
    }

    /// <summary>
    ///     A cluster index stays mesh-local while a page index becomes global.
    /// </summary>
    /// <remarks>
    ///     Two numbering schemes in one record, and the asymmetry is deliberate rather than an
    ///     oversight: the shader reads <c>clusterRecords[instance.firstCluster + cluster]</c>, so the
    ///     roots and the child runs are indices of that relative kind and adding the base to them would
    ///     add it twice. The pages have no such base — the bitset is the pool's — so they get it once.
    ///     Both halves are load-bearing and neither is visible from the other's side, which is why this
    ///     asserts the shape rather than a picture.
    /// </remarks>
    [Fact]
    public void Roots_stay_mesh_local_and_pages_do_not() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var (mesh, pages) = Scene();

        visibility.Register(mesh, pages, 0);
        var second = visibility.Register(mesh, pages, 1);

        var flattened = GpuClusterCulling.Flatten(mesh, pages);

        // A root is an index into the second mesh's own clusters, so it is below its cluster count and
        // says nothing about where that mesh landed.
        foreach (var root in flattened.Roots) {
            Assert.True(root < second.ClusterCount, $"Root {root} is not an index into one mesh's clusters.");
        }

        Assert.NotEqual(0, second.FirstPage);
    }

    /// <summary>
    ///     A page requested by one frame's traversal is resident for a later one, and the bit says so.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The whole loop, end to end, with the device half stubbed: the traversal asks for pages,
    ///         <see cref="PageResidency" /> loads and places them, and the bitset the next traversal
    ///         reads is derived from what the pool actually holds. What this pins is the <em>ordering</em>
    ///         — that a bit is never set for a page whose bytes have not been placed.
    ///     </para>
    ///     <para>
    ///         Asserted through <see cref="GpuClusterCulling.Traverse" /> rather than through a
    ///         dispatch, because the null device runs no shader — and the mirror is the same arithmetic
    ///         by construction, which is what phase 3 established.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_requested_page_becomes_resident_and_the_cut_gets_finer() {
        using var device = new NullDevice();

        var (mesh, pages) = Scene();
        var source = new MemoryMeshletPageSource();
        source.Add(0, pages);

        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var residency = new PageResidency(pool, (long)pages.Pages.Length * pages.PageSize);

        residency.Pin(new(0, 0));

        var scene = GpuClusterCulling.Flatten(mesh, pages);
        var instance = Instance(scene);
        var view = View(threshold: 0.05f);

        var widths = new List<int>();

        // Ten frames of asking, servicing and asking again. The cut can only get finer, because the set
        // of resident pages only grows in a pool nothing is competing for — which is the arrangement
        // that makes "does the loop converge" a question with an answer.
        for (var frame = 0; frame < 10; frame++) {
            // Service first, which is the order a frame runs in: VirtualGeometryRenderFeature.Prepare
            // lands the previous frame's requests before it hands the traversal a residency bitset. A
            // test that traversed first would find nothing resident on its first frame — not even the
            // pinned root page, whose load Pin only queued.
            residency.Service(maxLoads: 64);

            // A load completes on whatever thread its continuation lands on, so how many have *arrived*
            // when Service next looks is a scheduling question. Waiting for the in-flight set to drain
            // makes the frame boundary mean what a real frame's does — the submitted work is done — and
            // is what keeps this a test of convergence rather than of the thread pool.
            // ⚠ Generous rather than tight, and it was not: at 250 ms this failed intermittently once
            // the suite grew enough to compete for the thread pool, reporting "the pinned root page
            // never landed" — a timeout wearing the costume of a convergence failure. The number is a
            // bound on scheduling, not on the loop, so the only wrong value for it is a small one.
            SpinWait.SpinUntil(() => residency.Loading == 0, TimeSpan.FromSeconds(30));

            var result = GpuClusterCulling.Traverse(scene, instance, view, page => residency.IsResident(new(0, (int)page)));

            widths.Add(result.Visible.Length);

            foreach (var page in result.Requests) {
                residency.Request(new(0, page));
            }
        }

        // The first frame draws nothing, and that is the loop's shape rather than a defect: Service
        // places what has *arrived* before it starts what has been asked for, so a page pinned before
        // the first frame lands on the second one. What matters is that it lands and that the cut only
        // ever gets finer.
        Assert.Equal(0, widths[0]);

        var first = widths.FindIndex(width => width > 0);
        Assert.True(first > 0, "The pinned root page never landed, so nothing was ever drawable.");

        for (var frame = first + 1; frame < widths.Count; frame++) {
            Assert.True(
                widths[frame] >= widths[frame - 1],
                $"The cut got coarser at frame {frame}: {widths[frame - 1]} then {widths[frame]}."
            );
        }

        Assert.True(widths[^1] > widths[first], $"The cut never refined: {widths[first]} then {widths[^1]}.");

        // And the bit is only ever set for a page the pool was given bytes for.
        Assert.True(residency.ResidentPages > 1, "Nothing beyond the pinned page ever arrived.");
        Assert.Equal(residency.ResidentPages, ResidentByPlacement(residency, pages.Pages.Length));
    }

    /// <summary>
    ///     An instance record that nothing moved is not uploaded again.
    /// </summary>
    /// <remarks>
    ///     Phase 0's claim, made for the traversal's instances rather than for the object cull's
    ///     objects: the two use the same <see cref="PersistentUploadBuffer{T}" /> and the same
    ///     comparison, and a frame that quietly went back to uploading every instance draws exactly the
    ///     same picture. So the only way to keep it honest is to assert the bytes.
    /// </remarks>
    [Fact]
    public void An_instance_that_did_not_move_is_not_uploaded_twice() {
        using var device = new NullDevice();
        using var visibility = new GpuClusterVisibility(device);

        var (mesh, pages) = Scene();
        visibility.Register(mesh, pages, 0);

        var records = new CullInstance[1024];

        for (var i = 0; i < records.Length; i++) {
            records[i] = new() { Position = new(i, 0f, 0f), Scale = 1f, Flags = GpuCulling.Alive };
        }

        Upload(visibility, records);
        var everything = visibility.InstanceBytesUploaded;

        // Settle every region of the ring first. Each frame in flight has its own device memory and its
        // own record of what is in it, so the first pass over the ring uploads everything however little
        // moved — the steady state is what the claim is about.
        for (var frame = 0; frame < device.FramesInFlight; frame++) {
            Upload(visibility, records);
        }

        Assert.Equal(0, visibility.InstanceBytesUploaded);

        records[500].Position = new(500f, 1f, 0f);

        for (var frame = 0; frame < device.FramesInFlight; frame++) {
            Upload(visibility, records);

            Assert.Equal(64, visibility.InstanceBytesUploaded);
            Assert.Equal(1, visibility.InstanceUploadRegions);
        }

        // And then it is settled again: the change reached every region and nothing re-sends it.
        Upload(visibility, records);
        Assert.Equal(0, visibility.InstanceBytesUploaded);
        Assert.True(everything > 64 * 100, "The first upload should be the whole scene.");
    }

    /// <summary>
    ///     The feature turns a view's screen-height scale into the error scale the traversal projects
    ///     with, and a view that opted out of screen-size work gets zero.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The one place a camera reaches the traversal, and the conversion has a factor of two in it
    ///         — <see cref="RenderView.ScreenHeightScale" /> is <c>1 / tan(fov / 2)</c> as a fraction of
    ///         the viewport's height, and the traversal wants pixels. A factor of two here is a
    ///         threshold that means twice what it says, which looks like a quality setting that does not
    ///         quite work rather than like a bug.
    ///     </para>
    ///     <para>
    ///         The zero case is the load-bearing one: a shadow cascade leaves
    ///         <c>ScreenHeightScale</c> at zero on purpose, and a scale of zero projects every error to
    ///         zero and so accepts every cluster at its root. Choosing a finer cut for a shadow than for
    ///         its caster is how a shadow stops matching the thing casting it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_view_that_does_no_screen_size_work_projects_no_error() {
        Assert.Equal(0f, GpuClusterCulling.ErrorScaleFor(0f, 1080));
        Assert.Equal(0f, GpuClusterCulling.ErrorScaleFor(2.4f, 0));

        // The two entry points are the same number: one takes a field of view and a height, the other
        // takes what a view already carries.
        const float fov = 1.0f;
        var scale = 1f / MathF.Tan(fov * 0.5f);

        Assert.Equal(
            GpuClusterCulling.ErrorScaleFor(fov, 1080f),
            GpuClusterCulling.ErrorScaleFor(scale, 1080),
            3
        );
    }

    /// <summary>
    ///     A feature with no traversal set refuses a registration rather than losing it.
    /// </summary>
    /// <remarks>
    ///     The failure being avoided is silence: a mesh registered into a feature with no
    ///     <see cref="VirtualGeometryRenderFeature.Visibility" /> would hand back an index that no
    ///     instance record could resolve, and every object drawing it would draw nothing for a reason
    ///     nothing reports.
    /// </remarks>
    [Fact]
    public void Registering_without_a_traversal_is_refused() {
        var feature = new VirtualGeometryRenderFeature();
        var (mesh, pages) = Scene();

        Assert.Throws<InvalidOperationException>(() => feature.Register(mesh, pages, 0));
    }

    static void Upload(GpuClusterVisibility visibility, CullInstance[] records) {
        visibility.Begin(records.Length);

        for (var i = 0; i < records.Length; i++) {
            visibility.Set(i, records[i]);
        }

        // Prepare would upload, and it needs an effect system this test has no business standing up —
        // so the buffer is driven directly, which is what the counters measure anyway.
        visibility.UploadInstances();
    }

    static int ResidentByPlacement(PageResidency residency, int pageCount) {
        var placed = 0;

        for (var page = 0; page < pageCount; page++) {
            if (residency.TryGetPlacement(new(0, page), out _)) {
                placed++;
            }
        }

        return placed;
    }

    static CullInstance Instance(ClusterScene scene) =>
        new() {
            FirstCluster = 0,
            ClusterCount = (uint)scene.Clusters.Length,
            FirstRoot = 0,
            RootCount = (uint)scene.Roots.Length,
            Position = Vector3.Zero,
            Scale = 1f,
            StagesLow = 1u,
            Flags = GpuCulling.Alive
        };

    static CullView View(float threshold) {
        var view = new CullView {
            Position = new(0f, 0f, 40f),
            StagesLow = 1u,
            ErrorScale = 1080f,
            ErrorThreshold = threshold
        };

        // Every plane accepts everything, so the comparison is about the cut and not about the frustum.
        for (var i = 0; i < BoundingFrustum.PlaneCount; i++) {
            view.Planes[i] = new(Vector3.UnitY, 1e6f);
        }

        return view;
    }

    /// <summary>A sphere small enough that a page holds a few clusters.</summary>
    static (MeshletMesh Mesh, MeshletPageSet Pages) Scene() {
        var input = Sphere(32, 64);
        var mesh = MeshletBuilder.Build(input);

        return (mesh, MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 4 * 1024 }));
    }

    /// <summary>A closed UV sphere: one vertex per pole, and the seam welded.</summary>
    static MeshletBuildInput Sphere(int rings, int segments) {
        var positions = new List<Vector3> { new(0f, 1f, 0f) };

        for (var ring = 1; ring < rings; ring++) {
            var phi = MathF.PI * ring / rings;

            for (var segment = 0; segment < segments; segment++) {
                var theta = 2f * MathF.PI * segment / segments;

                positions.Add(
                    new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta))
                );
            }
        }

        positions.Add(new(0f, -1f, 0f));

        var indices = new List<int>();
        var last = positions.Count - 1;

        int At(int ring, int segment) => 1 + ((ring - 1) * segments) + (segment % segments);

        for (var segment = 0; segment < segments; segment++) {
            indices.AddRange([0, At(1, segment + 1), At(1, segment)]);
            indices.AddRange([last, At(rings - 1, segment), At(rings - 1, segment + 1)]);
        }

        for (var ring = 1; ring < rings - 1; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var a = At(ring, segment);
                var b = At(ring, segment + 1);
                var c = At(ring + 1, segment);
                var d = At(ring + 1, segment + 1);

                indices.AddRange([a, b, c]);
                indices.AddRange([b, d, c]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    static string Reflection(string folder, string file) {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) {
            var candidate = Path.Combine(directory.FullName, "Raven", "Library", folder, file);

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/{folder}/{file} was not found above '{AppContext.BaseDirectory}'.");
    }
}
