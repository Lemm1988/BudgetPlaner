// Service Worker registrieren (PWA, F-091) – als externe Datei wegen Content-Security-Policy.
if ('serviceWorker' in navigator) {
    navigator.serviceWorker.register('service-worker.js', { updateViaCache: 'none' }).catch(() => { /* z. B. unsicherer Kontext */ });
}
// Theme vor dem ersten Rendern setzen (verhindert Aufblitzen).
try {
    const theme = localStorage.getItem('bp.theme');
    if (theme === 'light' || theme === 'dark') document.documentElement.dataset.theme = theme;
    const lang = localStorage.getItem('bp.lang');
    if (lang === 'en' || lang === 'de') document.documentElement.lang = lang;
} catch { /* privat */ }
