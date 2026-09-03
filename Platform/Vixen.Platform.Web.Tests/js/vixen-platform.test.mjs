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
const buffer = new Float64Array(RECORD * 64);

/** Kind, to keep the assertions readable. Mirrors PlatformEventKind. */
const Kind = {
    windowResized: 4, keyDown: 20, keyUp: 21,
    mouseButtonDown: 31, mouseWheel: 33, touchDown: 40, dropFile: 80, dropText: 81
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

let taken = platform.drainEvents(buffer);

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

taken = platform.drainEvents(buffer);

equal(taken, 2, "two key events");
equal(buffer[0], Kind.keyDown, "a key down");
equal(buffer[9], 20, "KeyQ is HID 20 — the position, not the letter an AZERTY keyboard prints there");
equal(buffer[3], 1, "shift is reported as the left one");
equal(buffer[2], 12.5, "the browser's own timestamp survives, sub-millisecond");
equal(buffer[RECORD + 0], Kind.keyUp, "a key up");
equal(buffer[RECORD + 9], 230, "AltRight is HID 230");
equal(buffer[RECORD + 3], 1 << 5, "…and corrects the modifier mask to the right-hand bit, which is AltGr");

fire(canvasElement, "keydown", { code: "Unidentified", timeStamp: 1, getModifierState: () => false });
platform.drainEvents(buffer);
equal(buffer[9], 0, "a key with no HID position is Key.Unknown, for .NET to drop");

// ── Wheel: three units, one contract ─────────────────────────────────────────────────────────

fire(canvasElement, "wheel", { deltaMode: 1, deltaX: 0, deltaY: -3, offsetX: 10, offsetY: 20, timeStamp: 1 });
fire(canvasElement, "wheel", { deltaMode: 0, deltaX: 0, deltaY: -100, offsetX: 10, offsetY: 20, timeStamp: 2 });
fire(canvasElement, "wheel", { deltaMode: 2, deltaX: 0, deltaY: -1, offsetX: 10, offsetY: 20, timeStamp: 3 });

taken = platform.drainEvents(buffer);

equal(taken, 3, "three wheel events");
equal(buffer[0], Kind.mouseWheel, "a wheel event");
near(buffer[7], 1, "three lines up is one notch, positive up (Firefox)");
near(buffer[RECORD + 7], 1, "a hundred pixels up is one notch (Chrome, trackpad)");
near(buffer[2 * RECORD + 7], 1, "one page up is one notch");

// ── Mouse: role, not side; offset, not a layout read ─────────────────────────────────────────

fire(canvasElement, "pointerdown", {
    pointerType: "mouse", button: 2, offsetX: 5, offsetY: 6, detail: 2, timeStamp: 3, pointerId: 1
});

taken = platform.drainEvents(buffer);

equal(taken, 1, "a mouse button");
equal(buffer[0], Kind.mouseButtonDown, "…down");
equal(buffer[9], 2, "PointerEvent.button 2 is MouseButton.Secondary");
equal(buffer[10], 2, "the OS's own click count, not one we derived from timestamps");
equal(buffer[4], 5, "the position comes from offsetX");
equal(buffer[5], 6, "…and offsetY");

fire(canvasElement, "pointerdown", {
    pointerType: "touch", button: 0, offsetX: 1, offsetY: 1, timeStamp: 4, pointerId: 2
});

equal(platform.drainEvents(buffer), 0, "a touch is not also reported as a pointer, or every finger arrives twice");

// ── Touch: the browser's identifier and its pressure ─────────────────────────────────────────

fire(canvasElement, "touchstart", {
    changedTouches: [{ identifier: 99, clientX: 30, clientY: 40, force: 0.5 }], timeStamp: 4
});

taken = platform.drainEvents(buffer);

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

taken = platform.drainEvents(buffer);

equal(taken, 2, "a dropped file and the text that came with it");
equal(buffer[0], Kind.dropFile, "the file first");
equal(platform.takeString(buffer[8]), "level.vxb", "the file's name, by handle");
equal(platform.takeString(buffer[8]), "", "…and the handle is released once taken");
equal(buffer[RECORD + 0], Kind.dropText, "then the text");
equal(platform.takeString(buffer[RECORD + 8]), "hello", "the dropped text");

equal(platform.droppedFileCount(), 1, "the File itself is parked, because a browser gives no path");
equal(platform.droppedFileName(0), "level.vxb", "…under the same index as the event's order");
platform.clearDroppedFiles(1);
equal(platform.droppedFileCount(), 0, "and released when .NET has taken it");

// ── A drain smaller than the queue keeps the rest, in order ──────────────────────────────────

for (let index = 0; index < 10; index++) {
    fire(canvasElement, "keydown", { code: "KeyA", timeStamp: index, repeat: false, getModifierState: () => false });
}

const small = new Float64Array(RECORD * 4);
const seen = [];

do {
    taken = platform.drainEvents(small);

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

// ── The other three view-taking functions, through the same stub ─────────────────────────────

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

// ── Screen ───────────────────────────────────────────────────────────────────────────────────

equal(platform.screenWidth(), 1920, "the screen's width");
equal(platform.screenAvailHeight(), 1040, "…and the work area, which is availHeight");
equal(platform.hardwareConcurrency(), 8, "the hardware count, which is a hint and not a thread count");
equal(platform.isCrossOriginIsolated(), false, "…and no isolation, so .NET has one thread");

console.log(`${passed} assertions passed`);
