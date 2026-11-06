// 星星考題樂園 離線快取
const CACHE='starexam-v3.4-579731f9c3';
const SHELL=['./','index.html','manifest.webmanifest','data/questions.js?v=579731f9c3','icons/icon-192.png','icons/icon-512.png','icons/apple-touch-icon.png'];
self.addEventListener('install',e=>{ e.waitUntil(caches.open(CACHE).then(c=>c.addAll(SHELL))); });
self.addEventListener('message',e=>{ if(e.data==='skip') self.skipWaiting(); });
self.addEventListener('activate',e=>{ e.waitUntil(caches.keys().then(ks=>Promise.all(ks.filter(k=>k.startsWith('starexam-')&&k!==CACHE).map(k=>caches.delete(k)))).then(()=>self.clients.claim())); });
self.addEventListener('fetch',e=>{
  const req=e.request; if(req.method!=='GET') return;
  const url=new URL(req.url);
  // 頁面：先用網路（拿到最新版），離線時用快取
  if(req.mode==='navigate'){ e.respondWith(fetch(req).then(r=>{ const c=r.clone(); caches.open(CACHE).then(x=>x.put('index.html',c)); return r; }).catch(()=>caches.match('index.html'))); return; }
  // 題庫、附圖、函式庫、字型：快取優先，沒有才下載並存起來
  if(url.origin===location.origin || /fonts\.(googleapis|gstatic)\.com$/.test(url.hostname)){
    e.respondWith(caches.match(req).then(hit=>hit||fetch(req).then(r=>{ if(r.ok||r.type==='opaque'){ const c=r.clone(); caches.open(CACHE).then(x=>x.put(req,c)); } return r; })));
  }
});
