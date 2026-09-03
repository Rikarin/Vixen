// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser half of Vixen.Graphics.WebGPU.Browser, run under Node against a stub GPU.
//
//     node Platform/Vixen.Platform.Web.Tests/js/vixen-webgpu.test.mjs
//
// ── ⚠ Why this exists, and why the thing it was waiting for was never needed ─────────────────
//
// vixen-webgpu.js carried the same MemoryView mistake that `nuke BrowserSmoke` found four times
// in vixen-platform.js: a `[JSMarshalAs<JSType.MemoryView>] Span<byte>` is NOT a typed array, and
// four sites here treated it as one. It was left unfixed on the stated grounds that neither site
// could be RUN without a WebGPU adapter, and an unverifiable fix is the trap this repository
// keeps documenting.
//
// ⚠ That reasoning was wrong, and this file is the refutation. Three of the four throw a
// `TypeError` reading `.buffer` off the view — BEFORE any GPU object is touched, in code that a
// device would never reach. The fourth hands the view to `GPUQueue.writeBuffer`, which needs a
// stub of one method. What was actually required was a faithful MemoryView, which is
// `memory-view.mjs`, and a GPU stub small enough to fit below. No adapter, no browser, one second.
//
// ── What the stub is and is not ──────────────────────────────────────────────────────────────
//
// It records calls and returns plausible objects. It does NOT validate a descriptor the way a real
// implementation would, so this suite cannot tell you a pipeline is legal — `nuke BrowserSmoke`
// with a real adapter is still owed for that (issue #36 and the CI leg it names). What it CAN
// tell you is what no C# test and no gate in this repository can: that a value crossing the
// [JSImport] boundary arrives, and arrives with its bytes intact.

import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { MemoryView } from "./memory-view.mjs";

// ── Assertions ───────────────────────────────────────────────────────────────────────────────

let passed = 0;

function check(condition, what) {
    if (!condition) {
        console.error(`FAILED: ${what}`);
        process.exit(1);
    }

    passed++;
}

function equal(actual, expected, what) {
    check(actual === expected, `${what} — expected ${expected}, got ${actual}`);
}

/**
 * Runs a call that must not throw, and reports what it threw if it did.
 *
 * ⚠ Worth its own helper rather than letting the exception escape: every defect this file is
 * written against surfaces as a TypeError out of a constructor, and an unhandled rejection prints
 * a stack with no statement of what was being asserted.
 */
function survives(what, body) {
    try {
        const value = body();
        passed++;
        return value;
    } catch (error) {
        console.error(`FAILED: ${what} — threw ${error}`);
        process.exit(1);
    }
}

// ── A stub GPU ───────────────────────────────────────────────────────────────────────────────

const calls = [];

const record = (name, args) => {
    calls.push({ name, args });
    return { __kind: name };
};

/** The most recent call of a name, or undefined. */
const last = name => calls.filter(call => call.name === name).at(-1);

const queue = {
    writeBuffer(buffer, offset, data, dataOffset, size) {
        // ⚠ The real GPUQueue.writeBuffer takes a BufferSource — an ArrayBuffer or an
        // ArrayBufferView. A MemoryView is neither: it is an ordinary object, and Chrome rejects
        // it with "parameter 3 is not of type 'BufferSource'". The stub enforces exactly that,
        // because a stub that accepted anything is what let the original four defects through.
        if (!ArrayBuffer.isView(data) && !(data instanceof ArrayBuffer)) {
            throw new TypeError(
                "Failed to execute 'writeBuffer' on 'GPUQueue': parameter 3 is not of type 'BufferSource'."
            );
        }

        calls.push({ name: "writeBuffer", args: [buffer, offset, data.slice(), dataOffset, size] });
    },
    submit: (buffers) => record("submit", [buffers])
};

const device = {
    queue,
    // Deliberately not round numbers, and deliberately one that a browser would report as 0.
    limits: {
        maxTextureDimension2D: 16384,
        maxTextureDimension3D: 2048,
        maxTextureArrayLayers: 256,
        maxBindGroups: 4,
        maxUniformBufferBindingSize: 65536,
        minUniformBufferOffsetAlignment: 256,
        maxVertexBuffers: 8,
        maxBufferSize: 268435456,
        maxVertexAttributes: 16,
        maxColorAttachments: 8,
        maxDynamicUniformBuffersPerPipelineLayout: 8,
        maxComputeWorkgroupSizeX: 256,
        maxComputeWorkgroupSizeY: 256,
        maxComputeWorkgroupSizeZ: 64
    },
    features: new Set(["depth-clip-control", "timestamp-query"]),
    lost: new Promise(() => { }),
    addEventListener() { },
    createBuffer: descriptor => record("createBuffer", [descriptor]),
    createTexture: descriptor => record("createTexture", [descriptor]),
    createSampler: descriptor => record("createSampler", [descriptor]),
    createShaderModule: descriptor => record("createShaderModule", [descriptor]),
    createBindGroupLayout: descriptor => record("createBindGroupLayout", [descriptor]),
    createPipelineLayout: descriptor => record("createPipelineLayout", [descriptor]),
    createBindGroup: descriptor => record("createBindGroup", [descriptor]),
    createRenderPipeline: descriptor => record("createRenderPipeline", [descriptor]),
    createComputePipeline: descriptor => record("createComputePipeline", [descriptor]),
    createCommandEncoder: descriptor => record("createCommandEncoder", [descriptor])
};

const adapter = {
    limits: device.limits,
    features: device.features,
    requestDevice: descriptor => {
        calls.push({ name: "requestDevice", args: [descriptor] });
        return Promise.resolve(device);
    }
};

// ⚠ defineProperty and not assignment. Node has had a real `navigator` global since 21, and it is
// an accessor with no setter — `globalThis.navigator = …` throws
// "Cannot set property navigator of #<Object> which has only a getter" on any modern Node, which
// looks like a defect in the module under test and is not one.
Object.defineProperty(globalThis, "navigator", {
    value: {
        gpu: {
            requestAdapter: options => {
                calls.push({ name: "requestAdapter", args: [options] });
                return Promise.resolve(adapter);
            },
            getPreferredCanvasFormat: () => "bgra8unorm"
        }
    },
    writable: true,
    configurable: true
});

// ── The module ───────────────────────────────────────────────────────────────────────────────

const here = dirname(fileURLToPath(import.meta.url));

// A file URL and not the path — see the note in vixen-platform.test.mjs, which this shares a
// reason with: a Windows path has a drive-letter scheme and the ESM loader rejects it.
const gpu = await import(
    pathToFileURL(join(here, "../../Vixen.Graphics.WebGPU.Browser/wwwroot/vixen-webgpu.js")).href
);

check(gpu.isSupported(), "isSupported sees navigator.gpu");

equal(await gpu.initialise("", "high-performance"), "", "initialise takes the adapter and the device");
equal(last("requestAdapter").args[0].powerPreference, "high-performance", "…and passes the preference through");

// ⚠ Every limit the adapter reports, not the guaranteed floor. A device created with no
// requiredLimits reports the specification's minimums whatever the hardware manages.
equal(
    last("requestDevice").args[0].requiredLimits.maxTextureDimension2D,
    16384,
    "…and asks for everything the adapter offers"
);

// ── readLimits: fourteen doubles into a byte view ────────────────────────────────────────────
//
// ⚠ This wrote through `new DataView(destination.buffer, …)`. `destination` is the MemoryView for
// BrowserWebGpuBinding.ReadLimits's `stackalloc byte[14 * sizeof(double)]`, which has no `.buffer`
// — so this threw a TypeError on the ONE call that establishes what the device can do, on every
// browser build, before a frame was drawn.

const limits = new Uint8Array(14 * 8);

survives("readLimits writes through a MemoryView rather than reading .buffer off it", () =>
    gpu.readLimits(new MemoryView(limits))
);

const read = new DataView(limits.buffer);

// The C# side reads these with BinaryPrimitives.ReadDoubleLittleEndian, which is why the JS side
// states little-endian at every write rather than taking DataView's big-endian default.
equal(read.getFloat64(0, true), 16384, "…maxTextureDimension2D, the first of the fourteen");
equal(read.getFloat64(3 * 8, true), 4, "…maxBindGroups, in WebGpuLimits's declaration order");
equal(read.getFloat64(13 * 8, true), 64, "…maxComputeWorkgroupSizeZ, the last");

// ⚠ Not merely "did not throw". A view whose bytes were never written reads back as fourteen
// zeros, which is exactly what a browser reporting no limits looks like — and WebGpuLimits
// .OrGuaranteed() would then substitute the specification's floor and nobody would ever know the
// call had failed. Zero means "off" here, so at least one value must be a number nothing defaults
// to.
check(read.getFloat64(7 * 8, true) === 268435456, "…maxBufferSize, which no default would produce");

// ── The Reader: nine entry points decode a packed descriptor through it ──────────────────────
//
// ⚠ `new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)` in the constructor, over a
// MemoryView. This is the widest of the four: createTexture, createTextureView, createSampler,
// createBindGroupLayout, createPipelineLayout, createBindGroup, createRenderPipeline, copyTexture
// and beginRenderPass ALL build a Reader, so the entire descriptor half of the backend threw.

/** Packs little-endian int32s the way WebGpuPacker.Int does. */
function packInts(...values) {
    const bytes = new Uint8Array(values.length * 4);
    const view = new DataView(bytes.buffer);
    values.forEach((value, index) => view.setInt32(index * 4, value, true));
    return bytes;
}

// format, width, height, depthOrArrayLayers, mipLevelCount, sampleCount, dimension, usage —
// the layout createTexture's own doc comment states, repeated here as the writer documented it.
const RGBA8_UNORM = 18;
const texture = packInts(RGBA8_UNORM, 1920, 1080, 1, 4, 1, 1, 0x10);

const handle = survives("createTexture reads a descriptor that arrived as a MemoryView", () =>
    gpu.createTexture(new MemoryView(texture), "gbuffer")
);

check(handle > 0, "…and stores the texture behind a handle");

const created = last("createTexture").args[0];

equal(created.format, "rgba8unorm", "…decoding the format from webgpu.h's numbering");
equal(created.size.width, 1920, "…the width");
equal(created.size.height, 1080, "…the height");
equal(created.mipLevelCount, 4, "…the mip count");
equal(created.usage, 0x10, "…and the usage bits");
equal(created.label, "gbuffer", "…with the label, which does not travel packed");

// ── writeBuffer: the upload path, and the one the issue did not name ─────────────────────────
//
// ⚠ A FOURTH site, not in the three the issue lists. `queue.writeBuffer(buffer, offset, data, …)`
// passed the MemoryView straight to WebGPU, which takes a BufferSource. An ordinary object is not
// one, so EVERY buffer upload on the browser backend threw — which is every uniform, every vertex
// buffer and every index buffer, on frame one.

const buffer = gpu.createBuffer(256, 0x20, "camera");
const payload = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);

survives("writeBuffer hands WebGPU a BufferSource and not a MemoryView", () =>
    gpu.writeBuffer(buffer, 0, new MemoryView(payload))
);

const written = last("writeBuffer");

equal(written.args[4], 8, "…with the view's byteLength as the size");
equal(written.args[2][0], 1, "…and the caller's first byte");
equal(written.args[2][7], 8, "…through to its last, rather than a correctly sized run of zeros");

// ── setBindGroup: the dynamic-offset path ────────────────────────────────────────────────────
//
// ⚠ `dynamicOffsets.buffer.slice(…)` — the same missing `.buffer`, on the branch a bind group with
// dynamic offsets takes. `byteLength` exists on a MemoryView, so the empty branch above it worked
// and the non-empty one threw: a backend that appeared to bind fine until something used a
// dynamic uniform offset.

let boundOffsets = null;

const pass = {
    setBindGroup(group, bindGroup, offsets) {
        boundOffsets = offsets;
        calls.push({ name: "setBindGroup", args: [group, bindGroup, offsets] });
    },
    end() { }
};

// Reach into the module's handle table the only way its surface allows: store a real object by
// asking for one back. createCommandEncoder returns a handle to whatever the stub gave it.
const encoder = gpu.createCommandEncoder("frame");

device.createCommandEncoder = () => pass;

const passHandle = gpu.createCommandEncoder("pass");

check(encoder > 0 && passHandle > 0, "the handle table hands out handles for encoders");

survives("setBindGroup with no dynamic offsets takes the short branch", () =>
    gpu.setBindGroup(passHandle, 0, 0, new MemoryView(new Uint8Array(0)))
);

survives("setBindGroup reads dynamic offsets out of a MemoryView", () =>
    gpu.setBindGroup(passHandle, 1, 0, new MemoryView(packInts(256, 512)))
);

check(boundOffsets instanceof Uint32Array, "…as a Uint32Array, which is what WebGPU takes");
equal(boundOffsets.length, 2, "…one per offset");
equal(boundOffsets[0], 256, "…with the first offset's value");
equal(boundOffsets[1], 512, "…and the second, rather than two zeros");

console.log(`${passed} assertions passed`);
