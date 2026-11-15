// The tank plan: press a tank and drag it to a new place, or drag one of its edges or corners to resize it; let go to drop it there.
// The plan is a grid of columns and rows (one cell is 10 SVG units). A press that never moves is an ordinary click and is left alone.
// The page owns the truth: this only shows the change while the pointer is down, then reports the cells it was dropped on.

const states = new WeakMap();
const CELL = 10;
const THRESHOLD = 4;   // screen pixels before a press counts as a drag, so a click still selects
const EDGE = 6;        // thickness of an edge handle, in SVG units
const CORNER = 10;     // size of a corner handle

function toPlan(svg, e) {
    const pt = svg.createSVGPoint();
    pt.x = e.clientX; pt.y = e.clientY;
    const p = pt.matrixTransform(svg.getScreenCTM().inverse());
    return { x: p.x, y: p.y };
}

/** The handle rectangles of a tank at (x, y) with size (w, h), in SVG units. Keep in step with TankPlanPage.HandleRects. */
export function handleRects(x, y, w, h) {
    const X = x * CELL, Y = y * CELL, W = w * CELL, H = h * CELL;
    const c = Math.min(CORNER, W / 2, H / 2);
    const ew = Math.max(W - 2 * c, 0), eh = Math.max(H - 2 * c, 0);
    return {
        nw: [X, Y, c, c], ne: [X + W - c, Y, c, c], sw: [X, Y + H - c, c, c], se: [X + W - c, Y + H - c, c, c],
        n: [X + c, Y - EDGE / 2, ew, EDGE], s: [X + c, Y + H - EDGE / 2, ew, EDGE],
        w: [X - EDGE / 2, Y + c, EDGE, eh], e: [X + W - EDGE / 2, Y + c, EDGE, eh],
    };
}

/** Draws one tank (its box, its code and its handles) at the given cells. */
function layout(g, x, y, w, h) {
    const rect = g.querySelector("rect.tank-body");
    if (rect) { rect.setAttribute("x", x * CELL); rect.setAttribute("y", y * CELL); rect.setAttribute("width", w * CELL); rect.setAttribute("height", h * CELL); }
    const text = g.querySelector("text");
    if (text) { text.setAttribute("x", x * CELL + w * CELL / 2); text.setAttribute("y", y * CELL + h * CELL / 2 + 5); }
    const rects = handleRects(x, y, w, h);
    g.querySelectorAll("[data-handle]").forEach(el => {
        const r = rects[el.dataset.handle];
        if (!r) return;
        el.setAttribute("x", r[0]); el.setAttribute("y", r[1]); el.setAttribute("width", r[2]); el.setAttribute("height", r[3]);
    });
}

const cellsOf = g => ({ x: +g.dataset.x, y: +g.dataset.y, w: +g.dataset.w, h: +g.dataset.h });

export function init(svg, dotnet, columns, rows) {
    dispose(svg);
    const state = { drag: null, suppressClick: false };
    states.set(svg, state);
    const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

    const place = (d, e) => {
        const p = toPlan(svg, e);
        if (d.mode === "move") {
            d.x = clamp(Math.round((p.x - d.offX) / CELL), 0, columns - d.w);
            d.y = clamp(Math.round((p.y - d.offY) / CELL), 0, rows - d.h);
            d.g.setAttribute("transform", `translate(${(d.x - d.x0) * CELL} ${(d.y - d.y0) * CELL})`);
            return;
        }
        // resize: the edge being dragged follows the pointer to the nearest grid line; the opposite edge stays put
        const gx = clamp(Math.round(p.x / CELL), 0, columns), gy = clamp(Math.round(p.y / CELL), 0, rows);
        let left = d.x0, top = d.y0, right = d.x0 + d.w0, bottom = d.y0 + d.h0;
        if (d.mode.includes("w")) left = Math.min(gx, right - 1);
        if (d.mode.includes("e")) right = Math.max(gx, left + 1);
        if (d.mode.includes("n")) top = Math.min(gy, bottom - 1);
        if (d.mode.includes("s")) bottom = Math.max(gy, top + 1);
        [d.x, d.y, d.w, d.h] = [left, top, right - left, bottom - top];
        layout(d.g, d.x, d.y, d.w, d.h);
    };

    const down = e => {
        if (e.button > 0) return;
        const target = e.target.closest ? e.target : null;
        const g = target?.closest("[data-tank]");
        if (!g || g.dataset.movable !== "true") return;
        const handle = target.closest("[data-handle]");
        const { x, y, w, h } = cellsOf(g);
        const p = toPlan(svg, e);
        state.drag = {
            g, id: g.dataset.tank, mode: handle ? handle.dataset.handle : "move",
            x0: x, y0: y, w0: w, h0: h, x, y, w, h,
            offX: p.x - x * CELL, offY: p.y - y * CELL, sx: e.clientX, sy: e.clientY, moved: false,
        };
    };
    const move = e => {
        const d = state.drag;
        if (!d) return;
        if (!d.moved) {
            if (Math.hypot(e.clientX - d.sx, e.clientY - d.sy) < THRESHOLD) return;
            d.moved = true;
            svg.setPointerCapture?.(e.pointerId);
            d.g.classList.add("dragging");
            svg.classList.add("dragging");
            svg.dataset.mode = d.mode;
        }
        place(d, e);
        e.preventDefault();
    };
    const finish = cancelled => {
        const d = state.drag;
        if (!d) return;
        state.drag = null;
        svg.classList.remove("dragging");
        delete svg.dataset.mode;
        d.g.classList.remove("dragging");
        if (!d.moved) return;
        state.suppressClick = true;           // the click that follows a drag must not select the tank
        setTimeout(() => { state.suppressClick = false; }, 0);
        const unchanged = d.x === d.x0 && d.y === d.y0 && d.w === d.w0 && d.h === d.h0;
        if (cancelled || unchanged) { d.g.removeAttribute("transform"); layout(d.g, d.x0, d.y0, d.w0, d.h0); return; }
        dotnet.invokeMethodAsync("OnTankPlaced", d.id, d.x, d.y, d.w, d.h);   // the page re-renders the tank at its saved place
    };
    const up = () => finish(false);
    const cancel = () => finish(true);
    const click = e => { if (state.suppressClick) { e.stopPropagation(); e.preventDefault(); } };
    const key = e => { if (e.key === "Escape" && state.drag?.moved) finish(true); };

    svg.addEventListener("pointerdown", down);
    svg.addEventListener("pointermove", move);
    svg.addEventListener("pointerup", up);
    svg.addEventListener("pointercancel", cancel);
    svg.addEventListener("click", click, true);
    document.addEventListener("keydown", key);
    state.off = () => {
        svg.removeEventListener("pointerdown", down);
        svg.removeEventListener("pointermove", move);
        svg.removeEventListener("pointerup", up);
        svg.removeEventListener("pointercancel", cancel);
        svg.removeEventListener("click", click, true);
        document.removeEventListener("keydown", key);
    };
}

/** The page re-drew the plan (a tank saved or refused): drop any leftover drag offset and draw every tank where the page says it is. */
export function reset(svg) {
    svg.querySelectorAll("[data-tank]").forEach(g => {
        g.removeAttribute("transform");
        const { x, y, w, h } = cellsOf(g);
        layout(g, x, y, w, h);
    });
}

export function dispose(svg) {
    const state = states.get(svg);
    if (state?.off) state.off();
    states.delete(svg);
}
