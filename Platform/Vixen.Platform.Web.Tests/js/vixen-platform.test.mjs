// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser half's own tests, run under Node against a DOM stub.
//
//     node Platform/Vixen.Platform.Web.Tests/js/vixen-platform.test.mjs
//
// ── Why this exists, and why it is not xunit ─────────────────────────────────────────────────
//
// vixen-platform.js is a contract, not a helper. The twelve-double record layout is duplicated in
// WebEventRecord.cs because nothing can make one side derive it from the other across the language
// boundary; the HID key table, the wheel-unit conversion and the button-role mapping are each a
// translation that is wrong in a way no C# test can see. Leaving all of that to a browser smoke
// test means finding an inverted axis by looking at a running game.
//
// It is not driven from the xunit project because that project targets net10.0 and this is
// JavaScript: there is no runner that would host both. Node with a stub is enough — everything
// asserted here is arithmetic and table lookup, and the parts that genuinely need a browser
// (IndexedDB, fetch, pointer lock, the IME) are deliberately not touched.
//
// No dependencies, no package.json, no install step. Exits non-zero on the first failure.

import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, join } from "node:path";
import { MemoryView } from "./memory-view.mjs";

// ── A DOM stub, big enough to construct a canvas and fire events at it ───────────────────────

const listeners = new Map();
const keyOf = (target, type) => `${target.__id}:${type}`;

let nextId = 1;

function makeElement(tag) {
    return {
        __id: nextId++,
        tagName: tag.toUpperCase(),
        style: {},
        attributes: {},
        width: 0,
        height: 0,
        tabIndex: 0,
        value: "",
        setAttribute(name, value) { this.attributes[name] = value; },
        removeAttribute(name) { delete this.attributes[name]; },
        hasAttribute(name) { return name in this.attributes; },
        getBoundingClientRect: () => ({ left: 0, top: 0, width: 800, height: 600 }),
        addEventListener(type, listener) {
            listeners.set(keyOf(this, type), (listeners.get(keyOf(this, type)) ?? []).concat(listener));
        },
        removeEventListener() { },
        focus() { globalThis.document.activeElement = this; },
        blur() { globalThis.document.activeElement = null; },
        appendChild() { },
        requestPointerLock() { },
        requestFullscreen: () => Promise.resolve(),
        setPointerCapture() { },
        releasePointerCapture() { }
    };
}

const body = makeElement("body");
const canvasElement = makeElement("canvas");

globalThis.document = {
    __id: 0,
    body,
    activeElement: null,
    visibilityState: "visible",
    title: "",
    fullscreenElement: null,
    pointerLockElement: null,
    createElement: makeElement,
    querySelector: selector => (selector === "#view" ? canvasElement : null),
    addEventListener(type, listener) {
        listeners.set(keyOf(this, type), (listeners.get(keyOf(this, type)) ?? []).concat(listener));
    },
    exitPointerLock() { },
    exitFullscreen: () => Promise.resolve()
};

globalThis.devicePixelRatio = 2;
globalThis.screen = { width: 1920, height: 1080, availWidth: 1920, availHeight: 1040 };
globalThis.matchMedia = () => ({ matches: false, addEventListener() { }, removeEventListener() { } });
globalThis.ResizeObserver = class { observe() { } disconnect() { } };
globalThis.addEventListener = () => { };
globalThis.requestAnimationFrame = () => 1;
globalThis.cancelAnimationFrame = () => { };
globalThis.innerWidth = 800;
globalThis.innerHeight = 600;

// ⚠ Two slots, and the empty one is the load-bearing half. getGamepads() returns a sparse array
// with a null for every unoccupied port, and pollGamepads has a separate branch for those — which
// is where `view.fill()` was, a method a MemoryView does not have. A stub returning [] never
// enters the write path at all, so it can only assert that a function with a TypeError in it
// returns zero.
const stubGamepad = {
    index: 1,
    id: "Vixen Test Pad (STANDARD GAMEPAD)",
    mapping: "standard",
    axes: [0.25, -0.5, 0, 0],
    buttons: [{ value: 1 }, { value: 0 }],
    vibrationActuator: null
};

// navigator is a getter-only global in modern Node, so it has to be redefined rather than assigned.
Object.defineProperty(globalThis, "navigator", {
    value: { hardwareConcurrency: 8, getGamepads: () => [null, stubGamepad] },
    configurable: true
});

function fire(element, type, event) {
    for (const listener of listeners.get(keyOf(element, type)) ?? []) {
        listener({ preventDefault() { }, ...event });
    }
}

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

function near(actual, expected, what) {
    check(Math.abs(actual - expected) < 1e-9, `${what} — expected ${expected}, got ${actual}`);
}

// ── The module ───────────────────────────────────────────────────────────────────────────────

const here = dirname(fileURLToPath(import.meta.url));

// ⚠ A file URL, not the path. `join` gives a filesystem path, and a dynamic import of one is a
// specifier Node parses as a URL: on POSIX "/home/…" has no scheme and is read as a path, but on
// Windows "D:\…" has one — "d:" — and the loader rejects it outright with
// ERR_UNSUPPORTED_ESM_URL_SCHEME. That took the whole Windows CI leg down at Compile, before a
// single .NET test ran, because this runs from an MSBuild target rather than from the test host.
const platform = await import(
    pathToFileURL(join(here, "../../Vixen.Platform.Web/wwwroot/vixen-platform.js")).href
);

const RECORD = 12;

// ⚠ The typed array stands in for WebAssembly memory; `bufferView` is what the marshaller would
// really hand drainEvents, and it is `bufferView` that every call below passes. Reading the records
// back off `buffer` is fine — that is the heap — but handing `buffer` itself to a view-taking
// function is the mistake this whole file exists to catch: a real Float64Array has an indexer and a
// fill(), so it sails through a body a browser throws out of on the first frame of every build.
const buffer = new Float64Array(RECORD * 64);
const bufferView = new MemoryView(buffer);

/** Kind, to keep the assertions readable. Mirrors PlatformEventKind. */
const Kind = {
    windowResized: 4, keyDown: 20, keyUp: 21,
    mouseButtonDown: 31, mouseWheel: 33, touchDown: 40, dropFile: 80, dropText: 81,
    dropBegin: 82, dropComplete: 83
};

// ── The canvas ───────────────────────────────────────────────────────────────────────────────

const handle = platform.createCanvas("#view");

equal(handle, 1, "createCanvas returns a handle");
equal(platform.canvasSelector(handle), '[data-vixen-canvas="1"]', "the selector is derived from the handle");
equal(canvasElement.attributes["data-vixen-canvas"], "1", "the attribute is stamped on the element");
equal(canvasElement.style.touchAction, "none", "the browser's own touch gestures are suppressed");

// The backing store is the CSS box times devicePixelRatio, and it is what a swapchain is built at.
equal(platform.clientWidth(handle), 800, "client width is CSS pixels");
equal(platform.pixelWidth(handle), 1600, "pixel width is CSS times DPR");
equal(platform.pixelHeight(handle), 1200, "pixel height is CSS times DPR");
equal(canvasElement.width, 1600, "the canvas backing store was resized");

equal(platform.createCanvas("#nothing"), 0, "a selector matching nothing is refused");

// ── The ring ─────────────────────────────────────────────────────────────────────────────────

let taken = platform.drainEvents(bufferView);

equal(taken, 1, "creating the canvas queued its size");
equal(buffer[0], Kind.windowResized, "…as a WindowResized");
equal(buffer[4], 800, "…carrying the logical width");
equal(buffer[6], 1600, "…and the framebuffer width");

// ── Keyboard: KeyboardEvent.code onto USB HID, by position ───────────────────────────────────

fire(canvasElement, "keydown", {
    code: "KeyQ", timeStamp: 12.5, repeat: false, shiftKey: true, getModifierState: () => false
});

fire(canvasElement, "keyup", {
    code: "AltRight", timeStamp: 13, altKey: true, getModifierState: () => false
});

taken = platform.drainEvents(bufferView);

equal(taken, 2, "two key events");
equal(buffer[0], Kind.keyDown, "a key down");
equal(buffer[9], 20, "KeyQ is HID 20 — the position, not the letter an AZERTY keyboard prints there");
equal(buffer[3], 1, "shift is reported as the left one");
equal(buffer[2], 12.5, "the browser's own timestamp survives, sub-millisecond");
equal(buffer[RECORD + 0], Kind.keyUp, "a key up");
equal(buffer[RECORD + 9], 230, "AltRight is HID 230");
equal(buffer[RECORD + 3], 1 << 5, "…and corrects the modifier mask to the right-hand bit, which is AltGr");

fire(canvasElement, "keydown", { code: "Unidentified", timeStamp: 1, getModifierState: () => false });
platform.drainEvents(bufferView);
equal(buffer[9], 0, "a key with no HID position is Key.Unknown, for .NET to drop");

// ── Wheel: three units, one contract ─────────────────────────────────────────────────────────

fire(canvasElement, "wheel", { deltaMode: 1, deltaX: 0, deltaY: -3, offsetX: 10, offsetY: 20, timeStamp: 1 });
fire(canvasElement, "wheel", { deltaMode: 0, deltaX: 0, deltaY: -100, offsetX: 10, offsetY: 20, timeStamp: 2 });
fire(canvasElement, "wheel", { deltaMode: 2, deltaX: 0, deltaY: -1, offsetX: 10, offsetY: 20, timeStamp: 3 });

taken = platform.drainEvents(bufferView);

equal(taken, 3, "three wheel events");
equal(buffer[0], Kind.mouseWheel, "a wheel event");
near(buffer[7], 1, "three lines up is one notch, positive up (Firefox)");
near(buffer[RECORD + 7], 1, "a hundred pixels up is one notch (Chrome, trackpad)");
near(buffer[2 * RECORD + 7], 1, "one page up is one notch");

// ── Mouse: role, not side; offset, not a layout read ─────────────────────────────────────────

fire(canvasElement, "pointerdown", {
    pointerType: "mouse", button: 2, offsetX: 5, offsetY: 6, detail: 2, timeStamp: 3, pointerId: 1
});

taken = platform.drainEvents(bufferView);

equal(taken, 1, "a mouse button");
equal(buffer[0], Kind.mouseButtonDown, "…down");
equal(buffer[9], 2, "PointerEvent.button 2 is MouseButton.Secondary");
equal(buffer[10], 2, "the OS's own click count, not one we derived from timestamps");
equal(buffer[4], 5, "the position comes from offsetX");
equal(buffer[5], 6, "…and offsetY");

fire(canvasElement, "pointerdown", {
    pointerType: "touch", button: 0, offsetX: 1, offsetY: 1, timeStamp: 4, pointerId: 2
});

equal(platform.drainEvents(bufferView), 0, "a touch is not also reported as a pointer, or every finger arrives twice");

// ── Touch: the browser's identifier and its pressure ─────────────────────────────────────────

fire(canvasElement, "touchstart", {
    changedTouches: [{ identifier: 99, clientX: 30, clientY: 40, force: 0.5 }], timeStamp: 4
});

taken = platform.drainEvents(bufferView);

equal(taken, 1, "a touch down");
equal(buffer[0], Kind.touchDown, "…as TouchDown");
equal(buffer[10], 99, "the browser's identifier, for TouchTracker to turn into a small dense id");
equal(buffer[8], 0.5, "pressure");
equal(buffer[4], 30, "the position, from the bounding rect TouchEvent has no offset for");

// ── Strings travel by handle ─────────────────────────────────────────────────────────────────

fire(canvasElement, "drop", {
    dataTransfer: { files: [{ name: "level.vxb", size: 12 }], getData: () => "hello" },
    offsetX: 1, offsetY: 2, timeStamp: 5
});

taken = platform.drainEvents(bufferView);

// The files are bracketed, so that .NET can tell one drag of five files from five drags of one.
// A browser hands the whole DataTransfer list over in a single event and this used to flatten it.
equal(taken, 4, "a bracket, the dropped file inside it, and the text that came with it");
equal(buffer[0], Kind.dropBegin, "the bracket opens first");
equal(buffer[4], buffer[RECORD + 4], "…carrying the same position as the file, which is where the group is hit-tested");
equal(buffer[RECORD + 0], Kind.dropFile, "then the file");
equal(platform.takeString(buffer[RECORD + 8]), "level.vxb", "the file's name, by handle");
equal(platform.takeString(buffer[RECORD + 8]), "", "…and the handle is released once taken");
equal(buffer[2 * RECORD + 0], Kind.dropComplete, "then the bracket closes");
equal(buffer[3 * RECORD + 0], Kind.dropText, "then the text, which is not part of the file group");
equal(platform.takeString(buffer[3 * RECORD + 8]), "hello", "the dropped text");

equal(platform.droppedFileCount(), 1, "the File itself is parked, because a browser gives no path");
equal(platform.droppedFileName(0), "level.vxb", "…under the same index as the event's order");
platform.clearDroppedFiles(1);
equal(platform.droppedFileCount(), 0, "and released when .NET has taken it");

// ── A drain smaller than the queue keeps the rest, in order ──────────────────────────────────

for (let index = 0; index < 10; index++) {
    fire(canvasElement, "keydown", { code: "KeyA", timeStamp: index, repeat: false, getModifierState: () => false });
}

const small = new Float64Array(RECORD * 4);
const smallView = new MemoryView(small);
const seen = [];

do {
    taken = platform.drainEvents(smallView);

    for (let index = 0; index < taken; index++) {
        seen.push(small[index * RECORD + 2]);
    }
} while (taken === 4);

equal(seen.length, 10, "every event survives a drain smaller than the queue");
check(seen.every((value, index) => value === index), `…in order — got ${seen.join(",")}`);

// ── ⚠ A faithful MemoryView, because a typed array is NOT one ────────────────────────────────
//
// Every assertion above hands these functions a real Float64Array or Uint8Array, and that is a
// stub which is MORE PERMISSIVE THAN THE RUNTIME — which is why four defects in this module
// survived this suite until `nuke BrowserSmoke` called them from a real head.
//
// ⚠ The double now lives in memory-view.mjs, imported at the top of this file, rather than here.
// It was copied into two more suites when vixen-webgpu.js and vixen-audio.js got the same
// treatment, and three copies of the one thing that defines what "faithful" means is exactly how
// one of them drifts back into being permissive. That file carries the full explanation and the
// transcription of the runtime class it stands in for.

// ── Buffers: the way bytes cross an asynchronous boundary ────────────────────────────────────

const staged = platform.stageBuffer(new MemoryView(new Uint8Array([1, 2, 3, 4])));
const out = new Uint8Array(4);

equal(platform.bufferLength(staged), 4, "a staged buffer knows its length");
check(platform.readBuffer(staged, new MemoryView(out)), "a staged buffer reads back");
equal(out[3], 4, "…with its bytes intact through a MemoryView, which has no indexer");
equal(out[0], 1, "…from the first byte, not a correctly sized run of zeros");
platform.releaseBuffer(staged);
equal(platform.bufferLength(staged), 0, "and is gone once released");

check(
    !platform.readBuffer(staged, new MemoryView(new Uint8Array(4))),
    "reading a released buffer refuses rather than throwing"
);

// ── The other four view-taking functions, through the same stub ──────────────────────────────

const drain = new Float64Array(RECORD * 4);
fire(canvasElement, "keydown", { code: "KeyA", timeStamp: 1, repeat: false, getModifierState: () => false });
equal(platform.drainEvents(new MemoryView(drain)), 1, "drainEvents writes through a MemoryView");
equal(drain[2], 1, "…and the record reached the buffer");

const stride = platform.gamepadStride();
const pads = new Float64Array(stride * 4);

equal(platform.pollGamepads(new MemoryView(pads)), 2, "pollGamepads writes a record per port");

// The empty port, which is the branch that used to call view.fill().
equal(pads[0], 0, "…an empty port reports its slot");
equal(pads[1], 0, "…and reports itself disconnected");

// The occupied one, which is the branch that used to write through an indexer a MemoryView has
// not got — so with the old body every one of these read back as zero.
equal(pads[stride], 1, "…a connected pad reports its index");
equal(pads[stride + 1], 1, "…and reports itself connected");
equal(pads[stride + 2], 1, "…and its standard mapping");
equal(pads[stride + 3], 2, "…and how many buttons it has");
near(pads[stride + 4], 0.25, "…and its first axis, which is the value a stick actually sends");

const area = new Float64Array(4);
platform.onScreenKeyboardArea(new MemoryView(area));
equal(area.length, 4, "onScreenKeyboardArea writes four doubles without throwing on set()");

// ── The pasted image, which is the one view-taking function nothing covered ───────────────────
//
// ⚠ ImageData.data is a Uint8ClampedArray, and MemoryView.set compares constructors by identity —
// so `view.set(image.pixels)` throws `Assert failed: Expected function Uint8Array` rather than
// converting. That was a real defect, and until this case existed nothing in the repository would
// have seen it come back: readClipboardImage was the only MemoryView entry point across the three
// browser modules with no test at all.
//
// The route is the paste listener rather than a poke at module state, because decodeClipboardImage
// is what produces the clamped array in the first place. A stub that handed the module a
// Uint8Array would be testing the fix out of existence.

globalThis.createImageBitmap = async () => ({ width: 2, height: 2, close() { } });

// 2×2 RGBA, distinct in every channel so a run of zeros or an off-by-one row cannot pass.
const clamped = Uint8ClampedArray.of(
    10, 20, 30, 40,
    50, 60, 70, 80,
    90, 100, 110, 120,
    130, 140, 150, 160
);

globalThis.OffscreenCanvas = class {
    getContext() {
        return { drawImage() { }, getImageData: () => ({ data: clamped }) };
    }
};

platform.initialise();

fire(globalThis.document, "paste", {
    clipboardData: {
        types: ["text/plain"],
        getData: type => (type === "text/plain" ? "pasted" : ""),
        items: [{ type: "image/png", getAsFile: () => ({ name: "shot.png" }) }]
    }
});

// ⚠ A turn of the event loop, not a delay. decodeClipboardImage awaits createImageBitmap, so the
// rest of it runs as a microtask; setImmediate is queued behind every one of those. Nothing here
// is timed and nothing here would be slower on a loaded machine — it is an ordering, not a budget.
await new Promise(resolve => setImmediate(resolve));

equal(platform.clipboardText(), "pasted", "the paste carried its text");
equal(platform.clipboardImageWidth(), 2, "…and decoded the image it carried");
equal(platform.clipboardImageHeight(), 2, "…at its own height");

const pixels = new Uint8Array(16);

check(
    platform.readClipboardImage(new MemoryView(pixels)),
    "readClipboardImage writes through a MemoryView without throwing on the clamped source"
);

equal(pixels[0], 10, "…and the first pixel's red is the one the canvas held");
equal(pixels[3], 40, "…its alpha, so the channel order survived");
equal(pixels[15], 160, "…and the last byte of the last pixel, not a correctly sized run of zeros");

check(
    !platform.readClipboardImage(new MemoryView(new Uint8Array(15))),
    "…and a view one byte short is refused rather than half-filled"
);

// ── Screen ───────────────────────────────────────────────────────────────────────────────────

equal(platform.screenWidth(), 1920, "the screen's width");
equal(platform.screenAvailHeight(), 1040, "…and the work area, which is availHeight");
equal(platform.hardwareConcurrency(), 8, "the hardware count, which is a hint and not a thread count");
equal(platform.isCrossOriginIsolated(), false, "…and no isolation, so .NET has one thread");

// ── A parked view is a WINDOW, and what is stored has to be the window ────────────────────────
//
// ⚠ The header above says IndexedDB and fetch are deliberately not touched, and for the browser
// half of them that is still true — this stubs both, and it is not pretending to test either. What
// it tests is one line of arithmetic in writeDatabase that no browser is needed to get wrong.
//
// holdBuffer parks whatever typed array it is handed. stageBuffer hands it a slice(), which owns
// its whole ArrayBuffer, so `data.buffer` and the view were the same bytes and writeDatabase stored
// `data.buffer`. fetchRange hands it `bytes.subarray(offset, offset + length)` — a window with a
// non-zero byteOffset into a whole response body — and routing one of those into writeDatabase
// persisted the ENTIRE response under that path. No error, the right byte count returned to .NET,
// and the wrong bytes on disk. readBuffer was already immune because it spells the window out.
//
// So the fixture parks a real subarray the only way the module makes one, and writes THAT.

const responseBody = Uint8Array.of(
    0xF0, 0xF1, 0xF2, 0xF3,
    0xA0, 0xA1, 0xA2, 0xA3,
    0xE0, 0xE1, 0xE2, 0xE3
);

// ⚠ 200 rather than 206, which is the case fetchRange takes the subarray in: a server that ignores
// a Range header answers with the whole body, legally, and the slice is taken here.
globalThis.fetch = async () => ({
    ok: true,
    status: 200,
    statusText: "OK",
    arrayBuffer: async () => responseBody.buffer.slice(0)
});

// ── An IndexedDB stub, faithful in the one respect this asks about ────────────────────────────
//
// ⚠ put() structured-clones the record, exactly as IndexedDB does — a real store never hands back
// the object that went in. A stub that parked it by reference would answer the read with the
// caller's own typed array, and the defect this fixture exists to catch would be invisible through
// it: the whole 12-byte ArrayBuffer would still be *called* 4 bytes on the way out. There is no
// indexedDB in Node, so a run where this stub failed to install throws rather than passing.

const records = new Map();

function idbRequest(work) {
    const request = { result: undefined, error: null, onsuccess: null, onerror: null };

    queueMicrotask(() => {
        try {
            request.result = work();
            request.onsuccess?.();
        } catch (error) {
            request.error = error;
            request.onerror?.();
        }
    });

    return request;
}

const idbStore = {
    put(record) {
        return idbRequest(() => void records.set(record.path, structuredClone(record)));
    },
    get(path) {
        return idbRequest(() => structuredClone(records.get(path)));
    },
    delete(path) {
        return idbRequest(() => void records.delete(path));
    },
    openCursor() {
        const rows = [...records.values()];
        const request = { result: null, error: null, onsuccess: null, onerror: null };
        let index = 0;

        const step = () => queueMicrotask(() => {
            request.result = index < rows.length
                ? { value: structuredClone(rows[index++]), continue: step }
                : null;

            request.onsuccess?.();
        });

        step();
        return request;
    }
};

globalThis.indexedDB = {
    open() {
        const request = { result: null, error: null, onsuccess: null, onerror: null, onupgradeneeded: null };

        request.result = {
            objectStoreNames: { contains: () => false },
            createObjectStore() { },
            close() { },
            transaction: () => ({ objectStore: () => idbStore })
        };

        queueMicrotask(() => {
            request.onupgradeneeded?.();
            request.onsuccess?.();
        });

        return request;
    }
};

const parked = await platform.fetchRange("/whole.bin", 4, 4);

equal(platform.bufferLength(parked), 4, "a range a server answered whole is sliced down to the range");

const database = await platform.openDatabase("vixen-test");

equal(await platform.writeDatabase(database, "/range.bin", parked, 1234), 4, "writing it reports four bytes");
equal(await platform.listDatabase(database), 1, "the directory has the one key");

equal(
    platform.listingLength(0),
    4,
    "…⚠ and its length is the window's, not the whole response body's — this is the file size a "
    + "provider serves, so a wrong one here is a truncated read on the way back out"
);

const stored = await platform.readDatabase(database, "/range.bin");

equal(platform.bufferLength(stored), 4, "reading it back gives four bytes");

const bytes = new Uint8Array(4);

check(platform.readBuffer(stored, new MemoryView(bytes)), "…which read back through a MemoryView");
equal(bytes[0], 0xA0, "…starting at the range's first byte and not the response's");
equal(bytes[3], 0xA3, "…and ending at the range's last");

platform.releaseBuffer(stored);

check(await platform.deleteDatabase(database, "/range.bin"), "and the key deletes");
equal(await platform.listDatabase(database), 0, "…leaving the directory empty");

platform.closeDatabase(database);

console.log(`${passed} assertions passed`);
