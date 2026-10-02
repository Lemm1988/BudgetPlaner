// Browser-Funktionen für die Blazor-App (ein Modul, per JS-Interop importiert).

// ---------- Speicher ----------
export function lsGet(key) { try { return localStorage.getItem(key); } catch { return null; } }
export function lsSet(key, value) { try { localStorage.setItem(key, value); } catch { /* privat/voll */ } }
export function lsRemove(key) { try { localStorage.removeItem(key); } catch { } }

// ---------- Darstellung ----------
export function setTheme(mode) {
    const root = document.documentElement;
    if (mode === 'light' || mode === 'dark') root.dataset.theme = mode; else delete root.dataset.theme;
    const dark = mode === 'dark' || (mode !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches);
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#14201B' : '#1F5F4A');
}

export function setLang(lang) { document.documentElement.lang = lang; }
export function setTitle(title) { document.title = title; }
export function timeZone() { try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return 'Europe/Berlin'; } }
export function browserLanguage() { return navigator.language || 'de'; }
export function isMobile() { return matchMedia('(max-width: 767.98px)').matches; }
export function scrollToTop() { window.scrollTo({ top: 0, behavior: 'instant' }); }
// Nach Navigation: zum Anker (#id) scrollen, sobald das Element gerendert ist – sonst nach oben.
export function scrollToTarget() {
    const id = decodeURIComponent(location.hash.slice(1));
    if (!id) { window.scrollTo({ top: 0, behavior: 'instant' }); return; }
    let tries = 0;
    const attempt = () => {
        const el = document.getElementById(id);
        if (el) { el.querySelectorAll('details').forEach(d => d.open = true); el.scrollIntoView({ block: 'start' }); return; }
        if (++tries < 30) setTimeout(attempt, 50);
    };
    attempt();
}
export function deviceName() {
    const ua = navigator.userAgent;
    const os = /iPhone|iPad/.test(ua) ? 'iOS' : /Android/.test(ua) ? 'Android' : /Windows/.test(ua) ? 'Windows' : /Mac/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
    const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : 'Browser';
    return os ? `${browser} · ${os}` : browser;
}

export function registerViewport(dotnet) {
    const mq = matchMedia('(max-width: 767.98px)');
    const handler = () => dotnet.invokeMethodAsync('OnViewportChanged', mq.matches);
    mq.addEventListener('change', handler);
    const dark = matchMedia('(prefers-color-scheme: dark)');
    dark.addEventListener('change', () => dotnet.invokeMethodAsync('OnSystemThemeChanged'));
    return mq.matches;
}

// ---------- Online/Offline & Sichtbarkeit (F-132) ----------
export function isOnline() { return navigator.onLine; }
export function registerConnectivity(dotnet) {
    window.addEventListener('online', () => dotnet.invokeMethodAsync('OnOnlineChanged', true));
    window.addEventListener('offline', () => dotnet.invokeMethodAsync('OnOnlineChanged', false));
    document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') dotnet.invokeMethodAsync('OnVisible');
    });
}

// ---------- Fokus, Dialoge, Klick außerhalb ----------
export function focus(el) { try { el?.focus({ preventScroll: false }); } catch { } }
export function focusSelector(selector) { document.querySelector(selector)?.focus(); }
export function focusFirst(container) {
    const el = container?.querySelector('[autofocus], input:not([type=hidden]):not([disabled]), select, textarea, button:not([disabled]), [href], [tabindex]:not([tabindex="-1"])');
    el?.focus();
}
export function setScrollLock(locked) { document.body.classList.toggle('scroll-locked', !!locked); }

const outsideHandlers = new Map();
export function onClickOutside(el, dotnet) {
    const handler = e => { if (el && !el.contains(e.target)) dotnet.invokeMethodAsync('OnClickOutside'); };
    const key = Symbol();
    document.addEventListener('pointerdown', handler, true);
    outsideHandlers.set(el, handler);
    return true;
}
export function offClickOutside(el) {
    const handler = outsideHandlers.get(el);
    if (handler) document.removeEventListener('pointerdown', handler, true);
    outsideHandlers.delete(el);
}

// Fokusfalle für modale Dialoge (F-120: vollständig per Tastatur bedienbar)
export function trapFocus(container) {
    const handler = e => {
        if (e.key !== 'Tab') return;
        const items = [...container.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])')]
            .filter(x => x.offsetParent !== null);
        if (items.length === 0) return;
        const first = items[0], last = items[items.length - 1];
        if (e.shiftKey && document.activeElement === first) { last.focus(); e.preventDefault(); }
        else if (!e.shiftKey && document.activeElement === last) { first.focus(); e.preventDefault(); }
    };
    container.addEventListener('keydown', handler);
    container.__trap = handler;
}
export function releaseFocus(container) {
    if (container?.__trap) container.removeEventListener('keydown', container.__trap);
}
export function storeFocus() { window.__lastFocus = document.activeElement; }
export function restoreFocus() { try { window.__lastFocus?.focus(); } catch { } }

// ---------- Dateien, Teilen, Zwischenablage ----------
export async function downloadWithAuth(url, token, fallbackName) {
    const res = await fetch(url, { headers: token ? { Authorization: 'Bearer ' + token } : {} });
    if (!res.ok) {
        let message = res.statusText;
        try { message = (await res.json()).title || message; } catch { }
        throw new Error(message);
    }
    const blob = await res.blob();
    const disposition = res.headers.get('content-disposition') || '';
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    const name = match ? decodeURIComponent(match[1]) : fallbackName;
    saveBlob(blob, name);
}

export function downloadText(name, mime, text) { saveBlob(new Blob([text], { type: mime }), name); }

function saveBlob(blob, name) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
}

export function canShare() { return !!navigator.share; }
export async function share(title, text, url) {
    if (!navigator.share) return false;
    try { await navigator.share({ title, text, url }); return true; } catch { return false; }
}
export function openWindow(url) { window.open(url, '_blank', 'noopener,noreferrer'); }
export async function copyText(text) {
    try { await navigator.clipboard.writeText(text); return true; } catch { return false; }
}
export function print() { window.print(); }

// ---------- PWA: Installation & Updates (F-091) ----------
let deferredPrompt = null;
let installListener = null;
window.addEventListener('beforeinstallprompt', e => {
    e.preventDefault();
    deferredPrompt = e;
    installListener?.invokeMethodAsync('OnInstallAvailable');
});
window.addEventListener('appinstalled', () => { deferredPrompt = null; installListener?.invokeMethodAsync('OnInstalled'); });

export function registerInstall(dotnet) { installListener = dotnet; return !!deferredPrompt; }
export function isStandalone() { return matchMedia('(display-mode: standalone)').matches || navigator.standalone === true; }
export function isIos() { return /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1); }
export async function promptInstall() {
    if (!deferredPrompt) return false;
    deferredPrompt.prompt();
    const choice = await deferredPrompt.userChoice;
    deferredPrompt = null;
    return choice.outcome === 'accepted';
}

export function registerServiceWorkerUpdates(dotnet) {
    if (!('serviceWorker' in navigator)) return;
    navigator.serviceWorker.getRegistration().then(reg => {
        if (!reg) return;
        const notify = () => dotnet.invokeMethodAsync('OnUpdateAvailable');
        if (reg.waiting && navigator.serviceWorker.controller) notify();
        reg.addEventListener('updatefound', () => {
            const sw = reg.installing;
            sw?.addEventListener('statechange', () => {
                if (sw.state === 'installed' && navigator.serviceWorker.controller) notify();
            });
        });
    });
}
// Nachrichten des Service Workers: Klick auf Push-Benachrichtigung → In-App-Navigation, neuer Push → Glocke laden.
export function registerServiceWorkerMessages(dotnet) {
    if (!('serviceWorker' in navigator)) return;
    navigator.serviceWorker.addEventListener('message', e => {
        if (e.data?.type === 'navigate') dotnet.invokeMethodAsync('OnServiceWorkerNavigate', e.data.url);
        else if (e.data?.type === 'push') dotnet.invokeMethodAsync('OnPushReceived');
    });
}
export async function applyUpdate() {
    const reg = await navigator.serviceWorker?.getRegistration();
    if (reg?.waiting) {
        navigator.serviceWorker.addEventListener('controllerchange', () => location.reload());
        reg.waiting.postMessage({ type: 'SKIP_WAITING' });
    } else {
        location.reload();
    }
}

// ---------- Web-Push (F-080, F-081) ----------
export function pushSupported() { return 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window; }
export function notificationPermission() { return 'Notification' in window ? Notification.permission : 'unsupported'; }

function b64urlToBytes(s) {
    const pad = '='.repeat((4 - s.length % 4) % 4);
    const b64 = (s + pad).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(b64);
    return Uint8Array.from(raw, c => c.charCodeAt(0));
}
function bytesToB64url(buf) {
    const bytes = new Uint8Array(buf);
    let s = '';
    for (const b of bytes) s += String.fromCharCode(b);
    return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export async function subscribePush(vapidPublicKey) {
    if (!pushSupported()) return null;
    const permission = await Notification.requestPermission();
    if (permission !== 'granted') return null;
    const reg = await navigator.serviceWorker.ready;
    let sub = await reg.pushManager.getSubscription();
    if (!sub) sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64urlToBytes(vapidPublicKey) });
    const json = sub.toJSON();
    return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth };
}
export async function currentPushEndpoint() {
    if (!pushSupported()) return null;
    const reg = await navigator.serviceWorker.getRegistration();
    const sub = await reg?.pushManager.getSubscription();
    return sub?.endpoint ?? null;
}
export async function unsubscribePush() {
    const reg = await navigator.serviceWorker.getRegistration();
    const sub = await reg?.pushManager.getSubscription();
    if (!sub) return null;
    const endpoint = sub.endpoint;
    await sub.unsubscribe();
    return endpoint;
}

// ---------- Passkeys / WebAuthn (B-15) ----------
export function passkeysSupported() { return !!window.PublicKeyCredential; }

function decodeCreation(o) {
    o.challenge = b64urlToBytes(o.challenge);
    o.user.id = b64urlToBytes(o.user.id);
    o.excludeCredentials = (o.excludeCredentials || []).map(c => ({ ...c, id: b64urlToBytes(c.id) }));
    return o;
}
function decodeRequest(o) {
    o.challenge = b64urlToBytes(o.challenge);
    o.allowCredentials = (o.allowCredentials || []).map(c => ({ ...c, id: b64urlToBytes(c.id) }));
    return o;
}

export async function createPasskey(optionsJson) {
    const options = decodeCreation(JSON.parse(optionsJson));
    const cred = await navigator.credentials.create({ publicKey: options });
    return JSON.stringify({
        id: cred.id,
        rawId: bytesToB64url(cred.rawId),
        type: cred.type,
        response: {
            attestationObject: bytesToB64url(cred.response.attestationObject),
            clientDataJSON: bytesToB64url(cred.response.clientDataJSON),
            transports: cred.response.getTransports ? cred.response.getTransports() : [],
        },
        clientExtensionResults: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
    });
}

export async function getPasskey(optionsJson) {
    const options = decodeRequest(JSON.parse(optionsJson));
    const cred = await navigator.credentials.get({ publicKey: options, mediation: 'optional' });
    return JSON.stringify({
        id: cred.id,
        rawId: bytesToB64url(cred.rawId),
        type: cred.type,
        response: {
            authenticatorData: bytesToB64url(cred.response.authenticatorData),
            clientDataJSON: bytesToB64url(cred.response.clientDataJSON),
            signature: bytesToB64url(cred.response.signature),
            userHandle: cred.response.userHandle ? bytesToB64url(cred.response.userHandle) : null,
        },
        clientExtensionResults: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
    });
}

// ---------- Sortiermodus: Drag & Drop mit Maus und Touch (F-015) ----------
export function initSortable(container, dotnet, handleSelector) {
    if (!container || container.__sortable) return;
    let dragging = null, placeholder = null, startIndex = -1, offsetY = 0, pointerId = null;
    const items = () => [...container.children].filter(c => c.dataset.sortable !== undefined && c !== placeholder);

    const onDown = e => {
        const handle = e.target.closest(handleSelector);
        if (!handle || !container.contains(handle)) return;
        const item = handle.closest('[data-sortable]');
        if (!item) return;
        e.preventDefault();
        pointerId = e.pointerId;
        handle.setPointerCapture?.(pointerId);
        dragging = item;
        startIndex = items().indexOf(item);
        const rect = item.getBoundingClientRect();
        offsetY = e.clientY - rect.top;
        placeholder = document.createElement('div');
        placeholder.className = 'sort-placeholder';
        placeholder.style.height = rect.height + 'px';
        item.after(placeholder);
        item.classList.add('sort-dragging');
        item.style.width = rect.width + 'px';
        item.style.top = rect.top + 'px';
        item.style.left = rect.left + 'px';
        handle.addEventListener('pointermove', onMove);
        handle.addEventListener('pointerup', onUp, { once: true });
        handle.addEventListener('pointercancel', onUp, { once: true });
    };
    const onMove = e => {
        if (!dragging || e.pointerId !== pointerId) return;
        dragging.style.top = (e.clientY - offsetY) + 'px';
        const others = items().filter(i => i !== dragging);
        let target = null;
        for (const o of others) {
            const r = o.getBoundingClientRect();
            if (e.clientY < r.top + r.height / 2) { target = o; break; }
        }
        if (target) target.before(placeholder); else (others[others.length - 1] ?? placeholder).after(placeholder);
        if (e.clientY < 80) window.scrollBy(0, -12); else if (e.clientY > innerHeight - 80) window.scrollBy(0, 12);
    };
    const onUp = e => {
        const handle = e.currentTarget;
        handle.removeEventListener('pointermove', onMove);
        if (!dragging) return;
        const all = [...container.children].filter(c => c.dataset.sortable !== undefined || c === placeholder);
        const newIndex = all.filter(c => c !== dragging).indexOf(placeholder);
        dragging.classList.remove('sort-dragging');
        dragging.style.width = dragging.style.top = dragging.style.left = '';
        placeholder.remove();
        const from = startIndex;
        dragging = null; placeholder = null;
        if (newIndex >= 0 && newIndex !== from) dotnet.invokeMethodAsync('OnReorder', from, newIndex);
    };
    container.addEventListener('pointerdown', onDown);
    container.__sortable = onDown;
}
export function destroySortable(container) {
    if (container?.__sortable) {
        container.removeEventListener('pointerdown', container.__sortable);
        delete container.__sortable;
    }
}

// ---------- Größenbeobachtung für Diagramme (echte Pixelbreite → lesbare Schrift) ----------
const resizeObservers = new Map();
export function observeWidth(el, dotnet) {
    if (!el) return 0;
    const ro = new ResizeObserver(entries => {
        const w = Math.round(entries[0].contentRect.width);
        if (w > 0 && w !== el.__lastWidth) { el.__lastWidth = w; dotnet.invokeMethodAsync('OnWidth', w); }
    });
    ro.observe(el);
    resizeObservers.set(el, ro);
    return Math.round(el.getBoundingClientRect().width);
}
export function unobserveWidth(el) {
    const ro = resizeObservers.get(el);
    ro?.disconnect();
    resizeObservers.delete(el);
}
