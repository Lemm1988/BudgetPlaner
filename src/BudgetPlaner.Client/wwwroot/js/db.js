// Lokaler Offline-Speicher (F-132): IndexedDB mit drei Stores.
//  entities: Haushaltsdaten (Schlüssel "typ:id", Wert = JSON)
//  outbox:   noch nicht übertragene Änderungen (Schlüssel = OpId)
//  meta:     Sequenz, Haushalt, Mitglieder, Eigentümer des Speichers
const DB_NAME = 'budgetplaner';
const DB_VERSION = 1;
let dbPromise = null;

function open() {
    if (!dbPromise) {
        dbPromise = new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                for (const store of ['entities', 'outbox', 'meta']) {
                    if (!db.objectStoreNames.contains(store)) db.createObjectStore(store);
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
            req.onblocked = () => reject(new Error('IndexedDB blockiert'));
        });
    }
    return dbPromise;
}

function run(store, mode, work) {
    return open().then(db => new Promise((resolve, reject) => {
        const tx = db.transaction(store, mode);
        const s = tx.objectStore(store);
        let result;
        work(s, r => { result = r; });
        tx.oncomplete = () => resolve(result);
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
    }));
}

export function available() {
    return typeof indexedDB !== 'undefined';
}

export function getAll(store) {
    return run(store, 'readonly', (s, set) => {
        const items = [];
        const req = s.openCursor();
        req.onsuccess = () => {
            const cursor = req.result;
            if (cursor) {
                items.push({ k: String(cursor.key), v: cursor.value });
                cursor.continue();
            } else {
                set(items);
            }
        };
    });
}

export function get(store, key) {
    return run(store, 'readonly', (s, set) => {
        const req = s.get(key);
        req.onsuccess = () => set(req.result ?? null);
    });
}

export function put(store, key, value) {
    return run(store, 'readwrite', s => { s.put(value, key); });
}

export function putMany(store, entries) {
    if (!entries || entries.length === 0) return Promise.resolve();
    return run(store, 'readwrite', s => {
        for (const e of entries) s.put(e.v, e.k);
    });
}

export function deleteMany(store, keys) {
    if (!keys || keys.length === 0) return Promise.resolve();
    return run(store, 'readwrite', s => {
        for (const k of keys) s.delete(k);
    });
}

export function clear(store) {
    return run(store, 'readwrite', s => { s.clear(); });
}

export async function clearAll() {
    await clear('entities');
    await clear('outbox');
    await clear('meta');
}
