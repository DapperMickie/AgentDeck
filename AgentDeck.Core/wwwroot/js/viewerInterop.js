const registrations = new Map();
const pointerMoveIntervalMs = 16;
const wheelPixelStep = 100;
const wheelPageStep = 3;

function clamp(value) {
    return Math.min(Math.max(value, 0), 1);
}

function normalize(element, event) {
    const rect = element.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
        return null;
    }

    return {
        x: clamp((event.clientX - rect.left) / rect.width),
        y: clamp((event.clientY - rect.top) / rect.height)
    };
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
        capturedPointerIds: [],
        wheelRemainderX: 0,
        wheelRemainderY: 0,
        onPointerMove: null,
        onDragPointerMove: null,
        onPointerDown: null,
        onPointerUp: null,
        onWheel: null,
        onContextMenu: null,
        onAuxClick: null,
        onDoubleClick: null,
        onDragStart: null,
        onKeyDown: null,
        onKeyUp: null
    };

    const queueMove = event => {
        const point = normalize(element, event);
        if (!point) {
            return;
        }

        point.button = getActiveDragButton(registration);
        registration.pendingMove = point;
        schedulePointerMove(elementId);
    };

    const onPointerMove = event => queueMove(event);

    const onDragPointerMove = event => {
        if (registration.pressedButtons.length === 0) {
            return;
        }

        queueMove(event);
    };

    const onPointerDown = event => {
        element.focus();
        event.preventDefault();
        if (typeof element.setPointerCapture === "function") {
            try {
                element.setPointerCapture(event.pointerId);
                if (!registration.capturedPointerIds.includes(event.pointerId)) {
                    registration.capturedPointerIds.push(event.pointerId);
                }
            } catch {
            }
        }

        const point = normalize(element, event);
        if (!point) {
            return;
        }

        const button = buttonName(event.button);
        addPressedButton(registration, button);
        flushPendingMoveBeforeImmediatePointer(elementId);
        enqueuePointerEvent(registration, "down", point, button, getClickCount(event));
    };

    const onPointerUp = event => {
        event.preventDefault();
        if (typeof element.releasePointerCapture === "function") {
            try {
                element.releasePointerCapture(event.pointerId);
                registration.capturedPointerIds = registration.capturedPointerIds.filter(pointerId => pointerId !== event.pointerId);
            } catch {
            }
        }

        const point = normalize(element, event);
        if (!point) {
            return;
        }

        const button = buttonName(event.button);
        flushPendingMoveBeforeImmediatePointer(elementId);
        enqueuePointerEvent(registration, "up", point, button, getClickCount(event));
        removePressedButton(registration, button);
    };

    const onWheel = event => {
        event.preventDefault();
        const point = normalize(element, event);
        if (!point) {
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

        flushPendingMoveBeforeImmediatePointer(elementId);
        enqueuePointerEvent(registration, "wheel", point, null, 0, wheelDeltaX, wheelDeltaY);
    };

    const preventDefault = event => event.preventDefault();

    const onKeyDown = event => {
        dotNetReference
            .invokeMethodAsync("HandleKeyboard", "keydown", event.code, event.altKey, event.ctrlKey, event.shiftKey)
            .catch(() => {});
        event.preventDefault();
    };

    const onKeyUp = event => {
        dotNetReference
            .invokeMethodAsync("HandleKeyboard", "keyup", event.code, event.altKey, event.ctrlKey, event.shiftKey)
            .catch(() => {});
        event.preventDefault();
    };

    registration.onPointerMove = onPointerMove;
    registration.onDragPointerMove = onDragPointerMove;
    registration.onPointerDown = onPointerDown;
    registration.onPointerUp = onPointerUp;
    registration.onWheel = onWheel;
    registration.onContextMenu = preventDefault;
    registration.onAuxClick = preventDefault;
    registration.onDoubleClick = preventDefault;
    registration.onDragStart = preventDefault;
    registration.onKeyDown = onKeyDown;
    registration.onKeyUp = onKeyUp;

    element.addEventListener("pointermove", onPointerMove);
    element.addEventListener("pointerdown", onPointerDown);
    window.addEventListener("pointermove", onDragPointerMove);
    window.addEventListener("pointerup", onPointerUp);
    element.addEventListener("wheel", onWheel, { passive: false });
    element.addEventListener("contextmenu", preventDefault);
    element.addEventListener("auxclick", preventDefault);
    element.addEventListener("dblclick", preventDefault);
    element.addEventListener("dragstart", preventDefault);
    element.addEventListener("keydown", onKeyDown);
    element.addEventListener("keyup", onKeyUp);

    registrations.set(elementId, registration);
}

export function detach(elementId) {
    const existing = registrations.get(elementId);
    if (!existing) {
        return;
    }

    if (existing.moveTimer !== null) {
        window.clearTimeout(existing.moveTimer);
    }

    if (typeof existing.element.releasePointerCapture === "function") {
        for (const pointerId of existing.capturedPointerIds) {
            try {
                existing.element.releasePointerCapture(pointerId);
            } catch {
            }
        }
    }

    existing.capturedPointerIds = [];
    existing.pressedButtons = [];

    existing.element.removeEventListener("pointermove", existing.onPointerMove);
    window.removeEventListener("pointermove", existing.onDragPointerMove);
    existing.element.removeEventListener("pointerdown", existing.onPointerDown);
    window.removeEventListener("pointerup", existing.onPointerUp);
    existing.element.removeEventListener("wheel", existing.onWheel);
    existing.element.removeEventListener("contextmenu", existing.onContextMenu);
    existing.element.removeEventListener("auxclick", existing.onAuxClick);
    existing.element.removeEventListener("dblclick", existing.onDoubleClick);
    existing.element.removeEventListener("dragstart", existing.onDragStart);
    existing.element.removeEventListener("keydown", existing.onKeyDown);
    existing.element.removeEventListener("keyup", existing.onKeyUp);
    registrations.delete(elementId);
}

export function focus(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.focus();
    }
}

export async function toggleFullscreen(elementId) {
    const element = document.getElementById(elementId);
    if (!element) {
        return;
    }

    if (document.fullscreenElement === element) {
        await document.exitFullscreen();
        return;
    }

    if (document.fullscreenElement && document.fullscreenElement !== element) {
        await document.exitFullscreen();
        return;
    }

    if (typeof element.requestFullscreen === "function") {
        await element.requestFullscreen();
    }
}
