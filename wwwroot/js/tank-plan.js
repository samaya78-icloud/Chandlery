// The tank plan: press a tank and drag it to a new place on the plan; let go to drop it there.
// The plan is a grid of columns and rows (one cell is 10 SVG units). A press that never moves is an ordinary click and is left alone.
// The page owns the truth: this only moves the tank while the pointer is down, then reports the cell it was dropped on.

const states = new WeakMap();
const CELL = 10;
const THRESHOLD = 4; // screen pixels before a press counts as a drag, so a click still selects

function toPlan(svg, e) {
    const pt = svg.createSVGPoint();
    pt.x = e.clientX; pt.y = e.clientY;
    const p = pt.matrixTransform(svg.getScreenCTM().inverse());
    return { x: p.x, y: p.y };
}

export function init(svg, dotnet, columns, rows) {
    dispose(svg);
    const state = { drag: null, suppressClick: false };
    states.set(svg, state);

    const place = (d, e) => {
        const p = toPlan(svg, e);
        const col = Math.round((p.x - d.offX) / CELL);
        const row = Math.round((p.y - d.offY) / CELL);
        d.col = Math.min(Math.max(col, 0), columns - d.w);
        d.row = Math.min(Math.max(row, 0), rows - d.h);
        d.el.setAttribute("transform", `translate(${(d.col - d.x) * CELL} ${(d.row - d.y) * CELL})`);
    };

    const down = e => {
        if (e.button > 0) return;
        const el = e.target.closest ? e.target.closest("[data-tank]") : null;
        if (!el || el.dataset.movable !== "true") return;
        const x = +el.dataset.x, y = +el.dataset.y, w = +el.dataset.w, h = +el.dataset.h;
        const p = toPlan(svg, e);
        state.drag = { el, id: el.dataset.tank, x, y, w, h, col: x, row: y, offX: p.x - x * CELL, offY: p.y - y * CELL, sx: e.clientX, sy: e.clientY, moved: false };
    };
    const move = e => {
        const d = state.drag;
        if (!d) return;
        if (!d.moved) {
            if (Math.hypot(e.clientX - d.sx, e.clientY - d.sy) < THRESHOLD) return;
            d.moved = true;
            svg.setPointerCapture?.(e.pointerId);
            d.el.classList.add("dragging");
            svg.classList.add("dragging");
        }
        place(d, e);
        e.preventDefault();
    };
    const finish = (e, cancelled) => {
        const d = state.drag;
        if (!d) return;
        state.drag = null;
        svg.classList.remove("dragging");
        d.el.classList.remove("dragging");
        if (!d.moved) return;
        state.suppressClick = true;           // the click that follows a drag must not select the tank
        setTimeout(() => { state.suppressClick = false; }, 0);
        if (cancelled || (d.col === d.x && d.row === d.y)) { d.el.removeAttribute("transform"); return; }
        dotnet.invokeMethodAsync("OnTankMoved", d.id, d.col, d.row);   // the page re-renders the tank at its saved place
    };
    const up = e => finish(e, false);
    const cancel = e => finish(e, true);
    const click = e => { if (state.suppressClick) { e.stopPropagation(); e.preventDefault(); } };
    const key = e => { if (e.key === "Escape" && state.drag?.moved) finish(e, true); };

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

/** The page re-drew the plan (a tank saved or refused): drop any leftover drag offset. */
export function reset(svg) {
    svg.querySelectorAll("[data-tank][transform]").forEach(el => el.removeAttribute("transform"));
}

export function dispose(svg) {
    const state = states.get(svg);
    if (state?.off) state.off();
    states.delete(svg);
}
