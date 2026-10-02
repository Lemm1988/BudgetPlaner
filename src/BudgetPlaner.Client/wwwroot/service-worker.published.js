// Release: App-Shell offline aus dem Cache (F-091/F-132), Daten kommen aus IndexedDB.
// Push und Benachrichtigungen: sw-common.js. Ein neues Update wartet, bis die App „Aktualisieren“ auslöst.
self.importScripts('./service-worker-assets.js');
self.importScripts('./sw-common.js');

self.addEventListener('install', event => event.waitUntil(onInstall()));
self.addEventListener('activate', event => event.waitUntil(onActivate()));
self.addEventListener('fetch', event => {
    if (shouldHandle(event.request)) event.respondWith(onFetch(event.request));
});

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff2?$/, /\.png$/, /\.svg$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/];
const offlineAssetsExclude = [/^service-worker\.js$/, /^icons\/splash-/];
// Serverseitige Bereiche, die nie aus dem App-Cache bedient werden.
const serverPaths = ['/api/', '/hubs/', '/dev/', '/health'];

const baseUrl = new URL('/', self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

// Versionierte Dateien heißen „name.<fingerprint>.ext“; die App lädt einige über den unversionierten Namen
// (z. B. _framework/dotnet.js). Offline wird dann die versionierte Datei aus dem Cache geliefert.
const fingerprinted = /^(.*)\.([0-9a-z]{10})(\.[^./]+)$/;
const labelToUrl = new Map();
for (const asset of self.assetsManifest.assets) {
    const m = fingerprinted.exec(asset.url);
    if (m) labelToUrl.set(new URL(m[1] + m[3], baseUrl).href, new URL(asset.url, baseUrl).href);
}

function shouldHandle(request) {
    if (request.method !== 'GET') return false;
    const url = new URL(request.url);
    if (url.origin !== self.origin) return false;
    return !serverPaths.some(p => url.pathname.startsWith(p));
}

async function onInstall() {
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    const cache = await caches.open(cacheName);
    await cache.addAll(assetsRequests);
}

async function onActivate() {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
    await self.clients.claim();
}

async function onFetch(request) {
    // Navigationen (z. B. /budget) liefern die App-Shell, außer die URL ist selbst eine Datei aus dem Manifest.
    const isNavigation = request.mode === 'navigate' && !manifestUrlList.includes(request.url);
    const cache = await caches.open(cacheName);
    const cached = await cache.match(isNavigation ? 'index.html' : request)
        ?? (labelToUrl.has(request.url) ? await cache.match(labelToUrl.get(request.url)) : undefined);
    if (cached) return cached;
    try {
        return await fetch(request);
    } catch (err) {
        if (isNavigation) {
            const shell = await cache.match('index.html');
            if (shell) return shell;
        }
        throw err;
    }
}
