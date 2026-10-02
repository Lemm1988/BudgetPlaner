// Gemeinsame Service-Worker-Logik für Entwicklung und Release:
// Web-Push (F-080/F-081), Klick auf Benachrichtigung und kontrolliertes Update (F-091).

self.addEventListener('message', event => {
    if (event.data && event.data.type === 'SKIP_WAITING') self.skipWaiting();
});

self.addEventListener('push', event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; }
    catch { data = { body: event.data ? event.data.text() : '' }; }

    const options = {
        body: data.body || '',
        icon: 'icons/icon-192.png',
        badge: 'icons/favicon-32.png',
        data: { url: data.url || '/benachrichtigungen' },
    };
    if (data.tag) { options.tag = data.tag; options.renotify = true; }

    event.waitUntil((async () => {
        await self.registration.showNotification(data.title || 'BudgetPlaner', options);
        // Geöffnete Fenster aktualisieren ihre Glocke sofort.
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        windows.forEach(w => w.postMessage({ type: 'push' }));
    })());
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const target = new URL(event.notification.data?.url || '/uebersicht', self.location.origin);
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const open = windows.find(w => new URL(w.url).origin === target.origin);
        if (open) {
            // Innerhalb der laufenden App navigieren (kein Neuladen).
            open.postMessage({ type: 'navigate', url: target.pathname + target.search + target.hash });
            await open.focus();
        } else {
            await self.clients.openWindow(target.href);
        }
    })());
});
