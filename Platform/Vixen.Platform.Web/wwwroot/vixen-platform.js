// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0
//
// The browser half of Vixen.Platform.Web.
//
// ── One drain per frame, not one interop call per event ──────────────────────────────────────
//
// Every listener here writes a fixed-width record into a Float64Array ring. .NET copies the whole
// ring out once per PumpEvents through a single JSType.MemoryView call. The alternative — a .NET
// callback per DOM event — costs a marshalled delegate invocation for every mousemove, and a
// trackpad produces those at the display's refresh rate whether or not anything is listening.
//
// The record is twelve doubles because the slots are shared between kinds exactly as
// PlatformEvent's are, and because a double holds every field losslessly: coordinates are
// fractional, timestamps are milliseconds with sub-millisecond resolution, and key codes and
// device ids are small integers. One layout, no tagging, no per-event allocation.
//
// Strings cannot travel in a Float64Array, so a text-carrying event stores a *handle* in the value
// slot and .NET pulls the string with takeString(). Text events are rare — a keystroke, a drop —
// so the extra call per event is paid where it does not matter.
//
// ── The canvas is addressed by a number, not by a pointer ────────────────────────────────────
//
// SurfaceHandle is two nints and a discriminant, and a graphics backend gets nothing else: it does
// not reference Vixen.Platform, by the layer rule in docs/plan/00. So the canvas handle *is* the
// address, and the selector it maps to is derivable from the number alone:
//
//     [data-vixen-canvas="7"]
//
// which is what emscripten_webgl_create_context and canvas.getContext("webgpu") both want. The
// attribute is stamped on the element by createCanvas, never collides with the page's own ids, and
// means a backend can find the canvas knowing only the integer in the SurfaceHandle.

const RECORD = 12;

// Mirrors Vixen.Platform.PlatformEventKind. Values above 200 are this module's own and are
// translated by WebPlatform.PumpEvents into lifecycle calls rather than reaching an application.
const Kind = {
    windowShown: 1,
    windowHidden: 2,
    windowResized: 4,
    windowFocusGained: 5,
    windowFocusLost: 6,
    windowCloseRequested: 10,
    windowDpiChanged: 11,
    windowMouseEntered: 12,
    windowMouseLeft: 13,
    keyDown: 20,
    keyUp: 21,
    textInput: 22,
    textEditing: 23,
    mouseMoved: 30,
    mouseButtonDown: 31,
    mouseButtonUp: 32,
    mouseWheel: 33,
    touchDown: 40,
    touchMoved: 41,
    touchUp: 42,
    displaysChanged: 60,
    dropFile: 80,
    dropText: 81,

    // Internal.
    pageHidden: 200,
    pageVisible: 201,
    pageUnloading: 202,
    memoryPressure: 203
};

// Mirrors Vixen.Platform.KeyModifiers.
const Mod = {
    leftShift: 1 << 0,
    rightShift: 1 << 1,
    leftControl: 1 << 2,
    rightControl: 1 << 3,
    leftAlt: 1 << 4,
    rightAlt: 1 << 5,
    leftMeta: 1 << 6,
    rightMeta: 1 << 7,
    capsLock: 1 << 8,
    numLock: 1 << 9
};

const state = {
    queue: new Float64Array(RECORD * 256),
    count: 0,
    dropped: 0,
    strings: new Map(),
    nextString: 1,
    canvases: new Map(),
    nextCanvas: 1,
    // Modifier state is latched from the last event that carried it. A key event knows about
    // shiftKey/ctrlKey; a gamepad event knows nothing, and reporting "no modifiers held" there
    // would make a Ctrl-held gamepad binding impossible to write.
    modifiers: 0,
    frameHandle: 0,
    frameCallback: null,
    frameIntervals: [],
    lastFrameTime: 0,
    pointerLockCanvas: 0,
    textInput: null,
    textInputCanvas: 0,
    composing: false,
    clipboardText: "",
    clipboardImage: null,
    clipboardData: new Map(),
    battery: null,
    buffers: new Map(),
    nextBuffer: 1,
    databases: new Map(),
    nextDatabase: 1,
    listing: [],
    droppedFiles: []
};

// ── The ring ─────────────────────────────────────────────────────────────────────────────────

/** How many events the ring may hold before the *newest* are dropped. */
const MaxQueuedEvents = 8192;

function push(kind, windowId, timeStamp, modifiers, firstX, firstY, secondX, secondY, value, code, device) {
    if (state.count >= MaxQueuedEvents) {
        // Dropping the newest rather than the oldest, which is the opposite of what a ring
        // normally does and is right here: the oldest events are the ones with a matching release
        // still to come, and losing those is how a key gets stuck down. .NET reports the count.
        state.dropped++;
        return;
    }

    if ((state.count + 1) * RECORD > state.queue.length) {
        const grown = new Float64Array(Math.min(state.queue.length * 2, MaxQueuedEvents * RECORD));
        grown.set(state.queue);
        state.queue = grown;
    }

    let at = state.count * RECORD;
    const q = state.queue;

    q[at++] = kind;
    q[at++] = windowId;
    q[at++] = timeStamp;
    q[at++] = modifiers;
    q[at++] = firstX;
    q[at++] = firstY;
    q[at++] = secondX;
    q[at++] = secondY;
    q[at++] = value;
    q[at++] = code;
    q[at++] = device;
    q[at] = 0;

    state.count++;
}

/**
 * Copies whole records into .NET's buffer and keeps whatever did not fit for the next call.
 * Returns the number of records written; a caller that gets back its capacity calls again.
 */
export function drainEvents(view) {
    const capacity = Math.floor(view.length / RECORD);
    const taken = Math.min(capacity, state.count);

    if (taken === 0) {
        return 0;
    }

    view.set(state.queue.subarray(0, taken * RECORD), 0);

    const remaining = state.count - taken;

    if (remaining > 0) {
        state.queue.copyWithin(0, taken * RECORD, state.count * RECORD);
    }

    state.count = remaining;
    return taken;
}

/** How many events have been dropped because the ring was full, for the whole session. */
export function droppedEvents() {
    return state.dropped;
}

/**
 * The clock every event.timeStamp is measured against. .NET pairs one reading of this with one
 * Stopwatch.GetTimestamp() at boot, and converts from then on — which is what makes a
 * PlatformEvent's timestamp comparable with the rest of the engine's while keeping the browser's
 * own sub-millisecond ordering.
 */
export function now() {
    return performance.now();
}

/**
 * What the browser says the user's colour-scheme preference is: 2 for dark, 1 for light, 0 for no
 * preference.
 *
 * ⚠ Both queries are asked, and "neither matched" is a third answer rather than light. A browser
 * that does not implement the feature answers false to both, and reporting light there would make a
 * stylesheet's `(prefers-color-scheme: light)` block apply on a system that never said so.
 */
export function colorScheme() {
    if (!globalThis.matchMedia) {
        return 0;
    }

    if (globalThis.matchMedia("(prefers-color-scheme: dark)").matches) {
        return 2;
    }

    return globalThis.matchMedia("(prefers-color-scheme: light)").matches ? 1 : 0;
}

function holdString(text) {
    const handle = state.nextString++;
    state.strings.set(handle, text);
    return handle;
}

/** Takes a string an event referred to, and releases it. */
export function takeString(handle) {
    const text = state.strings.get(handle);
    state.strings.delete(handle);
    return text === undefined ? "" : text;
}

// ── Modifiers ────────────────────────────────────────────────────────────────────────────────

function modifiersOf(event) {
    let mask = 0;

    // The DOM says "shift is held" and not which one, except through getModifierState with a
    // location-qualified name, which no browser implements. Left is reported, because a binding
    // has to be told *something* and every platform's shortcut vocabulary means "either".
    if (event.shiftKey) mask |= Mod.leftShift;
    if (event.ctrlKey) mask |= Mod.leftControl;
    if (event.altKey) mask |= Mod.leftAlt;
    if (event.metaKey) mask |= Mod.leftMeta;

    if (typeof event.getModifierState === "function") {
        if (event.getModifierState("CapsLock")) mask |= Mod.capsLock;
        if (event.getModifierState("NumLock")) mask |= Mod.numLock;
    }

    // A KeyboardEvent knows which side it was, so a right-alt press corrects the guess above for
    // itself — which is the case that matters, because AltGr is right alt and a shortcut bound to
    // "alt" must not fire while a German keyboard types an @.
    if (event.code === "ShiftRight" && event.shiftKey) mask = (mask & ~Mod.leftShift) | Mod.rightShift;
    if (event.code === "ControlRight" && event.ctrlKey) mask = (mask & ~Mod.leftControl) | Mod.rightControl;
    if (event.code === "AltRight" && event.altKey) mask = (mask & ~Mod.leftAlt) | Mod.rightAlt;
    if (event.code === "MetaRight" && event.metaKey) mask = (mask & ~Mod.leftMeta) | Mod.rightMeta;

    state.modifiers = mask;
    return mask;
}

/** The modifiers held as of the last event that reported any. */
export function modifiers() {
    return state.modifiers;
}

// ── Canvases ─────────────────────────────────────────────────────────────────────────────────

/**
 * Adopts a canvas. `selector` may be null, in which case one is created and appended to the body.
 * Returns the handle, or 0 if the selector matched nothing or matched something that is not a
 * canvas.
 */
export function createCanvas(selector) {
    let element;

    if (selector) {
        element = document.querySelector(selector);

        if (!element || element.tagName !== "CANVAS") {
            return 0;
        }
    } else {
        element = document.createElement("canvas");
        element.style.width = "100%";
        element.style.height = "100%";
        element.style.display = "block";
        document.body.appendChild(element);
    }

    const handle = state.nextCanvas++;
    element.setAttribute("data-vixen-canvas", String(handle));

    // Focusable, or it never sees a key event. -1 keeps it out of the tab order, which is what a
    // full-page canvas wants and what a canvas embedded in a document does not — an application
    // that wants it tabbable sets tabIndex itself afterwards.
    if (!element.hasAttribute("tabindex")) {
        element.tabIndex = -1;
    }

    // The browser's own touch gestures — scroll, pinch-zoom, double-tap-zoom — fire before ours
    // and swallow the sequence halfway through, which shows up as a drag that stops moving.
    element.style.touchAction = "none";

    const canvas = {
        handle,
        element,
        listeners: [],
        observer: null,
        pointers: new Map(),
        lastPointer: null,
        clientSize: [0, 0],
        pixelSize: [0, 0],
        dpiScale: 0,
        cursor: "default",
        hidden: false
    };

    state.canvases.set(handle, canvas);
    attach(canvas);
    measure(canvas, true);

    return handle;
}

/** The CSS selector for a canvas handle. Derivable from the number alone, by design. */
export function canvasSelector(handle) {
    return `[data-vixen-canvas="${handle}"]`;
}

export function destroyCanvas(handle) {
    const canvas = state.canvases.get(handle);

    if (!canvas) {
        return;
    }

    for (const [target, type, listener] of canvas.listeners) {
        target.removeEventListener(type, listener);
    }

    canvas.observer?.disconnect();
    canvas.element.removeAttribute("data-vixen-canvas");
    state.canvases.delete(handle);
}

function on(canvas, target, type, listener, options) {
    target.addEventListener(type, listener, options);
    canvas.listeners.push([target, type, listener]);
}

function measure(canvas, force) {
    const rect = canvas.element.getBoundingClientRect();
    const ratio = globalThis.devicePixelRatio || 1;

    // Logical points from the layout box, physical pixels from the backing store. Deriving the
    // second from the first by multiplication is how a swapchain ends up one pixel out: the
    // browser rounds, and it is the browser's rounding that has to be honoured.
    const clientWidth = Math.max(1, Math.round(rect.width));
    const clientHeight = Math.max(1, Math.round(rect.height));
    const pixelWidth = Math.max(1, Math.round(rect.width * ratio));
    const pixelHeight = Math.max(1, Math.round(rect.height * ratio));

    const sizeChanged =
        clientWidth !== canvas.clientSize[0] ||
        clientHeight !== canvas.clientSize[1] ||
        pixelWidth !== canvas.pixelSize[0] ||
        pixelHeight !== canvas.pixelSize[1];

    const scaleChanged = ratio !== canvas.dpiScale;

    canvas.clientSize = [clientWidth, clientHeight];
    canvas.pixelSize = [pixelWidth, pixelHeight];
    canvas.dpiScale = ratio;

    if (sizeChanged || force) {
        // The backing store, which is what a WebGL or WebGPU context draws into. Setting it is
        // destructive — the browser clears the canvas — so it is only written when it changed.
        if (canvas.element.width !== pixelWidth) canvas.element.width = pixelWidth;
        if (canvas.element.height !== pixelHeight) canvas.element.height = pixelHeight;

        push(Kind.windowResized, canvas.handle, performance.now(), state.modifiers,
            clientWidth, clientHeight, pixelWidth, pixelHeight, 0, 0, 0);
    }

    if (scaleChanged && !force) {
        push(Kind.windowDpiChanged, canvas.handle, performance.now(), state.modifiers,
            0, 0, 0, 0, ratio, 0, 0);
    }
}

// ── Geometry, reported rather than remembered ────────────────────────────────────────────────

function sized(handle, index, which) {
    const canvas = state.canvases.get(handle);
    return canvas ? canvas[which][index] : 0;
}

export function clientWidth(handle) { return sized(handle, 0, "clientSize"); }
export function clientHeight(handle) { return sized(handle, 1, "clientSize"); }
export function pixelWidth(handle) { return sized(handle, 0, "pixelSize"); }
export function pixelHeight(handle) { return sized(handle, 1, "pixelSize"); }

export function dpiScale(handle) {
    const canvas = state.canvases.get(handle);
    return canvas ? canvas.dpiScale : (globalThis.devicePixelRatio || 1);
}

/**
 * A request, and a weak one. A canvas sized by CSS — the normal case, and what createCanvas makes
 * — is laid out by the page and this does nothing lasting; the ResizeObserver then reports what
 * the page decided, which is the number IWindow.ClientSize reads back.
 */
export function setClientSize(handle, width, height) {
    const canvas = state.canvases.get(handle);

    if (!canvas) {
        return;
    }

    canvas.element.style.width = `${width}px`;
    canvas.element.style.height = `${height}px`;
}

export function setVisible(handle, visible) {
    const canvas = state.canvases.get(handle);

    if (!canvas) {
        return;
    }

    canvas.element.style.visibility = visible ? "" : "hidden";
    canvas.hidden = !visible;
    push(visible ? Kind.windowShown : Kind.windowHidden, handle, performance.now(), state.modifiers, 0, 0, 0, 0, 0, 0, 0);
}

export function isVisible(handle) {
    const canvas = state.canvases.get(handle);
    return !!canvas && !canvas.hidden && document.visibilityState !== "hidden";
}

export function focus(handle) {
    state.canvases.get(handle)?.element.focus();
}

export function isFocused(handle) {
    const canvas = state.canvases.get(handle);
    return !!canvas && document.activeElement === canvas.element;
}

export function setTitle(title) {
    document.title = title;
}

// ── Fullscreen ───────────────────────────────────────────────────────────────────────────────

/**
 * A request that only succeeds inside a user gesture. Everywhere else the promise rejects and the
 * page stays as it is, which is why nothing here reports success: the answer arrives later, as a
 * fullscreenchange, and isFullscreen is what tells the truth about it.
 */
export function requestFullscreen(handle) {
    const canvas = state.canvases.get(handle);
    const element = canvas?.element;

    if (!element) {
        return;
    }

    const request = element.requestFullscreen || element.webkitRequestFullscreen;
    request?.call(element).catch(() => { });
}

export function exitFullscreen() {
    const exit = document.exitFullscreen || document.webkitExitFullscreen;
    exit?.call(document).catch(() => { });
}

export function isFullscreen(handle) {
    const canvas = state.canvases.get(handle);
    const current = document.fullscreenElement || document.webkitFullscreenElement;
    return !!canvas && current === canvas.element;
}

// ── Cursor ───────────────────────────────────────────────────────────────────────────────────

export function setCursor(handle, css) {
    const canvas = state.canvases.get(handle);

    if (canvas) {
        canvas.cursor = css;
        canvas.element.style.cursor = css;
    }
}

/** Relative mouse mode. Like fullscreen, only granted inside a user gesture. */
export function requestPointerLock(handle) {
    const canvas = state.canvases.get(handle);

    if (!canvas) {
        return;
    }

    state.pointerLockCanvas = handle;
    const request = canvas.element.requestPointerLock;
    const result = request?.call(canvas.element);
    result?.catch?.(() => { });
}

export function exitPointerLock() {
    state.pointerLockCanvas = 0;
    document.exitPointerLock?.();
}

export function isPointerLocked(handle) {
    const canvas = state.canvases.get(handle);
    return !!canvas && document.pointerLockElement === canvas.element;
}

// ── Listeners ────────────────────────────────────────────────────────────────────────────────

// offsetX/offsetY, not clientX minus a bounding rect. They are already the position inside the
// target's padding box, and reading them costs nothing — whereas getBoundingClientRect() forces the
// browser to flush layout, in a listener that fires at the display's refresh rate. TouchEvent has no
// offsetX, which is why the touch listeners do take the rect, once per event rather than per finger.
function pointInCanvas(event) {
    return [event.offsetX, event.offsetY];
}

// PointerEvent.button, which is left/middle/right/back/forward by *position*, onto Vixen's
// MouseButton, which is Primary/Secondary/Middle by *role*. The browser has already applied the
// user's left-handed swap by the time the event arrives, so 0 is the button under the index
// finger whichever hand that is.
const Buttons = [1, 3, 2, 4, 5];

function attach(canvas) {
    const element = canvas.element;

    // ── Pointer: mouse, pen and touch through one interface ──────────────────────────────────
    //
    // Touches are *also* delivered as pointer events, so a single set of listeners would report a
    // finger twice — once as a pointer and once as a touch. Non-mouse pointers are ignored here
    // and handled by the touch listeners below, which is the pair that carries pressure and a
    // stable identifier.

    on(canvas, element, "pointerdown", event => {
        if (event.pointerType !== "mouse") {
            return;
        }

        // Capture, so a drag that leaves the canvas still delivers its release. Without it a
        // button pressed inside and released outside stays down forever.
        element.setPointerCapture?.(event.pointerId);
        element.focus?.({ preventScroll: true });

        const [x, y] = pointInCanvas(event);
        canvas.lastPointer = [x, y];

        push(Kind.mouseButtonDown, canvas.handle, event.timeStamp, modifiersOf(event),
            x, y, 0, 0, 0, Buttons[event.button] ?? 0, Math.max(1, event.detail || 1));
    });

    on(canvas, element, "pointerup", event => {
        if (event.pointerType !== "mouse") {
            return;
        }

        element.releasePointerCapture?.(event.pointerId);
        const [x, y] = pointInCanvas(event);
        canvas.lastPointer = [x, y];

        push(Kind.mouseButtonUp, canvas.handle, event.timeStamp, modifiersOf(event),
            x, y, 0, 0, 0, Buttons[event.button] ?? 0, Math.max(1, event.detail || 1));
    });

    on(canvas, element, "pointermove", event => {
        if (event.pointerType !== "mouse") {
            return;
        }

        const [x, y] = pointInCanvas(event);

        // Under pointer lock the position is frozen and movementX/Y is the real device motion,
        // which is the entire point of the mode. Differencing positions there reads the lock, not
        // the mouse.
        const locked = document.pointerLockElement === element;
        const previous = canvas.lastPointer;

        const dx = locked ? (event.movementX || 0) : (previous ? x - previous[0] : 0);
        const dy = locked ? (event.movementY || 0) : (previous ? y - previous[1] : 0);

        canvas.lastPointer = [x, y];

        push(Kind.mouseMoved, canvas.handle, event.timeStamp, modifiersOf(event), x, y, dx, dy, 0, 0, 0);
    });

    on(canvas, element, "pointerenter", event => {
        if (event.pointerType === "mouse") {
            push(Kind.windowMouseEntered, canvas.handle, event.timeStamp, state.modifiers, 0, 0, 0, 0, 0, 0, 0);
        }
    });

    on(canvas, element, "pointerleave", event => {
        if (event.pointerType === "mouse") {
            canvas.lastPointer = null;
            push(Kind.windowMouseLeft, canvas.handle, event.timeStamp, state.modifiers, 0, 0, 0, 0, 0, 0, 0);
        }
    });

    // The context menu is the browser's, and over a game canvas it is in the way. Suppressed so
    // that a right-click is a right-click; an application that wants the menu removes the canvas's
    // handler itself.
    on(canvas, element, "contextmenu", event => event.preventDefault());

    // ── Wheel ────────────────────────────────────────────────────────────────────────────────

    on(canvas, element, "wheel", event => {
        // deltaMode is the browser's unit and differs between browsers for the same gesture:
        // pixels on a trackpad and in Chrome, lines in Firefox, pages when a page-scroll key is
        // involved. Vixen's contract is notches, positive up and right, so all three are converted
        // here rather than by every caller.
        const scale = event.deltaMode === 1 ? 1 / 3 : event.deltaMode === 2 ? 1 : 1 / 100;
        const [x, y] = pointInCanvas(event);

        // The same field says which kind of device produced it, and it says so in one direction
        // only. Lines and pages are units no continuous surface reports, so a non-pixel deltaMode
        // is a notched wheel; pixels are what a trackpad reports *and* what Chrome reports for a
        // wheel, so the pixel case is an absence of evidence rather than evidence of a trackpad.
        // PlatformEvent.IsNotched is documented on exactly those terms.
        const notched = event.deltaMode !== 0 ? 1 : 0;

        push(Kind.mouseWheel, canvas.handle, event.timeStamp, modifiersOf(event),
            x, y, -event.deltaX * scale, -event.deltaY * scale, 0, notched, 0);

        // Otherwise the page scrolls under the game. Only when the canvas has focus, so a canvas
        // embedded in a document does not trap the reader's scroll wheel.
        if (document.activeElement === element) {
            event.preventDefault();
        }
    }, { passive: false });

    // ── Touch ────────────────────────────────────────────────────────────────────────────────
    //
    // TouchEvent rather than PointerEvent, for `force` and for `Touch.identifier`, which is stable
    // for the life of a finger. .NET's TouchTracker turns that into the small dense id the event
    // stream promises.

    const touch = (kind, event) => {
        const rect = element.getBoundingClientRect();

        for (const point of event.changedTouches) {
            push(kind, canvas.handle, event.timeStamp, state.modifiers,
                point.clientX - rect.left, point.clientY - rect.top,
                0, 0, point.force || 1, 0, point.identifier);
        }

        event.preventDefault();
    };

    on(canvas, element, "touchstart", event => {
        element.focus?.({ preventScroll: true });
        touch(Kind.touchDown, event);
    }, { passive: false });

    on(canvas, element, "touchmove", event => touch(Kind.touchMoved, event), { passive: false });
    on(canvas, element, "touchend", event => touch(Kind.touchUp, event), { passive: false });

    // A cancel is the browser taking the gesture — a system edge swipe, a call. Reported as an up
    // so that whatever was being dragged is let go of; an application never told a finger left
    // keeps drawing the line it was drawing.
    on(canvas, element, "touchcancel", event => touch(Kind.touchUp, event), { passive: false });

    // ── Keyboard ─────────────────────────────────────────────────────────────────────────────

    on(canvas, element, "keydown", event => {
        push(Kind.keyDown, canvas.handle, event.timeStamp, modifiersOf(event),
            0, 0, 0, 0, 0, keyOf(event), event.repeat ? 1 : 0);

        // Tab moves focus out of the canvas and F-keys open browser UI. Suppressed while the
        // canvas has focus, except for the ones a user needs to keep: reload, devtools, and the
        // whole set once a modifier is held, because Ctrl+W closing the tab is not ours to take.
        if (shouldSuppress(event)) {
            event.preventDefault();
        }
    });

    on(canvas, element, "keyup", event => {
        push(Kind.keyUp, canvas.handle, event.timeStamp, modifiersOf(event),
            0, 0, 0, 0, 0, keyOf(event), 0);
    });

    // ── Focus ────────────────────────────────────────────────────────────────────────────────

    on(canvas, element, "focus", () =>
        push(Kind.windowFocusGained, canvas.handle, performance.now(), state.modifiers, 0, 0, 0, 0, 0, 0, 0));

    on(canvas, element, "blur", () => {
        // The modifier latch is cleared here on purpose. A user who alt-tabs away releases alt
        // somewhere the page never hears about, and a latch that kept it set would leave every
        // subsequent click looking alt-held.
        state.modifiers = 0;
        push(Kind.windowFocusLost, canvas.handle, performance.now(), 0, 0, 0, 0, 0, 0, 0, 0);
    });

    // ── Drag and drop ────────────────────────────────────────────────────────────────────────
    //
    // A dropped file is not a path: the browser gives a File object and no location on disk, and
    // there is nothing honest to put in PlatformEvent.Text's "native path". So the *name* is what
    // the event reports, and the File itself is parked in droppedFiles in the same order the
    // events were queued, for .NET to read the bytes out of — which is the only form in which a
    // browser will ever hand them over.

    on(canvas, element, "dragover", event => event.preventDefault());

    on(canvas, element, "drop", event => {
        event.preventDefault();
        const [x, y] = pointInCanvas(event);

        for (const file of event.dataTransfer?.files ?? []) {
            state.droppedFiles.push(file);
            push(Kind.dropFile, canvas.handle, event.timeStamp, state.modifiers,
                x, y, 0, 0, holdString(file.name), 0, 0);
        }

        const text = event.dataTransfer?.getData("text/plain");

        if (text) {
            push(Kind.dropText, canvas.handle, event.timeStamp, state.modifiers, x, y, 0, 0, holdString(text), 0, 0);
        }
    });

    // ── Resize and scale ─────────────────────────────────────────────────────────────────────

    if (globalThis.ResizeObserver) {
        canvas.observer = new ResizeObserver(() => measure(canvas, false));
        canvas.observer.observe(element);
    } else {
        on(canvas, globalThis, "resize", () => measure(canvas, false));
    }

    // devicePixelRatio changes with browser zoom and with a move to another monitor, and there is
    // no event for it — only a media query that stops matching. Re-armed each time it fires,
    // because the query names the ratio it was created with.
    watchScale(canvas);

    on(canvas, document, "fullscreenchange", () => measure(canvas, false));
}

function watchScale(canvas) {
    if (!globalThis.matchMedia) {
        return;
    }

    const ratio = globalThis.devicePixelRatio || 1;
    const query = globalThis.matchMedia(`(resolution: ${ratio}dppx)`);

    const listener = () => {
        query.removeEventListener("change", listener);

        if (state.canvases.has(canvas.handle)) {
            measure(canvas, false);
            watchScale(canvas);
        }
    };

    query.addEventListener("change", listener);
}

function shouldSuppress(event) {
    if (event.ctrlKey || event.metaKey) {
        return false;
    }

    // F5, F11 and F12 stay the browser's: reload, fullscreen and devtools are how a user gets out
    // of a page that has gone wrong, and a game is not entitled to take them.
    return event.code === "Tab"
        || event.code === "Space"
        || event.code === "Backspace"
        || event.code.startsWith("Arrow")
        || (event.code.startsWith("F") && !["F5", "F11", "F12"].includes(event.code));
}

// ── Keyboard.code → USB HID usage ────────────────────────────────────────────────────────────
//
// Both vocabularies name the *physical position*, which is why this is a table and not a guess:
// KeyboardEvent.code is defined by UI Events in terms of the same US-QWERTY legends the HID
// keyboard page uses, so "KeyQ" is HID 20 on an AZERTY keyboard even though it is labelled A.

const Keys = {
    KeyA: 4, KeyB: 5, KeyC: 6, KeyD: 7, KeyE: 8, KeyF: 9, KeyG: 10, KeyH: 11, KeyI: 12,
    KeyJ: 13, KeyK: 14, KeyL: 15, KeyM: 16, KeyN: 17, KeyO: 18, KeyP: 19, KeyQ: 20, KeyR: 21,
    KeyS: 22, KeyT: 23, KeyU: 24, KeyV: 25, KeyW: 26, KeyX: 27, KeyY: 28, KeyZ: 29,

    Digit1: 30, Digit2: 31, Digit3: 32, Digit4: 33, Digit5: 34,
    Digit6: 35, Digit7: 36, Digit8: 37, Digit9: 38, Digit0: 39,

    Enter: 40, Escape: 41, Backspace: 42, Tab: 43, Space: 44,
    Minus: 45, Equal: 46, BracketLeft: 47, BracketRight: 48, Backslash: 49,
    Semicolon: 51, Quote: 52, Backquote: 53, Comma: 54, Period: 55, Slash: 56, CapsLock: 57,

    F1: 58, F2: 59, F3: 60, F4: 61, F5: 62, F6: 63,
    F7: 64, F8: 65, F9: 66, F10: 67, F11: 68, F12: 69,

    PrintScreen: 70, ScrollLock: 71, Pause: 72, Insert: 73, Home: 74, PageUp: 75,
    Delete: 76, End: 77, PageDown: 78,
    ArrowRight: 79, ArrowLeft: 80, ArrowDown: 81, ArrowUp: 82,

    NumLock: 83, NumpadDivide: 84, NumpadMultiply: 85, NumpadSubtract: 86, NumpadAdd: 87,
    NumpadEnter: 88,
    Numpad1: 89, Numpad2: 90, Numpad3: 91, Numpad4: 92, Numpad5: 93,
    Numpad6: 94, Numpad7: 95, Numpad8: 96, Numpad9: 97, Numpad0: 98, NumpadDecimal: 99,

    IntlBackslash: 100, ContextMenu: 101,

    F13: 104, F14: 105, F15: 106, F16: 107, F17: 108, F18: 109,
    F19: 110, F20: 111, F21: 112, F22: 113, F23: 114, F24: 115,

    ControlLeft: 224, ShiftLeft: 225, AltLeft: 226, MetaLeft: 227,
    ControlRight: 228, ShiftRight: 229, AltRight: 230, MetaRight: 231,

    // Key.Back — Android's hardware back button and the browser's back gesture, which arrives on a
    // keyboard as BrowserBack. Outside the HID page, hence the value above every real usage code.
    BrowserBack: 512
};

function keyOf(event) {
    return Keys[event.code] ?? 0;
}

// ── The frame loop ───────────────────────────────────────────────────────────────────────────

/**
 * requestAnimationFrame, which is the only correct clock in a browser: it runs at the display's
 * rate whatever that is, it is throttled to nothing in a hidden tab, and it is the point at which
 * the compositor will actually take a frame. A setInterval loop renders frames nobody sees.
 */
export function startFrameLoop(callback) {
    if (state.frameHandle) {
        return;
    }

    state.frameCallback = callback;
    state.lastFrameTime = 0;

    const tick = time => {
        if (state.lastFrameTime > 0) {
            // Kept for the refresh-rate estimate, which is the only way to find out whether this
            // is a 60 Hz or a 120 Hz display: the browser does not say, on purpose.
            state.frameIntervals.push(time - state.lastFrameTime);

            if (state.frameIntervals.length > 120) {
                state.frameIntervals.shift();
            }
        }

        state.lastFrameTime = time;
        state.frameHandle = requestAnimationFrame(tick);
        state.frameCallback?.(time);
    };

    state.frameHandle = requestAnimationFrame(tick);
}

export function stopFrameLoop() {
    if (state.frameHandle) {
        cancelAnimationFrame(state.frameHandle);
        state.frameHandle = 0;
        state.frameCallback = null;
    }
}

/** The display's refresh rate, measured. Zero until enough frames have gone by to be sure. */
export function refreshRate() {
    if (state.frameIntervals.length < 10) {
        return 0;
    }

    // The median, not the mean: a single 300 ms hitch — a garbage collection, a shader compile —
    // would drag a mean far enough to report 40 Hz on a 60 Hz display.
    const sorted = [...state.frameIntervals].sort((a, b) => a - b);
    const median = sorted[sorted.length >> 1];
    return median > 0 ? 1000 / median : 0;
}

// ── Lifecycle ────────────────────────────────────────────────────────────────────────────────

function attachDocumentListeners() {
    document.addEventListener("visibilitychange", () => {
        push(document.visibilityState === "hidden" ? Kind.pageHidden : Kind.pageVisible,
            0, performance.now(), state.modifiers, 0, 0, 0, 0, 0, 0, 0);
    });

    // pagehide, not beforeunload. beforeunload is unreliable on mobile — a tab discarded under
    // memory pressure never fires it — and it is the event browsers are progressively restricting.
    // pagehide fires in the cases that actually happen, including bfcache.
    globalThis.addEventListener("pagehide", () => {
        push(Kind.pageUnloading, 0, performance.now(), state.modifiers, 0, 0, 0, 0, 0, 0, 0);
    });

    // The one memory signal a page gets, and only in Chromium. `performance.memory` is a
    // heuristic and this is not: the browser is telling us it is about to discard the tab.
    globalThis.addEventListener("freeze", () => {
        push(Kind.memoryPressure, 0, performance.now(), state.modifiers, 0, 0, 0, 0, 2, 0, 0);
    });

    if (globalThis.matchMedia) {
        globalThis.matchMedia("(dynamic-range: high)").addEventListener?.("change", () =>
            push(Kind.displaysChanged, 0, performance.now(), state.modifiers, 0, 0, 0, 0, 0, 0, 0));
    }

    globalThis.addEventListener("resize", () =>
        push(Kind.displaysChanged, 0, performance.now(), state.modifiers, 0, 0, 0, 0, 0, 0, 0));

    // ── Clipboard ────────────────────────────────────────────────────────────────────────────
    //
    // A page may only read the clipboard from inside a paste gesture, so what IClipboard serves is
    // what the last paste delivered — which its documentation says is the only thing a browser
    // will ever let it have, and is why that interface is synchronous.

    document.addEventListener("paste", event => {
        const data = event.clipboardData;

        if (!data) {
            return;
        }

        state.clipboardText = data.getData("text/plain") ?? "";
        state.clipboardData.clear();

        for (const type of data.types ?? []) {
            if (type !== "Files") {
                state.clipboardData.set(type, data.getData(type) ?? "");
            }
        }

        for (const item of data.items ?? []) {
            if (item.type?.startsWith("image/")) {
                decodeClipboardImage(item.getAsFile());
                break;
            }
        }
    });
}

async function decodeClipboardImage(file) {
    if (!file || !globalThis.createImageBitmap) {
        return;
    }

    try {
        const bitmap = await createImageBitmap(file);
        const surface = new OffscreenCanvas(bitmap.width, bitmap.height);
        const context = surface.getContext("2d");
        context.drawImage(bitmap, 0, 0);

        const image = context.getImageData(0, 0, bitmap.width, bitmap.height);
        state.clipboardImage = { width: bitmap.width, height: bitmap.height, pixels: image.data };
        bitmap.close();
    } catch {
        state.clipboardImage = null;
    }
}

export function clipboardText() { return state.clipboardText; }
export function hasClipboardText() { return state.clipboardText.length > 0; }

export function setClipboardText(text) {
    if (!navigator.clipboard?.writeText) {
        return false;
    }

    // Asynchronous, and the permission answer arrives too late to report. What is returned is
    // "this browser has the API and we asked it", not "the clipboard now holds this" — which is
    // the strongest true statement available from a synchronous call.
    navigator.clipboard.writeText(text).catch(() => { });
    state.clipboardText = text;
    return true;
}

export function clipboardImageWidth() { return state.clipboardImage?.width ?? 0; }
export function clipboardImageHeight() { return state.clipboardImage?.height ?? 0; }

export function readClipboardImage(view) {
    const image = state.clipboardImage;

    if (!image || view.length < image.pixels.length) {
        return false;
    }

    // ⚠ Re-wrapped as a Uint8Array. ImageData.data is a Uint8ClampedArray, and MemoryView.set
    // compares constructors exactly — the clamped variant throws `Assert failed: Expected function
    // Uint8Array` rather than converting. Same buffer, no copy.
    view.set(new Uint8Array(image.pixels.buffer, image.pixels.byteOffset, image.pixels.byteLength));
    return true;
}

export function clipboardData(format) {
    return state.clipboardData.get(format) ?? "";
}

export function hasClipboardData(format) {
    return state.clipboardData.has(format);
}

export function clearClipboard() {
    state.clipboardText = "";
    state.clipboardImage = null;
    state.clipboardData.clear();
    navigator.clipboard?.writeText?.("").catch(() => { });
}

// ── Text input and the IME ───────────────────────────────────────────────────────────────────
//
// A canvas cannot host an IME. What can is a real editable element, so text input puts an
// invisible one over the caret, focuses it, and reads composition events off it. Invisible rather
// than absent: an element with display:none or zero size gets no IME at all in Safari, and one
// parked off-screen makes the candidate window appear in the corner — which is the bug this exists
// to avoid.

function ensureTextInput() {
    if (state.textInput) {
        return state.textInput;
    }

    const input = document.createElement("input");
    input.type = "text";
    input.autocapitalize = "off";
    input.autocomplete = "off";
    input.spellcheck = false;
    input.setAttribute("aria-hidden", "true");

    Object.assign(input.style, {
        position: "fixed",
        left: "0px",
        top: "0px",
        width: "1px",
        height: "1px",
        padding: "0",
        border: "none",
        outline: "none",
        opacity: "0",
        background: "transparent",
        color: "transparent",
        caretColor: "transparent",
        zIndex: "2147483647"
    });

    input.addEventListener("compositionstart", () => {
        state.composing = true;
    });

    input.addEventListener("compositionupdate", event => {
        const text = event.data ?? "";
        // The caret sits at the end of the pre-edit string: no browser reports where inside a
        // composition it actually is, and the end is where every IME puts it in practice.
        push(Kind.textEditing, state.textInputCanvas, performance.now(), state.modifiers,
            0, 0, 0, 0, holdString(text), text.length, 0);
    });

    input.addEventListener("compositionend", event => {
        state.composing = false;
        push(Kind.textEditing, state.textInputCanvas, performance.now(), state.modifiers,
            0, 0, 0, 0, holdString(""), 0, 0);

        if (event.data) {
            push(Kind.textInput, state.textInputCanvas, performance.now(), state.modifiers,
                0, 0, 0, 0, holdString(event.data), 0, 0);
        }

        input.value = "";
    });

    input.addEventListener("input", event => {
        // While composing, `input` fires for every pre-edit change and its data is not committed
        // text; compositionend is what commits. Emitting here as well is how a Japanese user gets
        // every intermediate reading typed into the field.
        if (state.composing || event.isComposing) {
            return;
        }

        if (input.value) {
            push(Kind.textInput, state.textInputCanvas, performance.now(), state.modifiers,
                0, 0, 0, 0, holdString(input.value), 0, 0);
        }

        input.value = "";
    });

    // Keys still have to reach the game while a text field is open — Escape closes the chat box,
    // Enter sends it — and the canvas is not focused, so they are forwarded from here.
    input.addEventListener("keydown", event => {
        push(Kind.keyDown, state.textInputCanvas, event.timeStamp, modifiersOf(event),
            0, 0, 0, 0, 0, keyOf(event), event.repeat ? 1 : 0);
    });

    input.addEventListener("keyup", event => {
        push(Kind.keyUp, state.textInputCanvas, event.timeStamp, modifiersOf(event),
            0, 0, 0, 0, 0, keyOf(event), 0);
    });

    document.body.appendChild(input);
    state.textInput = input;
    return input;
}

export function activateTextInput(handle) {
    const canvas = state.canvases.get(handle);

    if (!canvas) {
        return false;
    }

    state.textInputCanvas = handle;
    const input = ensureTextInput();
    input.value = "";
    input.focus({ preventScroll: true });
    return true;
}

export function deactivateTextInput() {
    state.composing = false;

    if (state.textInput) {
        state.textInput.blur();
        state.textInput.value = "";
    }

    const canvas = state.canvases.get(state.textInputCanvas);
    canvas?.element.focus({ preventScroll: true });
    state.textInputCanvas = 0;
}

/** Puts the invisible field where the caret is, so the candidate window opens under it. */
export function setCandidateArea(handle, x, y, width, height) {
    const canvas = state.canvases.get(handle);
    const input = state.textInput;

    if (!canvas || !input) {
        return;
    }

    const rect = canvas.element.getBoundingClientRect();

    Object.assign(input.style, {
        left: `${rect.left + x}px`,
        top: `${rect.top + y}px`,
        width: `${Math.max(1, width)}px`,
        height: `${Math.max(1, height)}px`
    });
}

/**
 * Whether this browser puts a keyboard on screen. Coarse-pointer-and-no-hover is the standard
 * test and it is what CSS itself uses; there is no API that answers the question directly.
 */
export function hasOnScreenKeyboard() {
    return !!globalThis.matchMedia?.("(pointer: coarse)").matches;
}

/**
 * Where the on-screen keyboard is, as four numbers written into the caller's buffer: x, y, width,
 * height in CSS pixels relative to the viewport, all zero when nothing is covered.
 *
 * VirtualKeyboard is Chromium-only and needs overlaysContent set; visualViewport is the portable
 * approximation and is what everything else gets. Both are real measurements — no guess is made
 * from window heights, which is the trick that misreports split-screen and hardware keyboards.
 */
export function onScreenKeyboardArea(view) {
    if (view.length < 4) {
        return false;
    }

    // ⚠ Float64Array and not an array literal. MemoryView.set compares the source's constructor
    // against the view's exactly, so `view.set([x, y, w, h])` throws
    // `Assert failed: Expected function Float64Array` — it does not convert. Every call here used
    // to be an array literal, so ITextInput.OnScreenKeyboardArea threw on every path it took.
    const write = (x, y, width, height) => view.set(Float64Array.of(x, y, width, height), 0);

    const keyboard = navigator.virtualKeyboard;

    if (keyboard?.boundingRect) {
        const rect = keyboard.boundingRect;
        write(rect.x, rect.y, rect.width, rect.height);
        return rect.width > 0 && rect.height > 0;
    }

    const viewport = globalThis.visualViewport;

    if (!viewport || !state.textInput || document.activeElement !== state.textInput) {
        write(0, 0, 0, 0);
        return false;
    }

    const covered = globalThis.innerHeight - (viewport.height + viewport.offsetTop);

    if (covered <= 1) {
        write(0, 0, 0, 0);
        return false;
    }

    write(0, globalThis.innerHeight - covered, globalThis.innerWidth, covered);
    return true;
}

export function isTextInputActive() {
    return !!state.textInput && document.activeElement === state.textInput;
}

// ── Gamepads ─────────────────────────────────────────────────────────────────────────────────
//
// The Gamepad API is polled, not evented: navigator.getGamepads() returns a snapshot and there is
// no way to be told a button changed. So the snapshot crosses to .NET once per frame and the diff
// that turns it into events happens there, where it can be tested without a browser and without a
// physical pad.

/** Axes and buttons per gamepad record, matching the standard mapping's 4 and 17 with room. */
const GamepadAxes = 8;
const GamepadButtons = 24;
const GamepadRecord = 4 + GamepadAxes + GamepadButtons;

export function gamepadStride() {
    return GamepadRecord;
}

/**
 * Writes one record per slot: [index, connected, mapping, buttonCount, axes…, buttons…].
 * Returns how many records were written.
 */
export function pollGamepads(view) {
    const pads = navigator.getGamepads?.() ?? [];
    const capacity = Math.floor(view.length / GamepadRecord);
    let written = 0;

    // ⚠ Staged in a real Float64Array and handed over with one set(), because `view` is a .NET
    // MemoryView and NOT a typed array — see the note above readBuffer. This function used to
    // write through `view[at] = …`, which silently set properties on a plain object and reached
    // WebAssembly memory with nothing at all, and then called view.fill(), which does not exist:
    // `TypeError: view.fill is not a function`, thrown out of the first PumpEvents of the first
    // frame, caught by WebFrameLoop, which stopped the loop. The whole frame loop was dead on
    // frame one on every browser build, and no gate could see it until `nuke BrowserSmoke`.
    const staged = new Float64Array(capacity * GamepadRecord);

    for (let slot = 0; slot < pads.length && written < capacity; slot++) {
        const pad = pads[slot];
        let at = written * GamepadRecord;

        if (!pad) {
            staged[at] = slot;
            staged[at + 1] = 0;
            staged.fill(0, at + 2, at + GamepadRecord);
            written++;
            continue;
        }

        staged[at++] = pad.index;
        staged[at++] = 1;
        staged[at++] = pad.mapping === "standard" ? 1 : 0;
        staged[at++] = Math.min(pad.buttons.length, GamepadButtons);

        for (let axis = 0; axis < GamepadAxes; axis++) {
            staged[at + axis] = axis < pad.axes.length ? pad.axes[axis] : 0;
        }

        at += GamepadAxes;

        for (let button = 0; button < GamepadButtons; button++) {
            staged[at + button] = button < pad.buttons.length ? pad.buttons[button].value : 0;
        }

        written++;
    }

    if (written > 0) {
        view.set(staged.subarray(0, written * GamepadRecord), 0);
    }

    return written;
}

export function gamepadName(index) {
    const pad = (navigator.getGamepads?.() ?? [])[index];
    return pad ? pad.id : "";
}

/**
 * Rumble, where the pad and the browser both have it. Dual-rumble is the only effect the
 * Gamepad Extensions define; trigger motors are not exposed to a page at all.
 */
export function rumble(index, weak, strong, milliseconds) {
    const pad = (navigator.getGamepads?.() ?? [])[index];
    const actuator = pad?.vibrationActuator;

    if (!actuator?.playEffect) {
        return false;
    }

    actuator.playEffect("dual-rumble", {
        duration: milliseconds,
        strongMagnitude: strong,
        weakMagnitude: weak
    }).catch(() => { });

    return true;
}

export function stopRumble(index) {
    const pad = (navigator.getGamepads?.() ?? [])[index];
    pad?.vibrationActuator?.reset?.().catch(() => { });
}

export function hasRumble(index) {
    const pad = (navigator.getGamepads?.() ?? [])[index];
    return !!pad?.vibrationActuator?.playEffect;
}

// ── Screen, processors, power ────────────────────────────────────────────────────────────────

export function screenWidth() { return screen.width; }
export function screenHeight() { return screen.height; }
export function screenAvailWidth() { return screen.availWidth; }
export function screenAvailHeight() { return screen.availHeight; }

export function isHdr() {
    return !!globalThis.matchMedia?.("(dynamic-range: high)").matches;
}

export function hardwareConcurrency() {
    return navigator.hardwareConcurrency || 1;
}

/**
 * Whether the page has SharedArrayBuffer, and therefore whether .NET threads exist here at all.
 * It is the COOP/COEP headers that decide, which is a deployment fact the engine can only read
 * and never arrange.
 */
export function isCrossOriginIsolated() {
    return !!globalThis.crossOriginIsolated;
}

export function deviceMemory() {
    return navigator.deviceMemory || 0;
}

// Not exported: `initialise` is the only caller and it is in this file. An `export` here would
// claim a [JSImport] surface that does not exist, which InteropSurfaceTests now refuses.
function startBatteryWatch() {
    // getBattery() is gone from Firefox and Safari, on purpose: it is a fingerprinting surface.
    // Absent means "this browser will not say", which IPowerInfo already models as null.
    navigator.getBattery?.().then(battery => {
        state.battery = battery;
    }).catch(() => { });
}

export function hasBattery() { return !!state.battery; }
export function batteryLevel() { return state.battery ? state.battery.level : -1; }
export function batteryCharging() { return !!state.battery?.charging; }

export function batteryDischargingTime() {
    const seconds = state.battery?.dischargingTime;
    return seconds === undefined || !isFinite(seconds) ? -1 : seconds;
}

export function openUrl(url) {
    // noopener, always: without it the opened page gets a handle to this one through
    // window.opener and can navigate it somewhere else.
    return !!globalThis.open(url, "_blank", "noopener,noreferrer");
}

// ── Buffers: the way bytes come back from an asynchronous call ───────────────────────────────
//
// A [JSImport] promise can resolve with a number but not with a memory view — the view would
// outlive the call it was valid for. So an async read parks its bytes here, resolves with the
// length, and .NET copies them out with a synchronous readBuffer() and releases them.

function holdBuffer(bytes) {
    const handle = state.nextBuffer++;
    state.buffers.set(handle, bytes);
    return handle;
}

export function bufferLength(handle) {
    const bytes = state.buffers.get(handle);
    return bytes ? bytes.byteLength : 0;
}

// ── ⚠ A `view` parameter is a .NET MemoryView, which is NOT a typed array ────────────────────
//
// Every function below that takes a `view` is handed one by the marshaller for a
// [JSMarshalAs<JSType.MemoryView>] Span<T>, and its whole surface is FOUR members:
//
//     set(source, offset)   source must be a typed array of EXACTLY the matching constructor —
//                           Uint8Array for Span<byte>, Float64Array for Span<double>. A plain
//                           Array, or a Uint8ClampedArray for a byte span, throws
//                           `Assert failed: Expected function Uint8Array`.
//     copyTo(target, from)  the other direction, same constructor rule.
//     slice(start, end)     returns a real typed array holding a COPY.
//     length / byteLength
//
// There is no indexer and no fill. `view[i] = x` sets a property on a plain object and reaches
// WebAssembly memory with nothing; `view.fill(…)` is a TypeError; and passing a view where an
// array-like is expected — `someTypedArray.set(view)` — reads `undefined` at every index and
// writes a buffer of zeros that is exactly the right length.
//
// ⚠ All four of those mistakes were in this file, and every one of them survived every gate this
// repository has: the compiler sees a declaration, PublishWeb sees a file, BrowserModuleUrlTests
// sees a constant, and js/vixen-platform.test.mjs hands these functions REAL typed arrays, which
// support all four operations. Only calling them from the runtime finds it, which is what
// `nuke BrowserSmoke` is for and what it found on its first run against a real head.

export function readBuffer(handle, view) {
    const bytes = state.buffers.get(handle);

    if (!bytes || view.length < bytes.byteLength) {
        return false;
    }

    view.set(new Uint8Array(bytes.buffer ?? bytes, bytes.byteOffset ?? 0, bytes.byteLength));
    return true;
}

export function releaseBuffer(handle) {
    state.buffers.delete(handle);
}

/**
 * Parks a copy of the caller's bytes and returns a handle to them.
 *
 * The other direction of the same problem: a memory view is only valid for the call it was passed
 * to, so an *asynchronous* write cannot take one — the marshaller rejects the combination outright
 * rather than letting it look like it works. Staging synchronously and putting asynchronously is
 * the shape that survives, and the copy it costs is one IndexedDB would make anyway.
 */
export function stageBuffer(view) {
    // ⚠ slice(), not `new Uint8Array(view.length)` followed by `bytes.set(view)`. A MemoryView is
    // not array-like: `set` walked it with an indexer it does not have, read `undefined` at every
    // position, and stored a buffer of ZEROS of exactly the right length. So every write through
    // IndexedDbFileProvider stored the correct number of bytes and none of the correct ones, and
    // the round trip that would have shown it — write, read back, compare — is the check
    // `nuke BrowserSmoke` added. slice() copies out of WebAssembly memory itself and cannot be
    // wrong in this way.
    return holdBuffer(view.slice(0, view.length));
}

// ── Dropped files ────────────────────────────────────────────────────────────────────────────
//
// Parked in arrival order, matching the order of the dropFile events in the same drain. The event
// carries the name because that is all PlatformEvent has room for and all a UI needs to decide
// whether it wants the file; the bytes are asked for separately, and asynchronously, because
// reading a File is a Promise and always was.

export function droppedFileCount() {
    return state.droppedFiles.length;
}

export function droppedFileName(index) {
    return state.droppedFiles[index]?.name ?? "";
}

export function droppedFileLength(index) {
    return state.droppedFiles[index]?.size ?? 0;
}

/** The bytes of a dropped file, which is the only form a browser hands one over in. */
export async function readDroppedFile(index) {
    const file = state.droppedFiles[index];
    return file ? holdBuffer(new Uint8Array(await file.arrayBuffer())) : 0;
}

/**
 * Releases the first `count` parked Files — the ones .NET took last frame. Counted rather than
 * emptied, because a drop that happened between the two pumps is already in the array and clearing
 * the whole thing would throw it away before anybody saw it.
 */
export function clearDroppedFiles(count) {
    if (count > 0) {
        state.droppedFiles.splice(0, count);
    }
}

// ── fetch, with range requests ───────────────────────────────────────────────────────────────

/**
 * Fetches a byte range. Returns a buffer handle, or throws so that .NET sees a JSException with
 * the status in it.
 *
 * A server that ignores Range answers 200 with the whole body rather than 206 with the slice —
 * which is legal, and which a client that trusts the request would then misread as the range it
 * asked for. The slice is taken here when that happens, so the caller gets what it asked for
 * either way and pays only in bandwidth.
 */
export async function fetchRange(url, offset, length) {
    const headers = length > 0 ? { Range: `bytes=${offset}-${offset + length - 1}` } : undefined;
    const response = await fetch(url, { headers, cache: "default" });

    if (!response.ok) {
        throw new Error(`${response.status} ${response.statusText} for ${url}`);
    }

    let bytes = new Uint8Array(await response.arrayBuffer());

    if (length > 0 && response.status !== 206 && bytes.byteLength > length) {
        bytes = bytes.subarray(offset, offset + length);
    }

    return holdBuffer(bytes);
}

/** Fetches a whole resource. */
export async function fetchAll(url) {
    return await fetchRange(url, 0, 0);
}

/** HEAD, for a length and a last-modified without the body. Returns [length, lastModifiedMs]. */
export async function fetchHead(url) {
    const response = await fetch(url, { method: "HEAD" });

    if (!response.ok) {
        return holdBuffer(new Uint8Array(0));
    }

    const length = Number(response.headers.get("Content-Length") ?? 0);
    const modified = Date.parse(response.headers.get("Last-Modified") ?? "") || 0;

    return holdBuffer(new Float64Array([length, modified]));
}

/** Whether the server advertises byte ranges for a resource, which decides whether to stream it. */
export async function supportsRanges(url) {
    try {
        const response = await fetch(url, { method: "HEAD" });
        return response.ok && (response.headers.get("Accept-Ranges") ?? "").includes("bytes");
    } catch {
        return false;
    }
}

// ── IndexedDB ────────────────────────────────────────────────────────────────────────────────
//
// The only storage a browser gives that is large, durable and not swept by the cache eviction that
// takes Cache Storage first. localStorage is 5 MB of strings; the Origin Private File System is
// the better answer and is not in Safari on iOS, which is where the storage limits bite hardest.
//
// Everything here is asynchronous and IFileProvider's metadata half is not, so the *directory* —
// every key with its length and write time — is read once at open and kept in memory. Values are
// read and written on demand. That is what makes Exists() and Enumerate() answerable without
// blocking, which on the browser's one thread is not a preference.

const StoreName = "files";

export function openDatabase(name) {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(name, 1);

        request.onupgradeneeded = () => {
            const db = request.result;

            if (!db.objectStoreNames.contains(StoreName)) {
                db.createObjectStore(StoreName, { keyPath: "path" });
            }
        };

        request.onsuccess = () => {
            const handle = state.nextDatabase++;
            state.databases.set(handle, request.result);
            resolve(handle);
        };

        request.onerror = () => reject(request.error ?? new Error(`Cannot open IndexedDB '${name}'.`));
    });
}

export function closeDatabase(handle) {
    state.databases.get(handle)?.close();
    state.databases.delete(handle);
}

function transact(handle, mode) {
    const db = state.databases.get(handle);

    if (!db) {
        throw new Error("The database handle is not open.");
    }

    return db.transaction(StoreName, mode).objectStore(StoreName);
}

function awaited(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error ?? new Error("The IndexedDB request failed."));
    });
}

/**
 * Reads every key with its length and write time, and parks it for listingName/Length/Time. The
 * values are deliberately not read: a cache of downloaded bundles is hundreds of megabytes, and
 * the point of the directory is to answer questions about it without being it.
 */
export async function listDatabase(handle) {
    const store = transact(handle, "readonly");
    const listing = [];

    await new Promise((resolve, reject) => {
        const request = store.openCursor();

        request.onsuccess = () => {
            const cursor = request.result;

            if (!cursor) {
                resolve();
                return;
            }

            const record = cursor.value;
            listing.push([record.path, record.data?.byteLength ?? 0, record.modified ?? 0]);
            cursor.continue();
        };

        request.onerror = () => reject(request.error ?? new Error("Listing the store failed."));
    });

    state.listing = listing;
    return listing.length;
}

export function listingName(index) { return state.listing[index]?.[0] ?? ""; }
export function listingLength(index) { return state.listing[index]?.[1] ?? 0; }
export function listingTime(index) { return state.listing[index]?.[2] ?? 0; }

/** Reads one value. Returns a buffer handle, or 0 if there is no such key. */
export async function readDatabase(handle, path) {
    const record = await awaited(transact(handle, "readonly").get(path));
    return record?.data ? holdBuffer(new Uint8Array(record.data)) : 0;
}

/**
 * Writes one value from a buffer staged by stageBuffer(), and releases the buffer. Staged rather
 * than passed as a view, because a view cannot cross into an asynchronous call — see stageBuffer.
 */
export async function writeDatabase(handle, path, bufferHandle, modified) {
    const data = state.buffers.get(bufferHandle) ?? new Uint8Array(0);
    state.buffers.delete(bufferHandle);

    await awaited(transact(handle, "readwrite").put({ path, data: data.buffer, modified }));
    return data.byteLength;
}

export async function deleteDatabase(handle, path) {
    await awaited(transact(handle, "readwrite").delete(path));
    return true;
}

/**
 * How much room the origin has been given and how much of it is gone, as [usage, quota]. A cache
 * that writes until it is refused is a cache that gets the whole origin evicted.
 */
export async function storageEstimate() {
    const estimate = await navigator.storage?.estimate?.() ?? {};
    return holdBuffer(new Float64Array([estimate.usage ?? 0, estimate.quota ?? 0]));
}

/**
 * Asks for storage that the browser will not evict on its own. Granted silently in Chromium when
 * the site is installed or frequently used, prompted for in Firefox, refused in Safari.
 */
export async function persistStorage() {
    return !!(await navigator.storage?.persist?.());
}

// ── Lazy assemblies ──────────────────────────────────────────────────────────────────────────

/**
 * Fetches an assembly the publish step held back out of the boot manifest. Returns a buffer
 * handle for .NET to hand to AssemblyLoadContext.
 */
export async function fetchAssembly(url) {
    const response = await fetch(url, { cache: "default" });

    if (!response.ok) {
        throw new Error(`${response.status} ${response.statusText} for ${url}`);
    }

    return holdBuffer(new Uint8Array(await response.arrayBuffer()));
}

// ── Boot ─────────────────────────────────────────────────────────────────────────────────────

let started = false;

/** Attaches the document-wide listeners. Called once, by WebPlatform's constructor. */
export function initialise() {
    if (started) {
        return;
    }

    started = true;
    attachDocumentListeners();
    startBatteryWatch();
}
