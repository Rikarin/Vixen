// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// ── ⚠ A faithful MemoryView, because a typed array is NOT one ────────────────────────────────
//
// What the .NET marshaller passes for a `[JSMarshalAs<JSType.MemoryView>] Span<T>` is an instance
// of the runtime's own `Span` class, and its ENTIRE surface is five members. This is that class,
// transcribed from the shipped runtime — `class or` in
// `Microsoft.NETCore.App.Runtime.Mono.browser-wasm/<version>/runtimes/browser-wasm/native/dotnet.runtime.js`,
// which `Span` extends:
//
//     set(source, offset)   `if (!source || source.constructor !== view.constructor) throw`
//     copyTo(target, from)  the same constructor check, the other direction
//     slice(start, end)     `this._unsafe_create_view().slice(start, end)` — a real typed array
//     length                the element count
//     byteLength            length, <<2 for int, <<3 for double
//
// and three data properties: `_pointer`, `_length`, `_viewType`.
//
// ⚠ There is NO indexer and NO fill. `view[i] = x` sets a property on an ordinary JavaScript
// object and reaches WebAssembly memory with nothing at all; `view.fill(…)` is a TypeError; there
// is no `.buffer` and no `.byteOffset`, so `new DataView(view.buffer, …)` throws
// `TypeError: First argument to DataView constructor must be an ArrayBuffer`; and passing a view
// where an array-like is expected — `someTypedArray.set(view)` — reads `undefined` at every index
// and writes a run of ZEROS of exactly the right length.
//
// ── Why this file exists at all ──────────────────────────────────────────────────────────────
//
// Every one of those mistakes has been in this repository, and each survived every gate it has.
// `nuke BrowserSmoke` found four of them in vixen-platform.js on its first run against a real
// head: pollGamepads wrote through the indexer and then called view.fill(), throwing out of the
// first PumpEvents of the first frame and stopping the frame loop on frame one of every browser
// build; stageBuffer stored a buffer of zeros of exactly the right length, so every IndexedDB
// write was silently empty; two more passed set() a source of the wrong constructor.
//
// ⚠ They survived because the suite that covered them handed those functions REAL TYPED ARRAYS,
// which support all four operations. A test double more permissive than the thing it doubles
// proves nothing while looking thorough — so the double is here, in ONE place, rather than
// copied into each suite where the copies would drift apart.
//
// ⚠ And it does not need a browser. The four defects this double catches in vixen-webgpu.js are
// not WebGPU defects: `new DataView(view.buffer, …)` throws before a single GPU call is reached,
// so a WebGPU adapter — which the issue said was needed before any of it could be fixed — is not
// what stands between them and a red test.

/**
 * The marshaller's view onto WebAssembly memory, over a typed array standing in for the heap.
 *
 * The constructor check is by identity, exactly as the runtime does it: a `Uint8ClampedArray`
 * offered to a byte view throws rather than converting, which is a real defect this caught.
 */
export class MemoryView {
    constructor(typed) {
        this.typed = typed;

        // The runtime's three, so anything reading them off a view sees what it would really see:
        // a pointer that is not an ArrayBuffer, and no byteOffset at all.
        this._pointer = 0;
        this._length = typed.length;
        this._viewType = typed instanceof Uint8Array ? 0 : typed instanceof Int32Array ? 1 : 2;
    }

    set(source, offset = 0) {
        if (!source || source.constructor !== this.typed.constructor) {
            throw new Error(`Assert failed: Expected ${this.typed.constructor}`);
        }

        this.typed.set(source, offset);
    }

    copyTo(target, from = 0) {
        if (!target || target.constructor !== this.typed.constructor) {
            throw new Error(`Assert failed: Expected ${this.typed.constructor}`);
        }

        target.set(this.typed.subarray(from));
    }

    slice(start, end) {
        return this.typed.slice(start, end);
    }

    get length() {
        return this._length;
    }

    get byteLength() {
        return this._viewType === 0 ? this._length : this._viewType === 1 ? this._length << 2 : this._length << 3;
    }
}

// ── ⚠ Verifying the instrument ───────────────────────────────────────────────────────────────
//
// This was added because sabotaging the double proved it was NOT covered. Every suite that uses
// it stayed green when `set` stopped checking the source's constructor, and stayed green when the
// double was given a `.buffer` — the two properties that are the entire reason it exists. So the
// suites were testing the modules through an instrument nothing tested, which is the same shape
// as the original defect one level up: a double more permissive than the runtime proves nothing
// while looking thorough.
//
// ⚠ A permissive double is not a failing test. It is a PASSING one that has stopped asking the
// question, which is why this throws at import time rather than counting assertions: a suite must
// not be able to run at all against a double that has drifted.

/** Fails loudly if the double has stopped refusing what the runtime refuses. */
function selfCheck() {
    const view = new MemoryView(new Uint8Array(4));

    const refuses = (what, body) => {
        try {
            body();
        } catch {
            return;
        }

        throw new Error(
            `memory-view.mjs is no longer faithful: it accepted ${what}, which the .NET runtime `
            + "rejects. Every suite using it is now proving less than it appears to. See the "
            + "class comment above."
        );
    };

    // The constructor check, in both directions. These are the two that caught real defects:
    // readClipboardImage passed a Uint8ClampedArray, onScreenKeyboardArea an array literal.
    refuses("a plain Array in set()", () => view.set([1, 2, 3, 4], 0));
    refuses("a Uint8ClampedArray in set()", () => view.set(Uint8ClampedArray.of(1, 2, 3, 4), 0));
    refuses("a Float64Array in set()", () => view.set(Float64Array.of(1), 0));
    refuses("a mismatched target in copyTo()", () => view.copyTo(new Float64Array(4), 0));

    // ⚠ And the absences, which cannot be checked by catching: a MemoryView is an ordinary object,
    // so reading a member it does not have yields `undefined` rather than throwing. That silence
    // IS the defect — `new Float32Array(undefined, …)` builds an empty array and swallows every
    // write — so the double must not have them.
    for (const absent of ["buffer", "byteOffset", "fill", "subarray"]) {
        if (view[absent] !== undefined) {
            throw new Error(
                `memory-view.mjs is no longer faithful: it has a '${absent}', which a .NET `
                + "MemoryView does not. Reading one off the real thing yields undefined, and that "
                + "is precisely the mistake these suites exist to catch."
            );
        }
    }

    // slice() must yield a REAL typed array, because that is the only member the fixed code can
    // build a DataView over. A double returning another view would make the fix look unnecessary.
    if (!(view.slice(0, 4) instanceof Uint8Array)) {
        throw new Error("memory-view.mjs is no longer faithful: slice() must return a typed array.");
    }
}

selfCheck();
