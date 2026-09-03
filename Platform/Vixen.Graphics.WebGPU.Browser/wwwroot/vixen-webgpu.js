// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser half of Vixen.Graphics.WebGPU.Browser.
//
// Two jobs and nothing else. Objects are kept in an array and referred to by index, because
// a WebGPU object cannot cross to WebAssembly and an integer can. And descriptors arrive as
// packed bytes, read here with a DataView, because a render pipeline has around sixty fields
// nested in run-time-length arrays and the alternatives are a call per field or JSON.
//
// EVERY LAYOUT BELOW IS WRITTEN TWICE: here, and by WebGpuPacker's callers in C#. That is
// deliberate and it is the cost of the design — a mismatch is silent, so each reader repeats
// the layout the writer documented, in the same order and the same words.
//
// The enum tables map webgpu.h's numbering, which is what the C# side speaks, onto WebGPU's
// JavaScript strings. Those numbers are asserted against Silk.NET's by the test suite, so a
// binding upgrade that renumbered anything is a red build rather than a wrong texture format.

const objects = [null];
const free = [];

let device = null;
let queue = null;
let adapter = null;
let context = null;
let canvasFormat = "";
let lastError = "";

/** Stores an object and returns its handle. Handle 0 is always nothing. */
function store(value) {
    if (!value) {
        return 0;
    }

    if (free.length > 0) {
        const slot = free.pop();
        objects[slot] = value;
        return slot;
    }

    objects.push(value);
    return objects.length - 1;
}

function get(handle) {
    return handle === 0 ? undefined : objects[handle];
}

// ── The enum tables ─────────────────────────────────────────────────────────────────────────
//
// Sparse arrays indexed by webgpu.h's value. A gap is a format the C# side never emits.

const TEXTURE_FORMAT = [];
TEXTURE_FORMAT[1] = "r8unorm";
TEXTURE_FORMAT[2] = "r8snorm";
TEXTURE_FORMAT[3] = "r8uint";
TEXTURE_FORMAT[4] = "r8sint";
TEXTURE_FORMAT[5] = "r16uint";
TEXTURE_FORMAT[6] = "r16sint";
TEXTURE_FORMAT[7] = "r16float";
TEXTURE_FORMAT[8] = "rg8unorm";
TEXTURE_FORMAT[9] = "rg8snorm";
TEXTURE_FORMAT[10] = "rg8uint";
TEXTURE_FORMAT[11] = "rg8sint";
TEXTURE_FORMAT[12] = "r32float";
TEXTURE_FORMAT[13] = "r32uint";
TEXTURE_FORMAT[14] = "r32sint";
TEXTURE_FORMAT[15] = "rg16uint";
TEXTURE_FORMAT[16] = "rg16sint";
TEXTURE_FORMAT[17] = "rg16float";
TEXTURE_FORMAT[18] = "rgba8unorm";
TEXTURE_FORMAT[19] = "rgba8unorm-srgb";
TEXTURE_FORMAT[20] = "rgba8snorm";
TEXTURE_FORMAT[21] = "rgba8uint";
TEXTURE_FORMAT[22] = "rgba8sint";
TEXTURE_FORMAT[23] = "bgra8unorm";
TEXTURE_FORMAT[24] = "bgra8unorm-srgb";
TEXTURE_FORMAT[25] = "rgb10a2uint";
TEXTURE_FORMAT[26] = "rgb10a2unorm";
TEXTURE_FORMAT[27] = "rg11b10ufloat";
TEXTURE_FORMAT[28] = "rgb9e5ufloat";
TEXTURE_FORMAT[29] = "rg32float";
TEXTURE_FORMAT[30] = "rg32uint";
TEXTURE_FORMAT[31] = "rg32sint";
TEXTURE_FORMAT[32] = "rgba16uint";
TEXTURE_FORMAT[33] = "rgba16sint";
TEXTURE_FORMAT[34] = "rgba16float";
TEXTURE_FORMAT[35] = "rgba32float";
TEXTURE_FORMAT[36] = "rgba32uint";
TEXTURE_FORMAT[37] = "rgba32sint";
TEXTURE_FORMAT[38] = "stencil8";
TEXTURE_FORMAT[39] = "depth16unorm";
TEXTURE_FORMAT[40] = "depth24plus";
TEXTURE_FORMAT[41] = "depth24plus-stencil8";
TEXTURE_FORMAT[42] = "depth32float";
TEXTURE_FORMAT[43] = "depth32float-stencil8";
TEXTURE_FORMAT[44] = "bc1-rgba-unorm";
TEXTURE_FORMAT[45] = "bc1-rgba-unorm-srgb";
TEXTURE_FORMAT[48] = "bc3-rgba-unorm";
TEXTURE_FORMAT[49] = "bc3-rgba-unorm-srgb";
TEXTURE_FORMAT[50] = "bc4-r-unorm";
TEXTURE_FORMAT[52] = "bc5-rg-unorm";
TEXTURE_FORMAT[54] = "bc6h-rgb-ufloat";
TEXTURE_FORMAT[56] = "bc7-rgba-unorm";
TEXTURE_FORMAT[57] = "bc7-rgba-unorm-srgb";
TEXTURE_FORMAT[60] = "etc2-rgb8a1unorm";
TEXTURE_FORMAT[62] = "etc2-rgba8unorm";
TEXTURE_FORMAT[68] = "astc-4x4-unorm";
TEXTURE_FORMAT[69] = "astc-4x4-unorm-srgb";
TEXTURE_FORMAT[82] = "astc-8x8-unorm";
TEXTURE_FORMAT[83] = "astc-8x8-unorm-srgb";

const TEXTURE_FORMAT_VALUE = new Map();

for (let value = 0; value < TEXTURE_FORMAT.length; value++) {
    if (TEXTURE_FORMAT[value]) {
        TEXTURE_FORMAT_VALUE.set(TEXTURE_FORMAT[value], value);
    }
}

const TEXTURE_DIMENSION = ["1d", "2d", "3d"];
const TEXTURE_VIEW_DIMENSION = [undefined, "1d", "2d", "2d-array", "cube", "cube-array", "3d"];
const TEXTURE_ASPECT = ["all", "stencil-only", "depth-only"];
const ADDRESS_MODE = ["repeat", "mirror-repeat", "clamp-to-edge"];
const FILTER_MODE = ["nearest", "linear"];
const COMPARE_FUNCTION = [
    undefined, "never", "less", "less-equal", "greater", "greater-equal", "equal", "not-equal", "always"
];
const BUFFER_BINDING_TYPE = [undefined, "uniform", "storage", "read-only-storage"];
const SAMPLER_BINDING_TYPE = [undefined, "filtering", "non-filtering", "comparison"];
const TEXTURE_SAMPLE_TYPE = [undefined, "float", "unfilterable-float", "depth", "sint", "uint"];
const STORAGE_TEXTURE_ACCESS = [undefined, "write-only", "read-only", "read-write"];
const PRIMITIVE_TOPOLOGY = ["point-list", "line-list", "line-strip", "triangle-list", "triangle-strip"];
const INDEX_FORMAT = [undefined, "uint16", "uint32"];
const FRONT_FACE = ["ccw", "cw"];
const CULL_MODE = ["none", "front", "back"];
const VERTEX_FORMAT = [];
VERTEX_FORMAT[2] = "uint8x4";
VERTEX_FORMAT[6] = "unorm8x4";
VERTEX_FORMAT[8] = "snorm8x4";
VERTEX_FORMAT[13] = "unorm16x2";
VERTEX_FORMAT[16] = "snorm16x4";
VERTEX_FORMAT[17] = "float16x2";
VERTEX_FORMAT[18] = "float16x4";
VERTEX_FORMAT[19] = "float32";
VERTEX_FORMAT[20] = "float32x2";
VERTEX_FORMAT[21] = "float32x3";
VERTEX_FORMAT[22] = "float32x4";
VERTEX_FORMAT[23] = "uint32";
const VERTEX_STEP_MODE = ["vertex", "instance", "vertex"];
const BLEND_FACTOR = [
    "zero", "one", "src", "one-minus-src", "src-alpha", "one-minus-src-alpha", "dst", "one-minus-dst",
    "dst-alpha", "one-minus-dst-alpha", "src-alpha-saturated", "constant", "one-minus-constant"
];
const BLEND_OPERATION = ["add", "subtract", "reverse-subtract", "min", "max"];
const STENCIL_OPERATION = [
    "keep", "zero", "replace", "invert", "increment-clamp", "decrement-clamp", "increment-wrap",
    "decrement-wrap"
];
const LOAD_OP = [undefined, "clear", "load"];
const STORE_OP = [undefined, "store", "discard"];
const ALPHA_MODE = ["opaque", "opaque", "premultiplied", "premultiplied", "opaque"];

// ── The reader ──────────────────────────────────────────────────────────────────────────────

// ── ⚠ Every `view`, `descriptor`, `entries` and `data` below is a .NET MemoryView ────────────
//
// Not a typed array. What the marshaller passes for a `[JSMarshalAs<JSType.MemoryView>]
// Span<byte>` has FIVE members and no more:
//
//     set(source, offset)   source must be a Uint8Array for a byte span — the constructor is
//                           compared by identity, so a Uint8ClampedArray throws rather than
//                           converting: `Assert failed: Expected function Uint8Array`.
//     copyTo(target, from)  the other direction, same rule.
//     slice(start, end)     a REAL typed array holding a copy, taken out of WebAssembly memory.
//     length / byteLength
//
// There is no indexer, no `fill`, no `.buffer` and no `.byteOffset`.
//
// ⚠ Four sites in this file got that wrong and every one of them was fatal on the first frame of
// every browser build: this Reader (which nine entry points construct, so the whole descriptor
// half of the backend), `readLimits`, `writeBuffer`, and `setBindGroup`'s dynamic-offset branch.
// Three threw `TypeError: First argument to DataView constructor must be an ArrayBuffer`.
//
// ⚠ They survived every gate this repository has — the compiler sees a declaration, CompileWeb
// compiles it, BrowserModuleUrlTests knows the module URL, PublishWeb sees a file land — because
// none of them marshals a call. They were recorded as unfixable without a WebGPU adapter, and
// that was wrong: not one of them reaches a GPU object before it fails, so a faithful MemoryView
// under Node finds all four. See Platform/Vixen.Platform.Web.Tests/js/vixen-webgpu.test.mjs.

/**
 * Walks a packed descriptor. Little-endian at every read, stated rather than assumed:
 * WebAssembly is little-endian by specification and DataView's default is not.
 */
class Reader {
    constructor(view) {
        // ⚠ slice() first. `view` is a MemoryView, so `view.buffer` is `undefined` and
        // `new DataView(undefined, …)` throws outright — which it did, on every packed descriptor
        // this backend has ever sent. slice() is the one member that yields an ArrayBuffer to
        // read through, and the copy it costs is one a descriptor of a few dozen bytes would not
        // notice; the view is only valid for the duration of the call anyway.
        const bytes = view.slice(0, view.length);

        this.view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
        this.at = 0;
    }

    int() {
        const value = this.view.getInt32(this.at, true);
        this.at += 4;
        return value;
    }

    bool() {
        return this.int() !== 0;
    }

    float() {
        const value = this.view.getFloat32(this.at, true);
        this.at += 4;
        return value;
    }

    double() {
        const value = this.view.getFloat64(this.at, true);
        this.at += 8;
        return value;
    }

    object() {
        return get(this.int());
    }
}

// ── Bring-up ────────────────────────────────────────────────────────────────────────────────

export function isSupported() {
    return typeof navigator !== "undefined" && !!navigator.gpu;
}

/**
 * Asks for an adapter and a device, and configures the canvas when there is one.
 * Returns an empty string on success, or why it failed — the C# side turns a non-empty
 * answer into a PlatformNotSupportedException with the text in it.
 */
export async function initialise(canvasSelector, powerPreference) {
    try {
        const options = powerPreference ? { powerPreference } : {};
        adapter = await navigator.gpu.requestAdapter(options);

        if (!adapter) {
            return "navigator.gpu.requestAdapter returned nothing. WebGPU is present but no adapter "
                + "is usable — on Linux this is usually a browser started without --enable-features"
                + "=Vulkan.";
        }

        // Everything the adapter offers, rather than the guaranteed floor. A device created with no
        // requiredLimits reports the specification's minimums whatever the hardware manages, and
        // every one of those numbers reaches a renderer through GraphicsDeviceFeatures.
        const requiredLimits = {};

        for (const key in adapter.limits) {
            const value = adapter.limits[key];

            if (typeof value === "number") {
                requiredLimits[key] = value;
            }
        }

        device = await adapter.requestDevice({
            requiredFeatures: [...adapter.features],
            requiredLimits
        });

        if (!device) {
            return "adapter.requestDevice returned nothing.";
        }

        queue = device.queue;

        // WebGPU has no return codes: almost everything is void, and what went wrong arrives here.
        // Without this a backend that is silently doing nothing looks exactly like one that works.
        device.addEventListener("uncapturederror", event => {
            lastError = String(event.error);
            console.error("[vixen-webgpu]", event.error);
        });

        device.lost.then(info => {
            lastError = `device lost: ${info.reason} ${info.message}`;
            console.error("[vixen-webgpu]", lastError);
        });

        canvasFormat = navigator.gpu.getPreferredCanvasFormat();

        if (canvasSelector) {
            const canvas = document.querySelector(canvasSelector);

            if (!canvas) {
                return `No element matches '${canvasSelector}'.`;
            }

            context = canvas.getContext("webgpu");

            if (!context) {
                return `'${canvasSelector}' would not give a webgpu context.`;
            }
        }

        return "";
    } catch (error) {
        return String(error);
    }
}

/** The device's limits, as fourteen doubles in WebGpuLimits's declaration order. */
export function readLimits(destination) {
    const limits = device ? device.limits : {};
    const values = [
        limits.maxTextureDimension2D,
        limits.maxTextureDimension3D,
        limits.maxTextureArrayLayers,
        limits.maxBindGroups,
        limits.maxUniformBufferBindingSize,
        limits.minUniformBufferOffsetAlignment,
        limits.maxVertexBuffers,
        limits.maxBufferSize,
        limits.maxVertexAttributes,
        limits.maxColorAttachments,
        limits.maxDynamicUniformBuffersPerPipelineLayout,
        limits.maxComputeWorkgroupSizeX,
        limits.maxComputeWorkgroupSizeY,
        limits.maxComputeWorkgroupSizeZ
    ];

    // ⚠ Staged in a real Uint8Array and handed over with one set(), because `destination` is a
    // MemoryView over BrowserWebGpuBinding.ReadLimits's `stackalloc byte[14 * sizeof(double)]` and
    // has no `.buffer` to lay a DataView over. This used to read it directly and threw on the ONE
    // call that establishes what the device can do.
    //
    // ⚠ `Uint8Array` exactly: the span is `Span<byte>`, and set() compares constructors by
    // identity. A Float64Array of the same bytes would throw.
    const staged = new Uint8Array(values.length * 8);
    const view = new DataView(staged.buffer);

    for (let index = 0; index < values.length; index++) {
        // Zero for anything this browser does not report; the C# side substitutes the
        // specification's floor rather than believing a zero limit.
        view.setFloat64(index * 8, Number(values[index]) || 0, true);
    }

    destination.set(staged, 0);
}

export function readFeatures() {
    return device ? [...device.features] : [];
}

/**
 * What the browser will say about the adapter, which is deliberately nothing: naming the GPU
 * and its driver would identify the machine. `requestAdapterInfo` is gated behind a permission
 * nothing here asks for, so this is a constant.
 */
export function adapterName() {
    return "WebGPU adapter";
}

export function hasSurface() {
    return !!context;
}

export function preferredFormat() {
    return TEXTURE_FORMAT_VALUE.get(canvasFormat) || 0;
}

export function shutdown() {
    if (context) {
        context.unconfigure();
        context = null;
    }

    if (device) {
        device.destroy();
    }

    device = null;
    queue = null;
    adapter = null;
    objects.length = 1;
    free.length = 0;
}

// ── Resources ───────────────────────────────────────────────────────────────────────────────

export function createBuffer(size, usage, label) {
    return store(device.createBuffer({ size, usage, label }));
}

/** format, width, height, depthOrArrayLayers, mipLevelCount, sampleCount, dimension, usage. */
export function createTexture(descriptor, label) {
    const read = new Reader(descriptor);
    const format = TEXTURE_FORMAT[read.int()];
    const width = read.int();
    const height = read.int();
    const depthOrArrayLayers = read.int();
    const mipLevelCount = read.int();
    const sampleCount = read.int();
    const dimension = TEXTURE_DIMENSION[read.int()];
    const usage = read.int();

    return store(
        device.createTexture({
            label,
            size: { width, height, depthOrArrayLayers },
            mipLevelCount,
            sampleCount,
            dimension,
            format,
            usage
        })
    );
}

/** format, dimension, baseMipLevel, mipLevelCount, baseArrayLayer, arrayLayerCount, aspect. */
export function createTextureView(texture, descriptor, label) {
    const read = new Reader(descriptor);
    const format = TEXTURE_FORMAT[read.int()];
    const dimension = TEXTURE_VIEW_DIMENSION[read.int()];
    const baseMipLevel = read.int();
    const mipLevelCount = read.int();
    const baseArrayLayer = read.int();
    const arrayLayerCount = read.int();
    const aspect = TEXTURE_ASPECT[read.int()];

    return store(
        get(texture).createView({
            label,
            format,
            dimension,
            baseMipLevel,
            mipLevelCount,
            baseArrayLayer,
            arrayLayerCount,
            aspect
        })
    );
}

/**
 * addressU, addressV, addressW, magFilter, minFilter, mipmapFilter, compare, maxAnisotropy,
 * then lodMinClamp and lodMaxClamp as 32-bit floats.
 */
export function createSampler(descriptor, label) {
    const read = new Reader(descriptor);
    const addressModeU = ADDRESS_MODE[read.int()];
    const addressModeV = ADDRESS_MODE[read.int()];
    const addressModeW = ADDRESS_MODE[read.int()];
    const magFilter = FILTER_MODE[read.int()];
    const minFilter = FILTER_MODE[read.int()];
    const mipmapFilter = FILTER_MODE[read.int()];
    const compare = COMPARE_FUNCTION[read.int()];
    const maxAnisotropy = read.int();
    const lodMinClamp = read.float();
    const lodMaxClamp = read.float();

    const created = {
        label,
        addressModeU,
        addressModeV,
        addressModeW,
        magFilter,
        minFilter,
        mipmapFilter,
        lodMinClamp,
        lodMaxClamp,
        maxAnisotropy
    };

    // An absent `compare` and a present-but-undefined one are not the same thing to WebGPU: the
    // second makes the sampler a comparison sampler with no comparison, which is a validation error.
    if (compare) {
        created.compare = compare;
    }

    return store(device.createSampler(created));
}

export function createShaderModule(code, label) {
    return store(device.createShaderModule({ code, label }));
}

/**
 * A count, then ten integers per entry: binding, visibility, bufferType, hasDynamicOffset,
 * samplerType, textureSampleType, textureViewDimension, multisampled, storageAccess,
 * storageFormat.
 */
export function createBindGroupLayout(entries, label) {
    const read = new Reader(entries);
    const count = read.int();
    const declared = [];

    for (let index = 0; index < count; index++) {
        const entry = { binding: read.int(), visibility: read.int() };
        const bufferType = BUFFER_BINDING_TYPE[read.int()];
        const hasDynamicOffset = read.bool();
        const samplerType = SAMPLER_BINDING_TYPE[read.int()];
        const sampleType = TEXTURE_SAMPLE_TYPE[read.int()];
        const viewDimension = TEXTURE_VIEW_DIMENSION[read.int()];
        const multisampled = read.bool();
        const storageAccess = STORAGE_TEXTURE_ACCESS[read.int()];
        const storageFormat = TEXTURE_FORMAT[read.int()];

        // Exactly one of these four may be present. WebGPU decides what a binding *is* from which
        // one it finds, so setting an empty object for the others is not harmless — it is a
        // different binding.
        if (bufferType) {
            entry.buffer = { type: bufferType, hasDynamicOffset };
        } else if (samplerType) {
            entry.sampler = { type: samplerType };
        } else if (storageAccess) {
            entry.storageTexture = { access: storageAccess, format: storageFormat, viewDimension };
        } else if (sampleType) {
            entry.texture = { sampleType, viewDimension, multisampled };
        }

        declared.push(entry);
    }

    return store(device.createBindGroupLayout({ label, entries: declared }));
}

/** A count, then one handle per bind group layout. */
export function createPipelineLayout(groups, label) {
    const read = new Reader(groups);
    const count = read.int();
    const bindGroupLayouts = [];

    for (let index = 0; index < count; index++) {
        bindGroupLayouts.push(read.object());
    }

    return store(device.createPipelineLayout({ label, bindGroupLayouts }));
}

/** A count, then per entry binding, buffer, sampler, textureView, then offset and size. */
export function createBindGroup(layout, entries, label) {
    const read = new Reader(entries);
    const count = read.int();
    const declared = [];

    for (let index = 0; index < count; index++) {
        const binding = read.int();
        const buffer = read.object();
        const sampler = read.object();
        const textureView = read.object();
        const offset = read.double();
        const size = read.double();

        if (buffer) {
            declared.push({ binding, resource: { buffer, offset, size } });
        } else if (sampler) {
            declared.push({ binding, resource: sampler });
        } else {
            declared.push({ binding, resource: textureView });
        }
    }

    return store(device.createBindGroup({ label, layout: get(layout), entries: declared }));
}

/**
 * layout, vertexModule, fragmentModule, topology, stripIndexFormat, frontFace, cullMode,
 * unclippedDepth, sampleCount, vertexBufferCount, colourTargetCount, hasDepthStencil;
 * then per vertex buffer arrayStride, stepMode, attributeCount and its attributes;
 * then per colour target nine integers; then the depth-stencil state.
 */
export function createRenderPipeline(descriptor, vertexEntryPoint, fragmentEntryPoint, label) {
    const read = new Reader(descriptor);
    const layout = read.object();
    const vertexModule = read.object();
    const fragmentModule = read.object();
    const topology = PRIMITIVE_TOPOLOGY[read.int()];
    const stripIndexFormat = INDEX_FORMAT[read.int()];
    const frontFace = FRONT_FACE[read.int()];
    const cullMode = CULL_MODE[read.int()];
    const unclippedDepth = read.bool();
    const sampleCount = read.int();
    const vertexBufferCount = read.int();
    const colourTargetCount = read.int();
    const hasDepthStencil = read.bool();

    const buffers = [];

    for (let index = 0; index < vertexBufferCount; index++) {
        const arrayStride = read.double();
        const stepMode = VERTEX_STEP_MODE[read.int()];
        const attributeCount = read.int();
        const attributes = [];

        for (let slot = 0; slot < attributeCount; slot++) {
            const format = VERTEX_FORMAT[read.int()];
            const shaderLocation = read.int();
            const offset = read.double();
            attributes.push({ format, shaderLocation, offset });
        }

        buffers.push({ arrayStride, stepMode, attributes });
    }

    const targets = [];

    for (let index = 0; index < colourTargetCount; index++) {
        const format = TEXTURE_FORMAT[read.int()];
        const blendEnabled = read.bool();
        const writeMask = read.int();
        const colour = {
            operation: BLEND_OPERATION[read.int()],
            srcFactor: BLEND_FACTOR[read.int()],
            dstFactor: BLEND_FACTOR[read.int()]
        };
        const alpha = {
            operation: BLEND_OPERATION[read.int()],
            srcFactor: BLEND_FACTOR[read.int()],
            dstFactor: BLEND_FACTOR[read.int()]
        };

        const target = { format, writeMask };

        // An absent blend means "do not blend". A blend object that happens to be one-times-source
        // plus zero is not the same thing, and some implementations take a slower path for it.
        if (blendEnabled) {
            target.blend = { color: colour, alpha };
        }

        targets.push(target);
    }

    const created = {
        label,
        layout,
        vertex: { module: vertexModule, entryPoint: vertexEntryPoint, buffers },
        primitive: { topology, frontFace, cullMode, unclippedDepth },
        multisample: { count: sampleCount }
    };

    // Required on a strip topology, forbidden on a list one.
    if (stripIndexFormat) {
        created.primitive.stripIndexFormat = stripIndexFormat;
    }

    if (fragmentModule) {
        created.fragment = { module: fragmentModule, entryPoint: fragmentEntryPoint, targets };
    }

    if (hasDepthStencil) {
        const format = TEXTURE_FORMAT[read.int()];
        const depthWriteEnabled = read.bool();
        const depthCompare = COMPARE_FUNCTION[read.int()];
        const stencilReadMask = read.int();
        const stencilWriteMask = read.int();
        const depthBias = read.int();
        const stencilFront = face(read);
        const stencilBack = face(read);
        const depthBiasSlopeScale = read.float();
        const depthBiasClamp = read.float();

        created.depthStencil = {
            format,
            depthWriteEnabled,
            depthCompare,
            stencilFront,
            stencilBack,
            stencilReadMask,
            stencilWriteMask,
            depthBias,
            depthBiasSlopeScale,
            depthBiasClamp
        };
    }

    return store(device.createRenderPipeline(created));
}

function face(read) {
    return {
        compare: COMPARE_FUNCTION[read.int()],
        failOp: STENCIL_OPERATION[read.int()],
        depthFailOp: STENCIL_OPERATION[read.int()],
        passOp: STENCIL_OPERATION[read.int()]
    };
}

export function createComputePipeline(layout, module, entryPoint, label) {
    return store(
        device.createComputePipeline({
            label,
            layout: get(layout),
            compute: { module: get(module), entryPoint }
        })
    );
}

/**
 * Drops the table entry. There is no per-type release in JavaScript: the collector reclaims the
 * WebGPU object once nothing — including the implementation's own pending work — still refers to
 * it, which is the same guarantee wgpu*Release gives on the native surface.
 */
export function release(handle) {
    if (handle > 0 && objects[handle] !== undefined) {
        objects[handle] = undefined;
        free.push(handle);
    }
}

// ── Queue ───────────────────────────────────────────────────────────────────────────────────

export function writeBuffer(buffer, offset, data) {
    // ⚠ A FOURTH MemoryView site, and one nobody had written down. `data` used to be passed to
    // WebGPU directly, on the reasoning that the view is only valid for the duration of the call
    // and writeBuffer copies immediately — true, and beside the point: GPUQueue.writeBuffer takes
    // a BufferSource, and a MemoryView is an ordinary JavaScript object. Chrome rejects it with
    // "parameter 3 is not of type 'BufferSource'", so EVERY buffer upload on this backend threw —
    // every uniform, every vertex buffer, every index buffer, on frame one.
    //
    // ⚠ So the copy is not optional and it is not a defensive one; slice() is the only way to get
    // the bytes out of WebAssembly memory at all. It is a per-upload allocation on the hot path,
    // which is a real cost and is why BrowserWebGpuBinding.WriteBuffer goes to the trouble of
    // un-consting its span to avoid a copy on the C# side. Removing it needs a mapped staging
    // buffer, not a cleverer argument.
    const bytes = data.slice(0, data.length);

    queue.writeBuffer(get(buffer), offset, bytes, 0, bytes.byteLength);
}

export function submit(commandBuffer) {
    queue.submit([get(commandBuffer)]);
}

// ── Encoding ────────────────────────────────────────────────────────────────────────────────

export function createCommandEncoder(label) {
    return store(device.createCommandEncoder({ label }));
}

export function finishCommandEncoder(encoder, label) {
    const buffer = get(encoder).finish({ label });
    release(encoder);
    return store(buffer);
}

export function copyBufferToBuffer(encoder, source, sourceOffset, destination, destinationOffset, size) {
    get(encoder).copyBufferToBuffer(get(source), sourceOffset, get(destination), destinationOffset, size);
}

/** 0 buffer-to-texture, 1 texture-to-buffer, 2 texture-to-texture. */
export function copyTexture(encoder, kind, args) {
    const read = new Reader(args);
    const target = get(encoder);

    if (kind === 0) {
        const source = linear(read);
        const destination = region(read);
        target.copyBufferToTexture(source, destination, extent(read));
    } else if (kind === 1) {
        const source = region(read);
        const destination = linear(read);
        target.copyTextureToBuffer(source, destination, extent(read));
    } else {
        const source = region(read);
        const destination = region(read);
        target.copyTextureToTexture(source, destination, extent(read));
    }
}

function region(read) {
    const texture = read.object();
    const mipLevel = read.int();
    const x = read.int();
    const y = read.int();
    const z = read.int();
    const aspect = TEXTURE_ASPECT[read.int()];

    return { texture, mipLevel, origin: { x, y, z }, aspect };
}

function linear(read) {
    const buffer = read.object();
    const bytesPerRow = read.int();
    const rowsPerImage = read.int();
    const offset = read.double();

    return { buffer, offset, bytesPerRow, rowsPerImage };
}

function extent(read) {
    return { width: read.int(), height: read.int(), depthOrArrayLayers: read.int() };
}

/** 0 push, 1 pop, 2 marker — on an encoder or a pass, which have the same three methods. */
export function debugGroup(target, action, name) {
    const encoder = get(target);

    if (!encoder) {
        return;
    }

    if (action === 0) {
        encoder.pushDebugGroup(name);
    } else if (action === 1) {
        encoder.popDebugGroup();
    } else {
        encoder.insertDebugMarker(name);
    }
}

// ── Passes ──────────────────────────────────────────────────────────────────────────────────

/**
 * A colour attachment count and a depth flag; then per colour attachment view, resolveTarget,
 * loadOp and storeOp followed by four doubles of clear colour; then, when present, view,
 * depthLoadOp, depthStoreOp, stencilLoadOp, stencilStoreOp, stencilClearValue, depthReadOnly and
 * stencilReadOnly, and depthClearValue as a 32-bit float.
 */
export function beginRenderPass(encoder, descriptor, label) {
    const read = new Reader(descriptor);
    const colourCount = read.int();
    const hasDepth = read.bool();
    const colorAttachments = [];

    for (let index = 0; index < colourCount; index++) {
        const view = read.object();
        const resolveTarget = read.object();
        const loadOp = LOAD_OP[read.int()];
        const storeOp = STORE_OP[read.int()];
        const r = read.double();
        const g = read.double();
        const b = read.double();
        const a = read.double();

        const attachment = { view, loadOp, storeOp, clearValue: { r, g, b, a } };

        if (resolveTarget) {
            attachment.resolveTarget = resolveTarget;
        }

        colorAttachments.push(attachment);
    }

    const created = { label, colorAttachments };

    if (hasDepth) {
        const view = read.object();
        const depthLoadOp = LOAD_OP[read.int()];
        const depthStoreOp = STORE_OP[read.int()];
        const stencilLoadOp = LOAD_OP[read.int()];
        const stencilStoreOp = STORE_OP[read.int()];
        const stencilClearValue = read.int();
        const depthReadOnly = read.bool();
        const stencilReadOnly = read.bool();
        const depthClearValue = read.float();

        const attachment = { view, depthClearValue, stencilClearValue };

        // A read-only aspect may carry no load or store operation at all, and WebGPU rejects the
        // combination rather than ignoring it.
        if (depthReadOnly) {
            attachment.depthReadOnly = true;
        } else {
            attachment.depthLoadOp = depthLoadOp;
            attachment.depthStoreOp = depthStoreOp;
        }

        // Stencil operations belong only on a format that has stencil. A depth-only format with them
        // set is a validation error, so they are attached from the view's format rather than from
        // what the caller asked for — which the caller cannot know here.
        if (stencilReadOnly) {
            attachment.stencilReadOnly = true;
        } else if (stencilLoadOp) {
            attachment.stencilLoadOp = stencilLoadOp;
            attachment.stencilStoreOp = stencilStoreOp;
        }

        created.depthStencilAttachment = attachment;
    }

    return store(get(encoder).beginRenderPass(created));
}

export function beginComputePass(encoder, label) {
    return store(get(encoder).beginComputePass({ label }));
}

export function endPass(pass) {
    get(pass).end();
    release(pass);
}

export function setPipeline(pass, pipeline) {
    get(pass).setPipeline(get(pipeline));
}

export function setBindGroup(pass, group, bindGroup, dynamicOffsets) {
    if (dynamicOffsets.byteLength === 0) {
        get(pass).setBindGroup(group, get(bindGroup));
        return;
    }

    // A fresh array rather than a view: setBindGroup takes a sequence and the view is a window onto
    // WebAssembly memory that may move the moment anything allocates.
    //
    // ⚠ Through slice(), not `.buffer`. `dynamicOffsets` is a MemoryView and has no `.buffer` —
    // the version this replaces threw a TypeError here. `byteLength` DOES exist on a MemoryView,
    // so the early return above worked and only this branch failed: a backend that bound fine
    // until something used a dynamic uniform offset, which is the shape of bug that gets blamed
    // on the shader.
    //
    // slice() returns a Uint8Array whose buffer is exactly these bytes and whose byteOffset is 0,
    // so the Uint32Array can be laid straight over it. The C# side packs one int32 per offset
    // (BrowserWebGpuBinding.SetBindGroup), so the length is a multiple of four by construction.
    const bytes = dynamicOffsets.slice(0, dynamicOffsets.length);
    const offsets = new Uint32Array(bytes.buffer, 0, bytes.byteLength / 4);

    get(pass).setBindGroup(group, get(bindGroup), offsets);
}

export function setVertexBuffer(pass, slot, buffer, offset, size) {
    get(pass).setVertexBuffer(slot, get(buffer), offset, size);
}

export function setIndexBuffer(pass, buffer, format, offset, size) {
    get(pass).setIndexBuffer(get(buffer), INDEX_FORMAT[format], offset, size);
}

export function setViewport(pass, x, y, width, height, minDepth, maxDepth) {
    get(pass).setViewport(x, y, width, height, minDepth, maxDepth);
}

export function setScissorRect(pass, x, y, width, height) {
    get(pass).setScissorRect(x, y, width, height);
}

export function setBlendConstant(pass, r, g, b, a) {
    get(pass).setBlendConstant({ r, g, b, a });
}

export function setStencilReference(pass, reference) {
    get(pass).setStencilReference(reference);
}

export function draw(pass, vertexCount, instanceCount, firstVertex, firstInstance) {
    get(pass).draw(vertexCount, instanceCount, firstVertex, firstInstance);
}

export function drawIndexed(pass, indexCount, instanceCount, firstIndex, baseVertex, firstInstance) {
    get(pass).drawIndexed(indexCount, instanceCount, firstIndex, baseVertex, firstInstance);
}

export function drawIndexedIndirect(pass, args, offset) {
    get(pass).drawIndexedIndirect(get(args), offset);
}

export function dispatch(pass, x, y, z) {
    get(pass).dispatchWorkgroups(x, y, z);
}

export function dispatchIndirect(pass, args, offset) {
    get(pass).dispatchWorkgroupsIndirect(get(args), offset);
}

// ── Surface ─────────────────────────────────────────────────────────────────────────────────

export function configureSurface(format, usage, width, height, alphaMode) {
    if (!context) {
        return;
    }

    // The canvas's backing store is set here rather than by the page, so that the size the renderer
    // was told about and the size it draws into cannot disagree — which is the whole of "my UI is
    // blurry on a high-DPI display".
    context.canvas.width = width;
    context.canvas.height = height;

    context.configure({
        device,
        format: TEXTURE_FORMAT[format] || canvasFormat,
        usage,
        alphaMode: ALPHA_MODE[alphaMode] || "opaque"
    });
}

export function acquireSurfaceTexture() {
    if (!context) {
        return 0;
    }

    try {
        return store(context.getCurrentTexture());
    } catch (error) {
        lastError = String(error);
        return 0;
    }
}

/** The last thing that went wrong, for a diagnostic that wants it. */
export function lastErrorMessage() {
    const message = lastError;
    lastError = "";
    return message;
}
