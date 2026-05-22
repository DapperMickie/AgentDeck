// AgentDeck terminal interop module
// Manages xterm.js terminal instances keyed by sessionId

const terminals = new Map();

function canMeasureContainer(container) {
    return !!container
        && document.body.contains(container)
        && container.clientWidth > 0
        && container.clientHeight > 0;
}

function scheduleFit(entry) {
    if (!entry || entry.fitFrame !== null) {
        return;
    }

    entry.fitFrame = requestAnimationFrame(() => {
        entry.fitFrame = null;

        if (!canMeasureContainer(entry.container)) {
            return;
        }

        const previousCols = entry.term.cols;
        const previousRows = entry.term.rows;
        try { entry.fitAddon.fit(); } catch (_) {}

        if (entry.term.cols !== previousCols || entry.term.rows !== previousRows) {
            entry.dotnetRef.invokeMethodAsync('OnTerminalResize', entry.term.cols, entry.term.rows)
                .catch(err => console.warn('[AgentDeck] OnTerminalResize failed:', err));
        }
    });
}

/**
 * Create and mount an xterm.js terminal in the given DOM element.
 * @param {string} elementId - The id of the container div
 * @param {string} sessionId - The session identifier (used as key)
 * @param {object} dotnetRef - DotNet object reference for callbacks
 * @param {object} theme - Optional xterm theme overrides
 */
export function createTerminal(elementId, sessionId, dotnetRef, theme) {
    if (terminals.has(sessionId)) {
        disposeTerminal(sessionId);
    }

    const container = document.getElementById(elementId);
    if (!container) {
        console.warn(`[AgentDeck] Container #${elementId} not found`);
        return;
    }

    const term = new Terminal({
        fontFamily: '"Cascadia Code", "Fira Code", "Consolas", monospace',
        fontSize: 13,
        lineHeight: 1.2,
        cursorBlink: true,
        cursorStyle: 'bar',
        scrollback: 5000,
        // PTY output can contain bare LF characters. In a terminal emulator LF
        // advances the row but does not reset the cursor column, which makes
        // plain-text output stair-step. Treat LF as CRLF for display so new
        // lines start at column zero while preserving normal ANSI behavior.
        convertEol: true,
        theme: theme || {
            background:   '#1e1e2e',
            foreground:   '#cdd6f4',
            cursor:       '#f5e0dc',
            cursorAccent: '#1e1e2e',
            selectionBackground: 'rgba(137,180,250,0.3)',
            black:        '#45475a',
            red:          '#f38ba8',
            green:        '#a6e3a1',
            yellow:       '#f9e2af',
            blue:         '#89b4fa',
            magenta:      '#f5c2e7',
            cyan:         '#94e2d5',
            white:        '#bac2de',
            brightBlack:  '#585b70',
            brightRed:    '#f38ba8',
            brightGreen:  '#a6e3a1',
            brightYellow: '#f9e2af',
            brightBlue:   '#89b4fa',
            brightMagenta:'#f5c2e7',
            brightCyan:   '#94e2d5',
            brightWhite:  '#a6adc8',
        }
    });

    const fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(container);
    // Do NOT fit here — the caller calls fitAndGetSize() after joining the
    // session so the actual cols/rows are sent to the runner atomically.

    const sendTerminalInput = data => {
        if (!data) {
            return;
        }

        dotnetRef.invokeMethodAsync('OnTerminalInput', data)
            .catch(err => console.warn('[AgentDeck] OnTerminalInput failed:', err));
    };

    // Keyboard input → .NET callback
    term.onData(sendTerminalInput);

    // Resize → .NET callback
    term.onResize(({ cols, rows }) => {
        dotnetRef.invokeMethodAsync('OnTerminalResize', cols, rows)
            .catch(err => console.warn('[AgentDeck] OnTerminalResize failed:', err));
    });

    terminals.set(sessionId, {
        term,
        fitAddon,
        dotnetRef,
        container,
        fitFrame: null,
        resizeObserver: null,
        resizeHandler: null,
        viewportResizeHandler: null,
        pasteHandler: null,
        pasteTargets: []
    });

    const entry = terminals.get(sessionId);
    entry.pasteHandler = event => {
        const text = event.clipboardData?.getData('text/plain');
        if (!text) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        sendTerminalInput(text);
    };

    const addPasteTarget = target => {
        if (!target || entry.pasteTargets.includes(target)) {
            return;
        }

        target.addEventListener('paste', entry.pasteHandler, true);
        entry.pasteTargets.push(target);
    };

    addPasteTarget(container);
    addPasteTarget(term.element);
    addPasteTarget(term.textarea);
    addPasteTarget(container.querySelector('.xterm-helper-textarea'));

    const pasteFromClipboard = event => {
        if (!navigator.clipboard?.readText) {
            return true;
        }

        navigator.clipboard.readText()
            .then(sendTerminalInput)
            .catch(err => console.warn('[AgentDeck] clipboard paste failed:', err));
        event.preventDefault();
        return false;
    };

    const sendShortcut = (event, data) => {
        sendTerminalInput(data);
        event.preventDefault();
        return false;
    };

    term.attachCustomKeyEventHandler(event => {
        if (event.type !== 'keydown') {
            return true;
        }

        const key = event.key?.toLowerCase();

        if ((event.ctrlKey || event.metaKey) && key === 'v') {
            return pasteFromClipboard(event);
        }

        if (event.shiftKey && event.key === 'Insert') {
            return pasteFromClipboard(event);
        }

        if (event.ctrlKey && event.key === 'Backspace') {
            // Readline-compatible previous-word delete.
            return sendShortcut(event, '\x17');
        }

        if (event.ctrlKey && event.key === 'Delete') {
            // xterm sequence for Ctrl+Delete / delete next word in readline-aware apps.
            return sendShortcut(event, '\x1b[3;5~');
        }

        if (event.altKey && event.key === 'Backspace') {
            // Meta+Backspace is the common terminal binding for backward-kill-word.
            return sendShortcut(event, '\x1b\x7f');
        }

        if (event.altKey && key === 'b') {
            return sendShortcut(event, '\x1bb');
        }

        if (event.altKey && key === 'f') {
            return sendShortcut(event, '\x1bf');
        }

        if (event.shiftKey && event.key === 'Backspace') {
            // Preserve shifted backspace as an explicit DEL instead of letting the browser decide.
            return sendShortcut(event, '\x7f');
        }

        return true;
    });
}

/**
 * Write raw terminal data (including ANSI escape sequences) to the terminal.
 */
export function writeToTerminal(sessionId, data) {
    const entry = terminals.get(sessionId);
    if (entry) {
        entry.term.write(data);
    }
}

/**
 * Fit the terminal to its container size.
 */
export function fitTerminal(sessionId) {
    const entry = terminals.get(sessionId);
    if (entry) {
        scheduleFit(entry);
    }
}

/**
 * Fit the terminal and return the resulting {cols, rows} after layout.
 * Uses requestAnimationFrame so the browser finishes layout before measuring.
 * Returns a Promise so the C# caller can await the actual dimensions and
 * immediately notify the runner — avoiding the PTY size race condition.
 */
export function fitAndGetSize(sessionId) {
    return new Promise(resolve => {
        requestAnimationFrame(() => {
            const entry = terminals.get(sessionId);
            if (!entry) { resolve(null); return; }
            if (!canMeasureContainer(entry.container)) { resolve(null); return; }
            try { entry.fitAddon.fit(); } catch (_) {}
            resolve({ cols: entry.term.cols, rows: entry.term.rows });
        });
    });
}

export function registerAutoFit(sessionId) {
    const entry = terminals.get(sessionId);
    if (!entry) {
        return;
    }

    unregisterAutoFit(sessionId);

    const onResize = () => scheduleFit(entry);
    entry.resizeHandler = onResize;

    if (typeof ResizeObserver !== 'undefined') {
        entry.resizeObserver = new ResizeObserver(onResize);
        entry.resizeObserver.observe(entry.container);
    }

    window.addEventListener('resize', onResize);

    if (window.visualViewport) {
        entry.viewportResizeHandler = onResize;
        window.visualViewport.addEventListener('resize', onResize);
    }
}

export function unregisterAutoFit(sessionId) {
    const entry = terminals.get(sessionId);
    if (!entry) {
        return;
    }

    if (entry.resizeObserver) {
        entry.resizeObserver.disconnect();
        entry.resizeObserver = null;
    }

    if (entry.resizeHandler) {
        window.removeEventListener('resize', entry.resizeHandler);
        entry.resizeHandler = null;
    }

    if (entry.viewportResizeHandler && window.visualViewport) {
        window.visualViewport.removeEventListener('resize', entry.viewportResizeHandler);
        entry.viewportResizeHandler = null;
    }

    if (entry.fitFrame !== null) {
        cancelAnimationFrame(entry.fitFrame);
        entry.fitFrame = null;
    }
}

/**
 * Focus the terminal.
 */
export function focusTerminal(sessionId) {
    const entry = terminals.get(sessionId);
    if (entry) entry.term.focus();
}

/**
 * Dispose and remove a terminal instance.
 */
export function disposeTerminal(sessionId) {
    const entry = terminals.get(sessionId);
    if (entry) {
        unregisterAutoFit(sessionId);
        if (entry.pasteHandler) {
            for (const target of entry.pasteTargets || []) {
                try { target.removeEventListener('paste', entry.pasteHandler, true); } catch (_) {}
            }
            entry.pasteTargets = [];
            entry.pasteHandler = null;
        }
        try { entry.term.dispose(); } catch (_) {}
        // Release the .NET reference immediately rather than waiting for JS GC,
        // since the term.onData/onResize closures captured it.
        try { entry.dotnetRef.dispose(); } catch (_) {}
        terminals.delete(sessionId);
    }
}

/**
 * Returns {cols, rows} for the given terminal, or null if not found.
 */
export function getTerminalSize(sessionId) {
    const entry = terminals.get(sessionId);
    if (!entry) return null;
    return { cols: entry.term.cols, rows: entry.term.rows };
}

// ========== Keyboard Shortcuts ==========

let _shortcutHandler = null;

export function registerShortcuts(dotnetRef) {
    unregisterShortcuts();
    _shortcutHandler = (e) => {
        if (!e.ctrlKey) return;
        if (e.key === 't') { e.preventDefault(); dotnetRef.invokeMethodAsync('OnShortcutNewTerminal'); }
        else if (e.key === 'w') { e.preventDefault(); dotnetRef.invokeMethodAsync('OnShortcutCloseTerminal'); }
        else if (e.key === '?') { e.preventDefault(); dotnetRef.invokeMethodAsync('OnShortcutHelp'); }
        else if (e.key >= '1' && e.key <= '9') { e.preventDefault(); dotnetRef.invokeMethodAsync('OnShortcutSwitchTab', parseInt(e.key) - 1); }
    };
    document.addEventListener('keydown', _shortcutHandler);
}

export function unregisterShortcuts() {
    if (_shortcutHandler) { document.removeEventListener('keydown', _shortcutHandler); _shortcutHandler = null; }
}
