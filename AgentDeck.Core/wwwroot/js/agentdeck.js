// AgentDeck terminal interop module
// Manages xterm.js terminal instances keyed by sessionId

const terminals = new Map();

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

    // Initial fit
    try { fitAddon.fit(); } catch (_) {}

    // Keyboard input → .NET callback
    term.onData(data => {
        dotnetRef.invokeMethodAsync('OnTerminalInput', data)
            .catch(err => console.warn('[AgentDeck] OnTerminalInput failed:', err));
    });

    // Resize → .NET callback
    term.onResize(({ cols, rows }) => {
        dotnetRef.invokeMethodAsync('OnTerminalResize', cols, rows)
            .catch(err => console.warn('[AgentDeck] OnTerminalResize failed:', err));
    });

    terminals.set(sessionId, { term, fitAddon, dotnetRef });
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
        try { entry.fitAddon.fit(); } catch (_) {}
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
