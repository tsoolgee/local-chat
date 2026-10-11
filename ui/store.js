'use strict';
/* Model store: download models from the catalog or from any link. Uses helpers from app.js. */

let storeTab = 'chat';
let catalog = null;
let downloads = [];
let dlTimer = null;
const visionWanted = {};

const TAGS = {
  recommended: ['מומלץ', ''],
  hebrew: ['עברית טובה', 'blue'],
  fast: ['מהיר', ''],
  smart: ['חכם במיוחד', 'purple'],
};

const fmtSize = b => '⁦' + (b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : Math.max(1, Math.round(b / 1e6)) + ' MB') + '⁩';
const fmtEta = s => s > 3600 ? `${Math.floor(s / 3600)} שע' ${Math.round(s % 3600 / 60)} דק'` : s > 60 ? `${Math.round(s / 60)} דק'` : `${Math.max(1, Math.round(s))} שנ'`;

async function openStore(tab) {
  if (tab) storeTab = tab;
  $('#store').classList.remove('hidden');
  document.querySelectorAll('.store-tabs .tab').forEach(t => t.classList.toggle('on', t.dataset.tab === storeTab));
  $('#storeBody').innerHTML = '<p class="store-intro">טוען…</p>';
  await refreshStore();
}

function closeStore() { $('#store').classList.add('hidden'); }

async function refreshStore() {
  try {
    [catalog, downloads] = await Promise.all([api('catalog'), api('downloads')]);
  } catch (e) { $('#storeBody').innerHTML = `<div class="err-box">${esc(e.message)}</div>`; return; }
  renderStore();
  scheduleDlPoll();
}

function jobFor(id) { return downloads.find(d => d.id === id); }
const active = d => d && (d.state === 'downloading' || d.state === 'queued');

function renderStore() {
  if (!catalog) return;
  const ram = engine.ram ? engine.ram.total : 0;
  $('#diskInfo').textContent = catalog.freeDisk >= 0 ? 'מקום פנוי בדיסק: ' + fmtSize(catalog.freeDisk) : '';
  const body = $('#storeBody');
  if (storeTab === 'url') return renderUrlTab(body);

  const fitId = bestFit(storeTab, ram);
  // the one that fits this computer comes first
  const list = catalog.models.filter(m => m.kind === storeTab).sort((a, b) => (b.id === fitId) - (a.id === fitId));
  const intro = storeTab === 'chat'
    ? 'מודל גדול יותר = חכם יותר אבל איטי יותר וצורך יותר זיכרון. לרוב המחשבים כדאי להתחיל ב-<b>Qwen3.5 9B</b> או ב-<b>Gemma 4 12B</b> לעברית.'
    : 'מודלי תמונות עובדים על המעבד ולוקח להם זמן: מדקה בערך (SD-Turbo) ועד כ-10 דקות (Z-Image).';
  body.innerHTML = `<p class="store-intro">${intro}</p><div class="cards"></div>` +
    `<div class="store-note">הקבצים יורדים מ-Hugging Face לתיקייה <b>${storeTab === 'image' ? 'מודלים\\תמונות' : 'מודלים'}</b>. אם ההורדה נקטעת, היא תמשיך מאותה נקודה.</div>`;
  const cards = $('.cards', body);
  for (const m of list) cards.append(buildCard(m, ram));
}

/** The best model of a kind for this computer: the biggest one that leaves room for the other kind of model. */
function bestFit(kind, ram) {
  const gb = ram / 1073741824;
  if (!gb) return null;
  const share = kind === 'chat' ? 0.6 : 0.4;
  // very slow image models (minutes per picture) are never the default suggestion
  const list = catalog.models.filter(m => m.kind === kind && !(kind === 'image' && m.speed === 'איטי')).sort((a, b) => a.ram - b.ram);
  const fits = list.filter(m => m.ram <= gb * share);
  if (!fits.length) return list[0] && list[0].id;
  const top = fits[fits.length - 1].ram;
  // among equally big ones prefer "recommended", then Hebrew
  const same = fits.filter(m => m.ram === top);
  return (same.find(m => (m.tags || []).includes('recommended')) || same.find(m => (m.tags || []).includes('hebrew')) || same[0]).id;
}

function buildCard(m, ram) {
  const job = jobFor(m.id);
  const optional = m.files.filter(f => f.optional);
  const required = m.files.filter(f => !f.optional);
  if (visionWanted[m.id] === undefined) visionWanted[m.id] = optional.some(f => f.installed);
  const size = required.reduce((a, f) => a + f.size, 0) + (visionWanted[m.id] ? optional.reduce((a, f) => a + f.size, 0) : 0);
  const tooBig = ram && m.ram * 1073741824 > ram * 0.95;

  const c = el('div', 'card' + (m.installed ? ' installed' : ''));
  c.innerHTML = `<h4>${esc(m.name)}</h4>
    <div class="meta">${bestFit(m.kind, ram) === m.id ? '<span class="tag fit">✓ מתאים למחשב שלך</span>' : ''}${(m.tags || []).map(t => TAGS[t] ? `<span class="tag ${TAGS[t][1]}">${TAGS[t][0]}</span>` : '').join('')}</div>
    <div class="desc">${esc(m.desc)}</div>
    <div class="meta"><span class="m">${fmtSize(size)}</span><span class="m${tooBig ? ' bad' : ''}">זיכרון: ⁦${m.ram}GB+⁩</span><span class="m">מהירות: ${esc(m.speed || '')}</span></div>`;

  if (optional.length) {
    const optSize = optional.reduce((a, f) => a + f.size, 0);
    const lab = el('label', 'opt', `<input type="checkbox"> גם הבנת תמונות (עוד ${fmtSize(optSize)})`);
    const cb = $('input', lab);
    cb.checked = !!visionWanted[m.id];
    cb.disabled = active(job);
    cb.onchange = () => { visionWanted[m.id] = cb.checked; renderStore(); };
    c.append(lab);
  }

  if (active(job)) {
    const pct = job.total ? Math.min(100, job.done / job.total * 100) : 0;
    const eta = job.speed > 0 ? fmtEta((job.total - job.done) / job.speed) : '';
    c.append(el('div', 'prog', `<div style="width:${pct.toFixed(1)}%"></div>`));
    c.append(el('div', 'prog-text', `<span>${pct.toFixed(0)}% · ${fmtSize(job.done)} מתוך ${fmtSize(job.total)}</span><span>${job.speed > 0 ? fmtSize(job.speed) + ' לשנייה · עוד ' + eta : 'מתחבר…'}</span>`));
  }
  if (job && job.state === 'error') c.append(el('div', 'err', esc(job.error)));

  const acts = el('div', 'actions');
  const btn = (text, cls, fn) => { const b = el('button', 'btn ' + cls, text); b.onclick = fn; acts.append(b); return b; };
  const needsOptional = visionWanted[m.id] && optional.some(f => !f.installed);
  if (active(job)) {
    btn('ביטול', '', () => dlAction('download-cancel', m.id));
  } else if (m.installed && !needsOptional) {
    const eng = m.kind === 'image' ? imgEngine() : engine;
    const loaded = eng.model === m.path && eng.state !== 'idle';
    btn(loaded ? 'בשימוש ✓' : 'השתמש במודל', loaded ? '' : 'primary', () => useModel(m)).disabled = loaded;
    btn('מחיקה', 'ghost', () => deleteModel(m));
  } else {
    const partial = m.files.some(f => f.partial > 0);
    btn(m.installed ? 'הורדת ההרחבה' : partial ? 'המשך הורדה' : 'הורדה', 'primary', () => startDownload(m));
    if (m.files.some(f => f.installed || f.partial)) btn('מחיקה', 'ghost', () => deleteModel(m));
  }
  c.append(acts);
  return c;
}

function renderUrlTab(body) {
  body.innerHTML = `<div class="url-box">
      <p class="store-intro">אפשר להוריד כל מודל: מדביקים קישור לקובץ (למשל מדף הקבצים של המודל ב-Hugging Face).
      למודל שיחה צריך קובץ <b>.gguf</b> (מומלץ בגרסה Q4_K_M), ולמודל תמונות קובץ <b>.safetensors</b> או <b>.gguf</b>.</p>
      <div class="row">
        <input id="dlUrl" dir="ltr" placeholder="https://huggingface.co/…/resolve/main/model-Q4_K_M.gguf">
        <select id="dlKind"><option value="chat">מודל שיחה</option><option value="image">מודל תמונות</option></select>
        <button class="btn primary" id="dlGo">הורדה</button>
      </div>
    </div>
    <div class="dl-list" id="dlList"></div>`;
  $('#dlUrl').value = renderUrlTab.last || '';
  $('#dlGo').onclick = async () => {
    const url = $('#dlUrl').value.trim();
    if (!url) return;
    renderUrlTab.last = url;
    try {
      await api('download', { method: 'POST', body: { url, kind: $('#dlKind').value } });
      toast('ההורדה התחילה');
      $('#dlUrl').value = ''; renderUrlTab.last = '';
      downloads = await api('downloads'); renderDlList(); scheduleDlPoll();
    }
    catch (e) { toast(e.message, 5000); }
  };
  renderDlList();
}

function renderDlList() {
  const box = $('#dlList');
  if (!box) return;
  const items = downloads.filter(d => d.id.startsWith('url-'));
  box.innerHTML = '';
  for (const d of items) {
    const it = el('div', 'dl-item');
    const pct = d.total ? Math.min(100, d.done / d.total * 100) : 0;
    const stateText = { downloading: d.speed > 0 ? `${pct.toFixed(0)}% · ${fmtSize(d.speed)} לשנייה` : 'מתחבר…', queued: 'ממתין', done: 'הסתיים ✓', error: 'נכשל', cancelled: 'בוטל' }[d.state] || d.state;
    it.innerHTML = `<div class="top"><b>${esc(d.name)}</b><span class="disk">${stateText}</span></div>`;
    if (active(d)) it.append(el('div', 'prog', `<div style="width:${pct.toFixed(1)}%"></div>`));
    if (d.error) it.append(el('div', 'err-box', esc(d.error)));
    const row = el('div', 'actions');
    const b = (t, fn) => { const x = el('button', 'btn', t); x.onclick = fn; row.append(x); };
    if (active(d)) b('ביטול', () => dlAction('download-cancel', d.id));
    else b('הסרה מהרשימה', () => dlAction('download-forget', d.id));
    it.append(row);
    box.append(it);
  }
}

async function startDownload(m) {
  try {
    await api('download', { method: 'POST', body: { id: m.id, vision: !!visionWanted[m.id] } });
    toast('ההורדה התחילה. אפשר להמשיך להשתמש בתוכנה בינתיים.');
    await refreshStore();
  } catch (e) { toast(e.message, 5000); }
}

async function dlAction(route, id) {
  try { await api(route, { method: 'POST', body: { id } }); } catch (e) { toast(e.message, 5000); }
  refreshStore();
}

async function deleteModel(m) {
  if (!confirm(`למחוק את ${m.name} מהמחשב?`)) return;
  try { await api('model-delete', { method: 'POST', body: { id: m.id } }); toast('נמחק'); } catch (e) { toast(e.message, 5000); }
  await refreshModels(); pollState(); refreshStore();
}

async function useModel(m) {
  closeStore();
  await refreshModels();
  if (m.kind === 'image') { await saveSettings({ mode: 'image' }); loadImageModel(m.path); }
  else { if (S.mode === 'image') await saveSettings({ mode: 'chat' }); loadModel(m.path); }
}

/* poll while something downloads; notify when done */
const announced = new Set();
function scheduleDlPoll() {
  clearTimeout(dlTimer);
  const anyActive = downloads.some(active);
  const open = !$('#store').classList.contains('hidden');
  const badge = $('#dlBadge');
  const n = downloads.filter(active).length;
  badge.textContent = n; badge.classList.toggle('hidden', !n);
  if (anyActive || open) dlTimer = setTimeout(pollDownloads, anyActive ? 1000 : 4000);
}

async function pollDownloads() {
  try {
    const before = downloads;
    downloads = await api('downloads');
    let changed = false;
    for (const d of downloads) {
      const was = before.find(x => x.id === d.id);
      if (was && active(was) && !active(d)) changed = true;
      if (d.state === 'done' && was && active(was) && !announced.has(d.id)) {
        announced.add(d.id);
        onDownloaded(d);
      }
    }
    if (!$('#store').classList.contains('hidden')) {
      if (changed) catalog = await api('catalog');
      // the link tab has an input the user may be typing in: refresh only its list
      if (storeTab === 'url') renderDlList(); else renderStore();
    }
  } catch { }
  scheduleDlPoll();
}

async function onDownloaded(d) {
  await refreshModels();
  toast(`${d.name} ירד בהצלחה`, 4000);
  // first model of its kind: load it right away
  const eng = d.kind === 'image' ? imgEngine() : engine;
  if (!eng.model || eng.state === 'idle' || eng.state === 'error') {
    if (d.kind === 'image') loadImageModel(d.path); else loadModel(d.path);
  }
  renderEngine();
}

/* wiring */
$('#openStore').onclick = () => openStore();
$('#store').addEventListener('click', e => { if (e.target.id === 'store' || e.target.closest('[data-close-store]')) closeStore(); });
document.querySelectorAll('.store-tabs .tab').forEach(t => t.onclick = () => {
  storeTab = t.dataset.tab;
  document.querySelectorAll('.store-tabs .tab').forEach(x => x.classList.toggle('on', x === t));
  renderStore();
});
api('downloads').then(d => { downloads = d || []; scheduleDlPoll(); }).catch(() => { });
