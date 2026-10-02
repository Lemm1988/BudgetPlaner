// Entwicklung: kein Offline-Cache, damit Änderungen sofort sichtbar sind.
// Push und Benachrichtigungen funktionieren wie im Release.
self.importScripts('./sw-common.js');

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', () => { });
