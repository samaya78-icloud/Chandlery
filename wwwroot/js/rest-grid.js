// The quarter-hour grid for one day of work and rest. Tap a cell to flip it; press and drag to paint a stretch the way the first cell was set.
// The page owns the truth: this only turns cells into work periods ("04:00-08:00,16:00-20:00") and reports them back when a stroke ends.

const SLOT = 15;
const SLOTS = 96;
const states = new WeakMap();

const pad = n => String(n).padStart(2, "0");
const clock = minute => `${pad(Math.floor(minute / 60))}:${pad(minute % 60)}`;

function cells(root) { return root.querySelectorAll("[data-slot]"); }

function toSpans(root) {
    const spans = [];
    let start = -1;
    const list = cells(root);
    for (let i = 0; i <= SLOTS; i++) {
        const on = i < SLOTS && list[i].classList.contains("work");
        if (on && start < 0) start = i;
        if (!on && start >= 0) { spans.push(`${clock(start * SLOT)}-${clock(i * SLOT)}`); start = -1; }
    }
    return spans.join(",");
}

function fromSpans(root, text) {
    const on = new Array(SLOTS).fill(false);
    for (const part of (text || "").split(/[,;]/)) {
        const m = /^\s*(\d{1,2}):?(\d{2})\s*-\s*(\d{1,2}):?(\d{2})\s*$/.exec(part);
        if (!m) continue;
        const from = (+m[1] * 60 + +m[2]) / SLOT, to = (+m[3] * 60 + +m[4]) / SLOT;
        for (let s = Math.max(0, from); s < Math.min(SLOTS, to); s++) on[s] = true;
    }
    cells(root).forEach((c, i) => c.classList.toggle("work", on[i]));
}

function slotAt(x, y) {
    const el = document.elementFromPoint(x, y);
    const cell = el && el.closest ? el.closest("[data-slot]") : null;
    return cell && cell.dataset.slot !== undefined ? cell : null;
}

export function init(root, dotnet, spans, readOnly) {
    dispose(root);
    fromSpans(root, spans);
    const state = { dotnet, readOnly, paint: null, last: spans || "" };
    states.set(root, state);
    if (readOnly) return;

    // A quick swipe skips cells between two pointer events, so every cell from the last one reached to this one is painted.
    const paintTo = cell => {
        const to = +cell.dataset.slot;
        const from = state.at ?? to;
        const list = cells(root);
        for (let s = Math.min(from, to); s <= Math.max(from, to); s++) list[s].classList.toggle("work", state.paint);
        state.at = to;
    };
    const down = e => {
        const cell = slotAt(e.clientX, e.clientY);
        if (!cell || (e.button !== undefined && e.button > 0)) return;
        state.paint = !cell.classList.contains("work");
        state.at = null;
        paintTo(cell);
        root.setPointerCapture?.(e.pointerId);
        e.preventDefault();
    };
    const move = e => {
        if (state.paint === null) return;
        const cell = slotAt(e.clientX, e.clientY);
        if (cell) paintTo(cell);
    };
    const up = () => {
        if (state.paint === null) return;
        state.paint = null;
        state.at = null;
        const text = toSpans(root);
        if (text !== state.last) { state.last = text; state.dotnet.invokeMethodAsync("OnGridChanged", text); }
    };
    root.addEventListener("pointerdown", down);
    root.addEventListener("pointermove", move);
    root.addEventListener("pointerup", up);
    root.addEventListener("pointercancel", up);
    state.off = () => {
        root.removeEventListener("pointerdown", down);
        root.removeEventListener("pointermove", move);
        root.removeEventListener("pointerup", up);
        root.removeEventListener("pointercancel", up);
    };
}

/** The page changed the day (another date, a prefill, clear): show it without telling the page back. */
export function set(root, spans, readOnly) {
    const state = states.get(root);
    if (!state) return;
    if ((spans || "") === state.last && readOnly === state.readOnly) return;
    state.last = spans || "";
    fromSpans(root, spans);
}

export function dispose(root) {
    const state = states.get(root);
    if (state?.off) state.off();
    states.delete(root);
}
