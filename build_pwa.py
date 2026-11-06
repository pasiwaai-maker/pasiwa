"""建置星星考題樂園 PWA（可安裝到手機、平板，離線可用）。
輸出：/home/claude/pwa/（直接放到 GitHub Pages 或任何 HTTPS 靜態網站）
"""
import base64, hashlib, json, os, re, shutil, sys

APP = '/home/claude/app'
SRC = '/home/claude/vs/StarExam/StarExam/wwwroot'
ARGS = [a for a in sys.argv[1:] if not a.startswith('--')]
INLINE = '--inline-images' in sys.argv   # 附圖留在題庫檔裡（檔案數少，方便用 GitHub 網頁上傳）
OUT = ARGS[0] if ARGS else '/home/claude/pwa'

shutil.rmtree(OUT, ignore_errors=True)
os.makedirs(OUT + '/data'); os.makedirs(OUT + '/img'); os.makedirs(OUT + '/icons')
shutil.copytree(SRC + '/lib', OUT + '/lib')

# ---- 題庫：把 base64 附圖拆成獨立圖檔，題庫檔變小、圖片用到才下載 ----
bank = json.loads(open(SRC + '/data/questions.js', encoding='utf-8').read().split('=', 1)[1].strip().rstrip(';'))
def save_img(data_uri):
    if INLINE: return data_uri
    m = re.match(r'data:image/(png|jpe?g|gif|webp);base64,(.*)$', data_uri, re.S)
    if not m: return data_uri
    raw = base64.b64decode(m.group(2)); ext = 'jpg' if m.group(1).startswith('jp') else m.group(1)
    name = hashlib.sha1(raw).hexdigest()[:16] + '.' + ext
    p = f'{OUT}/img/{name}'
    if not os.path.exists(p): open(p, 'wb').write(raw)
    return 'img/' + name
for q in bank:
    if q.get('img', '').startswith('data:'): q['img'] = save_img(q['img'])
    if q.get('optImgs'): q['optImgs'] = [save_img(x) if x.startswith('data:') else x for x in q['optImgs']]
qjs = '// 星星考題樂園 題庫\nwindow.QUESTION_BANK = [\n' + ',\n'.join(json.dumps(q, ensure_ascii=False, separators=(',', ':')) for q in bank) + '\n];\n'
open(OUT + '/data/questions.js', 'w', encoding='utf-8').write(qjs)
bank_ver = hashlib.sha1(qjs.encode()).hexdigest()[:10]

# ---- 頁面 ----
t = open(APP + '/template.html', encoding='utf-8').read()
app_ver = re.search(r"APP_VERSION='([\d.]+)'", t).group(1)
v = t.replace('const BANK = /*__BANK__*/[];', 'const BANK = window.QUESTION_BANK || [];')
v = v.replace('<canvas id="confetti"', f'<script src="data/questions.js?v={bank_ver}"></script>\n<canvas id="confetti"', 1)
pwa_js = """<script>
/* PWA：註冊離線快取；有新版本時提示重新整理 */
if('serviceWorker' in navigator){
  window.addEventListener('load',()=>{
    navigator.serviceWorker.register('sw.js').then(reg=>{
      reg.addEventListener('updatefound',()=>{ const nw=reg.installing; if(!nw) return;
        nw.addEventListener('statechange',()=>{ if(nw.state==='installed' && navigator.serviceWorker.controller){
          const b=document.createElement('button'); b.className='btn primary'; b.textContent='有新版本，點這裡更新';
          b.style.cssText='position:fixed;left:50%;bottom:16px;transform:translateX(-50%);z-index:999';
          b.onclick=()=>{ nw.postMessage('skip'); location.reload(); }; document.body.appendChild(b); } }); });
    }).catch(()=>{});
  });
}
/* 「加到主畫面」按鈕（Android Chrome／電腦版 Edge、Chrome） */
let deferredInstall=null;
window.addEventListener('beforeinstallprompt',e=>{ e.preventDefault(); deferredInstall=e; showInstall(); });
function showInstall(){ if(document.getElementById('pwaInstall')) return;
  const b=document.createElement('button'); b.id='pwaInstall'; b.className='btn mint'; b.textContent='📲 安裝到主畫面';
  b.style.cssText='position:fixed;right:12px;bottom:12px;z-index:998';
  b.onclick=async()=>{ if(!deferredInstall) return; deferredInstall.prompt(); await deferredInstall.userChoice; deferredInstall=null; b.remove(); };
  document.body.appendChild(b); }
window.addEventListener('appinstalled',()=>{ const b=document.getElementById('pwaInstall'); if(b) b.remove(); });
</script>"""
i = v.index('</style>') + len('</style>')
head = ('<!doctype html>\n<html lang="zh-Hant">\n<head>\n<meta charset="utf-8">\n'
        '<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">\n'
        '<meta name="theme-color" content="#FFC93C">\n'
        '<meta name="description" content="國小、國中各科段考模擬卷，有詳解、錯題本，可離線練習。">\n'
        '<link rel="manifest" href="manifest.webmanifest">\n'
        '<link rel="icon" href="icons/icon-192.png" type="image/png">\n'
        '<link rel="apple-touch-icon" href="icons/apple-touch-icon.png">\n'
        '<meta name="apple-mobile-web-app-capable" content="yes">\n'
        '<meta name="mobile-web-app-capable" content="yes">\n'
        '<meta name="apple-mobile-web-app-status-bar-style" content="default">\n'
        '<meta name="apple-mobile-web-app-title" content="星星考題">\n')
css_extra = ('<style>body{padding-left:env(safe-area-inset-left);padding-right:env(safe-area-inset-right);'
             'padding-bottom:env(safe-area-inset-bottom)} @media (display-mode: standalone){body{padding-top:env(safe-area-inset-top)}}</style>')
open(OUT + '/index.html', 'w', encoding='utf-8').write(head + v[:i] + css_extra + '\n</head>\n<body>\n' + v[i:] + '\n' + pwa_js + '\n</body>\n</html>\n')

# ---- manifest ----
manifest = {
    "name": "星星考題樂園", "short_name": "星星考題", "lang": "zh-Hant",
    "description": "國小、國中各科段考模擬卷，有詳解、錯題本，可離線練習。",
    "start_url": "./", "scope": "./", "display": "standalone", "orientation": "any",
    "background_color": "#EAF4FF", "theme_color": "#FFC93C",
    "icons": [
        {"src": "icons/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any"},
        {"src": "icons/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any"},
        {"src": "icons/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable"}
    ]
}
json.dump(manifest, open(OUT + '/manifest.webmanifest', 'w', encoding='utf-8'), ensure_ascii=False, indent=2)

# ---- service worker ----
cache = f'starexam-v{app_ver}-{bank_ver}'
sw = """// 星星考題樂園 離線快取
const CACHE='%s';
const SHELL=['./','index.html','manifest.webmanifest','data/questions.js?v=%s','icons/icon-192.png','icons/icon-512.png','icons/apple-touch-icon.png'];
self.addEventListener('install',e=>{ e.waitUntil(caches.open(CACHE).then(c=>c.addAll(SHELL))); });
self.addEventListener('message',e=>{ if(e.data==='skip') self.skipWaiting(); });
self.addEventListener('activate',e=>{ e.waitUntil(caches.keys().then(ks=>Promise.all(ks.filter(k=>k.startsWith('starexam-')&&k!==CACHE).map(k=>caches.delete(k)))).then(()=>self.clients.claim())); });
self.addEventListener('fetch',e=>{
  const req=e.request; if(req.method!=='GET') return;
  const url=new URL(req.url);
  // 頁面：先用網路（拿到最新版），離線時用快取
  if(req.mode==='navigate'){ e.respondWith(fetch(req).then(r=>{ const c=r.clone(); caches.open(CACHE).then(x=>x.put('index.html',c)); return r; }).catch(()=>caches.match('index.html'))); return; }
  // 題庫、附圖、函式庫、字型：快取優先，沒有才下載並存起來
  if(url.origin===location.origin || /fonts\\.(googleapis|gstatic)\\.com$/.test(url.hostname)){
    e.respondWith(caches.match(req).then(hit=>hit||fetch(req).then(r=>{ if(r.ok||r.type==='opaque'){ const c=r.clone(); caches.open(CACHE).then(x=>x.put(req,c)); } return r; })));
  }
});
""" % (cache, bank_ver)
open(OUT + '/sw.js', 'w', encoding='utf-8').write(sw)
open(OUT + '/.nojekyll', 'w').write('')
if INLINE: shutil.rmtree(OUT + '/img', ignore_errors=True)
print('OK', OUT, 'bank', len(bank), 'q.js', len(qjs.encode()) // 1024, 'KB', 'imgs', 0 if INLINE else len(os.listdir(OUT + '/img')), 'cache', cache)
