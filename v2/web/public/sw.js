const staticCache = 'taskservice-static-v1';
const offlineFiles = ['/offline.html', '/offline.css'];
const portalPath = /^\/(?:unternehmen\/[a-z0-9-]+(?:\/.*)?|plattform\/?|)$/;
const assetPath = /^\/assets\/[A-Za-z0-9_.-]+-[A-Za-z0-9_-]{6,}\.(?:js|css)$/;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(staticCache).then(cache => cache.addAll(offlineFiles)));
});

self.addEventListener('activate', event => {
  event.waitUntil(Promise.all([
    caches.keys().then(keys => Promise.all(keys
      .filter(key => key.startsWith('taskservice-static-') && key !== staticCache)
      .map(key => caches.delete(key)))),
    self.clients.claim(),
  ]));
});

self.addEventListener('fetch', event => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== 'GET' || url.origin !== self.location.origin) return;

  if (request.mode === 'navigate' && portalPath.test(url.pathname)) {
    event.respondWith(fetch(request).catch(async () =>
      (await caches.match('/offline.html')) || Response.error()));
    return;
  }
  if (url.search || !(assetPath.test(url.pathname) || offlineFiles.includes(url.pathname))) return;

  event.respondWith((async () => {
    const cache = await caches.open(staticCache);
    const match = await cache.match(request);
    if (match) return match;
    const response = await fetch(request);
    if (response.ok && response.type === 'basic' && !/no-store|private/i.test(response.headers.get('Cache-Control') || '')) {
      await cache.put(request, response.clone()).catch(() => {});
    }
    return response;
  })());
});
