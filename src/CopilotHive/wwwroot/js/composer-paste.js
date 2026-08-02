// Clipboard paste support for the Composer chat textarea.
//
// Loaded as an ES module via IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/composer-paste.js").
// When the user pastes a supported image/PDF blob, the blob is streamed to .NET through
// DotNet.createJSStreamReference so it lands in the SAME AttachmentService.SaveAsync
// pending-attachment flow the file picker already uses.
//
// The `allowed` flag mirrors the .NET-side state (no pending attachment, not uploading,
// not streaming). It is only an optimisation/UX mirror: .NET re-validates authoritatively.

const SUPPORTED_TYPES = [
    'image/png',
    'image/jpeg',
    'image/gif',
    'image/webp',
    'application/pdf'
];

let textarea = null;
let dotNetRef = null;
let listener = null;
let allowed = true;

/**
 * Registers the paste listener on the given textarea.
 * @param {string} textareaId id of the textarea element.
 * @param {any} ref DotNetObjectReference to the ComposerChat component.
 */
export function install(textareaId, ref) {
    // Defensive: never stack two listeners if install runs twice.
    uninstall();

    const element = document.getElementById(textareaId);
    if (!element) {
        return;
    }

    textarea = element;
    dotNetRef = ref;
    allowed = true;
    listener = onPaste;
    textarea.addEventListener('paste', listener);
}

/**
 * Removes the paste listener and clears every stored reference.
 */
export function uninstall() {
    if (textarea && listener) {
        textarea.removeEventListener('paste', listener);
    }
    textarea = null;
    listener = null;
    dotNetRef = null;
    allowed = false;
}

/**
 * Mirrors the .NET-side "paste is currently allowed" state.
 * @param {boolean} value whether a paste may be intercepted.
 */
export function setAllowed(value) {
    allowed = !!value;
}

function onPaste(e) {
    // Not allowed → normal paste passes straight through (no preventDefault, no error).
    if (!allowed) {
        return;
    }

    const items = e.clipboardData && e.clipboardData.items;
    if (!items) {
        return;
    }

    // First supported file item wins; at most one attachment per paste event.
    let match = null;
    for (let i = 0; i < items.length; i++) {
        const item = items[i];
        if (item.kind === 'file' && SUPPORTED_TYPES.indexOf(item.type) !== -1) {
            match = item;
            break;
        }
    }

    // Plain text, or an unsupported clipboard file → untouched, no error.
    if (!match) {
        return;
    }

    // The blob is resolved BEFORE anything is intercepted: a missing or zero-length blob is
    // never interesting, so the paste must fall through to normal browser behaviour with no
    // preventDefault, no .NET call, no error and `allowed` left untouched.
    const blob = match.getAsFile();
    if (!blob || blob.size === 0) {
        return;
    }

    // Only now is the paste ours. Both statements are synchronous, and run BEFORE the async
    // .NET invocation: the browser must not insert the blob itself, and a second paste must
    // not race the first one.
    e.preventDefault();
    allowed = false;

    dotNetRef
        .invokeMethodAsync(
            'HandlePasteAsync',
            DotNet.createJSStreamReference(blob),
            match.type,
            blob.size)
        .catch(() => {
            // Interop failed (circuit teardown, disposed ref): re-enable so paste is not
            // permanently dead. .NET re-syncs the mirror on its next state transition.
            allowed = true;
        });
}
