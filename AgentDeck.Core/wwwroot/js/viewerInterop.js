const registrations = new Map();
const pointerMoveIntervalMs = 16;
const wheelPixelStep = 100;
const wheelPageStep = 3;
const moveLogIntervalMs = 500;

function log(message, level = "info") {
    const prefixed = `[AgentDeck.ViewerInterop] ${message}`;
    if (level === "warn") {
        console.warn(prefixed);
        return;
    }

    console.info(prefixed);
}

function reportStatus(registration, message, level = "info") {
    log(message, level);
    registration.dotNetReference
        .invokeMethodAsync("HandleInteropStatus", message)
        .catch(() => {});
}

function clamp(value) {
    return Math.min(Math.max(value, 0), 1);
}

function normalize(element, event) {
    const frame = getInteractiveFrameElement(element);
    const rect = (frame ?? element).getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
        return null;
    }

    if (frame
        && (event.clientX < rect.left
            || event.clientX > rect.right
            || event.clientY < rect.top
            || event.clientY > rect.bottom)) {
        return null;
    }

    return {
        x: clamp((event.clientX - rect.left) / rect.width),
        y: clamp((event.clientY - rect.top) / rect.height)
    };
}

function getInteractiveFrameElement(element) {
    const frame = element.querySelector(".remote-viewer-surface__frame");
    if (!frame) {
        return null;
    }

    return frame.clientWidth > 0 && frame.clientHeight > 0
        ? frame
        : null;
}

function buttonName(button) {
    switch (button) {
        case 1: return "middle";
        case 2: return "right";
        default: return "left";
    }
}

function addPressedButton(registration, button) {
    registration.pressedButtons = registration.pressedButtons.filter(current => current !== button);
    registration.pressedButtons.push(button);
}

function removePressedButton(registration, button) {
    registration.pressedButtons = registration.pressedButtons.filter(current => current !== button);
}

function getActiveDragButton(registration) {
    return registration.pressedButtons.length === 0
        ? null
        : registration.pressedButtons[registration.pressedButtons.length - 1];
}

function scaleWheelDelta(value, deltaMode) {
    switch (deltaMode) {
        case WheelEvent.DOM_DELTA_LINE:
            return value;
        case WheelEvent.DOM_DELTA_PAGE:
            return value * wheelPageStep;
        default:
            return value / wheelPixelStep;
    }
}

function getClickCount(event) {
    return Number.isFinite(event.detail) && event.detail > 0 ? event.detail : 1;
}

function schedulePointerMove(elementId) {
    const registration = registrations.get(elementId);
    if (!registration || registration.moveTimer !== null || registration.moveInFlight || !registration.pendingMove) {
        return;
    }

    registration.moveTimer = window.setTimeout(() => flushPointerMove(elementId), pointerMoveIntervalMs);
}

function enqueuePointerEvent(registration, eventType, point, button, clickCount = 0, wheelDeltaX = 0, wheelDeltaY = 0) {
    registration.pointerSendChain = registration.pointerSendChain
        .catch(() => {})
        .then(() => registration.dotNetReference.invokeMethodAsync(
            "HandlePointer",
            eventType,
            point.x,
            point.y,
            button,
            clickCount,
            wheelDeltaX,
            wheelDeltaY))
        .catch(() => {});

    return registration.pointerSendChain;
}

function flushPointerMove(elementId) {
    const registration = registrations.get(elementId);
    if (!registration) {
        return;
    }

    registration.moveTimer = null;
    if (registration.moveInFlight || !registration.pendingMove) {
        return;
    }

    const move = registration.pendingMove;
    registration.pendingMove = null;
    registration.moveInFlight = true;

    enqueuePointerEvent(registration, "move", move, move.button)
        .finally(() => {
            const current = registrations.get(elementId);
            if (!current) {
                return;
            }

            current.moveInFlight = false;
            if (current.pendingMove) {
                schedulePointerMove(elementId);
            }
        });
}

function flushPendingMoveBeforeImmediatePointer(elementId) {
    const registration = registrations.get(elementId);
    if (!registration) {
        return Promise.resolve();
    }

    if (registration.moveTimer !== null) {
        window.clearTimeout(registration.moveTimer);
        registration.moveTimer = null;
    }

    if (!registration.pendingMove) {
        return registration.pointerSendChain;
    }

    const move = registration.pendingMove;
    registration.pendingMove = null;
    registration.moveInFlight = true;

    return enqueuePointerEvent(registration, "move", move, move.button)
        .finally(() => {
            const current = registrations.get(elementId);
            if (!current) {
                return;
            }

            current.moveInFlight = false;
            if (current.pendingMove) {
                schedulePointerMove(elementId);
            }
        });
}

export function attach(elementId, dotNetReference) {
    detach(elementId);

    const element = document.getElementById(elementId);
    if (!element) {
        return;
    }

    const registration = {
        element,
        dotNetReference,
        pendingMove: null,
        moveInFlight: false,
        moveTimer: null,
        pointerSendChain: Promise.resolve(),
        pressedButtons: [],
        wheelRemainderX: 0,
        wheelRemainderY: 0,
        onMouseMove: null,
        onDragMouseMove: null,
        onMouseDown: null,
        onMouseUp: null,
        onWheel: null,
        onContextMenu: null,
        onAuxClick: null,
        onDoubleClick: null,
        onDragStart: null,
        onKeyDown: null,
        onKeyUp: null,
        onFrameLoad: null,
        frameElement: null,
        lastMoveLogAt: 0
    };

    reportStatus(registration, `Attaching viewer hooks for #${elementId}. framePresent=${getInteractiveFrameElement(element) !== null}`);

    const bindFrameLoad = () => {
        const frame = getInteractiveFrameElement(element);
        if (registration.frameElement === frame) {
            return;
        }

        if (registration.frameElement && registration.onFrameLoad) {
            registration.frameElement.removeEventListener("load", registration.onFrameLoad);
        }

        registration.frameElement = frame;
        if (!frame) {
            reportStatus(registration, `No frame element found for #${elementId} at attach time.`, "warn");
            return;
        }

        const onFrameLoad = () => {
            reportStatus(registration, `Frame load observed for #${elementId} (${frame.clientWidth}x${frame.clientHeight}).`);
            element.focus();
        };

        registration.onFrameLoad = onFrameLoad;
        frame.addEventListener("load", onFrameLoad);
        reportStatus(registration, `Frame element detected for #${elementId}. complete=${frame.complete}`);
        if (frame.complete) {
            onFrameLoad();
        }
    };

    bindFrameLoad();

    const queueMove = event => {
        const point = normalize(element, event);
        if (!point) {
            return;
        }

        point.button = getActiveDragButton(registration);
        registration.pendingMove = point;
        const now = Date.now();
        if (now - registration.lastMoveLogAt >= moveLogIntervalMs) {
            registration.lastMoveLogAt = now;
            reportStatus(registration, `mousemove x=${point.x.toFixed(3)} y=${point.y.toFixed(3)} button=${point.button ?? "<none>"}`);
        }
        schedulePointerMove(elementId);
    };

    const onMouseMove = event => queueMove(event);

    const onDragMouseMove = event => {
        if (registration.pressedButtons.length === 0) {
            return;
        }

        queueMove(event);
    };

    const onMouseDown = event => {
        element.focus();
        event.preventDefault();
        const point = normalize(element, event);
        if (!point) {
            reportStatus(registration, `mousedown ignored because no normalized point was available for #${elementId}.`, "warn");
            return;
        }

        const button = buttonName(event.button);
        reportStatus(registration, `mousedown x=${point.x.toFixed(3)} y=${point.y.toFixed(3)} button=${button}`);
        addPressedButton(registration, button);
        flushPendingMoveBeforeImmediatePointer(elementId);
        enqueuePointerEvent(registration, "down", point, button, getClickCount(event));
    };

    const onMouseUp = event => {
        event.preventDefault();
        const point = normalize(element, event);
        if (!point) {
            reportStatus(registration, `mouseup ignored because no normalized point was available for #${elementId}.`, "warn");
            return;
        }

        const button = buttonName(event.button);
        reportStatus(registration, `mouseup x=${point.x.toFixed(3)} y=${point.y.toFixed(3)} button=${button}`);
        flushPendingMoveBeforeImmediatePointer(elementId);
        enqueuePointerEvent(registration, "up", point, button, getClickCount(event));
        removePressedButton(registration, button);
    };

    const onWheel = event => {
        event.preventDefault();
        const point = normalize(element, event);
        if (!point) {
            reportStatus(registration, `wheel ignored because no normalized point was available for #${elementId}.`, "warn");
            return;
        }

        registration.wheelRemainderX += scaleWheelDelta(event.deltaX, event.deltaMode);
        registration.wheelRemainderY += scaleWheelDelta(event.deltaY, event.deltaMode);

        const wheelDeltaX = Math.trunc(registration.wheelRemainderX);
        const wheelDeltaY = Math.trunc(registration.wheelRemainderY);
        if (wheelDeltaX === 0 && wheelDeltaY === 0) {
            return;
        }

        registration.wheelRemainderX -= wheelDeltaX;
        registration.wheelRemainderY -= wheelDeltaY;

        reportStatus(registration, `wheel x=${point.x.toFixed(3)} y=${point.y.toFixed(3)} dx=${wheelDeltaX} dy=${wheelDeltaY}`);
        flushPendingMoveBeforeImmediatePointer(elementId);
        enqueuePointerEvent(registration, "wheel", point, null, 0, wheelDeltaX, wheelDeltaY);
    };

    const preventDefault = event => event.preventDefault();

    const onKeyDown = event => {
        reportStatus(registration, `keydown code=${event.code} alt=${event.altKey} ctrl=${event.ctrlKey} shift=${event.shiftKey}`);
        dotNetReference
            .invokeMethodAsync("HandleKeyboard", "keydown", event.code, event.altKey, event.ctrlKey, event.shiftKey)
            .catch(() => {});
        event.preventDefault();
    };

    const onKeyUp = event => {
        reportStatus(registration, `keyup code=${event.code} alt=${event.altKey} ctrl=${event.ctrlKey} shift=${event.shiftKey}`);
        dotNetReference
            .invokeMethodAsync("HandleKeyboard", "keyup", event.code, event.altKey, event.ctrlKey, event.shiftKey)
            .catch(() => {});
        event.preventDefault();
    };

    registration.onMouseMove = onMouseMove;
    registration.onDragMouseMove = onDragMouseMove;
    registration.onMouseDown = onMouseDown;
    registration.onMouseUp = onMouseUp;
    registration.onWheel = onWheel;
    registration.onContextMenu = preventDefault;
    registration.onAuxClick = preventDefault;
    registration.onDoubleClick = preventDefault;
    registration.onDragStart = preventDefault;
    registration.onKeyDown = onKeyDown;
    registration.onKeyUp = onKeyUp;

    element.addEventListener("mousemove", onMouseMove);
    element.addEventListener("mousedown", onMouseDown);
    window.addEventListener("mousemove", onDragMouseMove);
    window.addEventListener("mouseup", onMouseUp);
    element.addEventListener("wheel", onWheel, { passive: false });
    element.addEventListener("contextmenu", preventDefault);
    element.addEventListener("auxclick", preventDefault);
    element.addEventListener("dblclick", preventDefault);
    element.addEventListener("dragstart", preventDefault);
    element.addEventListener("keydown", onKeyDown);
    element.addEventListener("keyup", onKeyUp);

    registrations.set(elementId, registration);
    reportStatus(registration, `Viewer hooks registered for #${elementId}.`);
}

export function detach(elementId) {
    const existing = registrations.get(elementId);
    if (!existing) {
        return;
    }

    if (existing.moveTimer !== null) {
        window.clearTimeout(existing.moveTimer);
    }

    if (existing.frameElement && existing.onFrameLoad) {
        existing.frameElement.removeEventListener("load", existing.onFrameLoad);
    }

    existing.pressedButtons = [];

    existing.element.removeEventListener("mousemove", existing.onMouseMove);
    window.removeEventListener("mousemove", existing.onDragMouseMove);
    existing.element.removeEventListener("mousedown", existing.onMouseDown);
    window.removeEventListener("mouseup", existing.onMouseUp);
    existing.element.removeEventListener("wheel", existing.onWheel);
    existing.element.removeEventListener("contextmenu", existing.onContextMenu);
    existing.element.removeEventListener("auxclick", existing.onAuxClick);
    existing.element.removeEventListener("dblclick", existing.onDoubleClick);
    existing.element.removeEventListener("dragstart", existing.onDragStart);
    existing.element.removeEventListener("keydown", existing.onKeyDown);
    existing.element.removeEventListener("keyup", existing.onKeyUp);
    reportStatus(existing, `Viewer hooks detached for #${elementId}.`);
    registrations.delete(elementId);
}

export function focus(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.focus();
        log(`Focus requested for #${elementId}.`);
    }
}

export async function toggleFullscreen(elementId) {
    const element = document.getElementById(elementId);
    if (!element) {
        log(`Toggle fullscreen requested for missing #${elementId}.`, "warn");
        return;
    }

    if (document.fullscreenElement === element) {
        log(`Exiting fullscreen for #${elementId}.`);
        await document.exitFullscreen();
        return;
    }

    if (document.fullscreenElement && document.fullscreenElement !== element) {
        log(`Exiting existing fullscreen element before entering fullscreen for #${elementId}.`);
        await document.exitFullscreen();
        return;
    }

    if (typeof element.requestFullscreen === "function") {
        log(`Entering fullscreen for #${elementId}.`);
        await element.requestFullscreen();
    }
}
