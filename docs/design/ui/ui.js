/* gitfs UI — рабочая логика экранов.
   Одно состояние на все поверхности: диалог, трей, менеджер, онбординг.
   Смонтировал в диалоге — строка появилась в менеджере и в меню трея,
   буква ушла из свободных. Это и есть контракт поведения для Avalonia-порта. */
'use strict';

(() => {
  // ---------- данные ----------
  // Известные репозитории демо-машины: остальные пути валидацию не проходят.
  const REPOS = {
    'C:\\src\\gitfs':        'gitfs',
    'C:\\src\\avalonia-lab': 'avalonia-lab',
    'C:\\src\\docs-site':    'docs-site',
    'C:\\src\\terminal':     'terminal',
  };
  const LETTER_POOL = ['G', 'H', 'I', 'J', 'K', 'L', 'M', 'N'];
  const VIEW_KEYS = ['branches', 'tags', 'commits', 'dates', 'history'];

  const state = {
    repo: 'C:\\src\\gitfs',
    mode: 'drive',            // drive | folder
    letter: null,             // null → первая свободная
    folder: 'C:\\mnt\\gitfs',
    views: { branches: true, tags: true, commits: true, dates: false, history: true },
    adv: { ref: 'HEAD', commitLimit: 200, historyLimit: 500, cacheMb: 256, blobMb: 8,
           policy: 'native', readOnly: true, keepOverlay: true },
    mounts: [
      { repo: 'gitfs',        path: 'C:\\src\\gitfs',        mount: 'G:', views: 'b t c d h', uptime: '4h 12m', status: 'ok' },
      { repo: 'avalonia-lab', path: 'C:\\src\\avalonia-lab', mount: 'H:', views: 'b · · · h', uptime: '22m',    status: 'ok' },
      { repo: 'docs-site',    path: 'C:\\src\\docs-site',    mount: 'I:', views: 'b t c d h', uptime: '1h 03m', status: 'reopening' },
    ],
    selected: 'docs-site',
    submenuFor: null,
    driverError: false,
    winfsp: 'missing',        // missing | installing | ok
  };

  // Статистика панели деталей — по репозиториям (в проде читается из .gitfs/status.txt).
  const STATS = {
    'gitfs':        { cache: '203 MB', objects: '61 402', packs: '4',  reopens: '0' },
    'avalonia-lab': { cache: '96 MB',  objects: '18 774', packs: '2',  reopens: '0' },
    'docs-site':    { cache: '184 MB', objects: '42 118', packs: '7',  reopens: '3' },
  };
  const LOGS = {
    'gitfs':        '12:40:03 mounted G: in 1.2s',
    'avalonia-lab': '16:30:44 mounted H: in 0.6s',
    'docs-site':    '14:02:11 pack reopened (3)\n14:02:11 history\\ limit 200 reached\n13:58:04 mounted I: in 0.9s',
  };

  const $ = (sel, root) => (root || document).querySelector(sel);
  const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
  const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  const icon = (id, size, cls) =>
    `<svg width="${size}" height="${size}"${cls ? ` class="${cls}"` : ''} aria-hidden="true"><use href="#${id}"></use></svg>`;

  const takenLetters = () => state.mounts.map(m => m.mount.replace(':', ''));
  const freeLetters  = () => LETTER_POOL.filter(l => !takenLetters().includes(l));
  const currentLetter = () => state.letter || freeLetters()[0] || '—';
  const repoName = p => REPOS[p] || null;
  const viewsPattern = v => VIEW_KEYS.map(k => (v[k] ? k[0] : '·')).join(' ');

  // ---------- диалог ----------
  function renderLetters() {
    const sel = $('#f-letter');
    const cur = currentLetter();
    sel.innerHTML =
      freeLetters().map(l => `<option value="${l}"${l === cur ? ' selected' : ''}>${l}:</option>`).join('') +
      takenLetters().map(l => `<option value="${l}"${l === cur ? ' selected' : ''}>${l}: · in use</option>`).join('');
    $('#letters-hint').textContent = 'free: ' + freeLetters().join(' ');
    const busy = takenLetters().includes(cur);
    sel.classList.toggle('is-err', busy);
    const msg = $('#letter-msg');
    msg.hidden = !busy;
    if (busy) $('#letter-msg-text').innerHTML =
      `${cur}: is already in use. <a href="#" id="use-free">Use ${freeLetters()[0]}:</a> — the first free letter.`;
  }

  function renderRepo() {
    const input = $('#f-repo');
    const known = !!repoName(state.repo);
    input.classList.toggle('is-err', !known);
    $('#repo-msg').hidden = known;
  }

  function renderTree() {
    const v = state.views;
    const rows = [];
    const row = (pad, ic, text, cls) =>
      rows.push(`<div class="trow${cls ? ' ' + cls : ''}" style="padding-left:${pad}px">${ic}<span>${text}</span></div>`);
    const target = state.mode === 'drive' ? currentLetter() + ':\\' : esc(state.folder) + '\\';
    row(0, icon('ic-mark-min', 14), target);
    row(15, icon('ic-branches', 14), 'branches\\', v.branches ? '' : 'off');
    row(15, icon('ic-tags', 14), 'tags\\', v.tags ? '' : 'off');
    row(15, icon('ic-commits', 14), 'commits\\', v.commits ? '' : 'off');
    row(15, icon('ic-dates', 14), 'dates\\', v.dates ? '' : 'off');
    row(15, icon('ic-history', 14), 'history\\', v.history ? 'acc' : 'off');
    if (v.history) {
      row(30, icon('ic-folder', 14), 'src\\', 'dim');
      row(45, icon('ic-folder', 14), 'Program.cs\\', 'acc');
      rows.push('<div class="trow dim" style="padding-left:60px"><span>0001-a3f9c21.cs</span></div>');
      rows.push('<div class="trow dim" style="padding-left:60px"><span>…</span></div>');
      rows.push(`<div class="trow acc" style="padding-left:60px"><span>latest.cs</span></div>`);
    }
    $('#tree').innerHTML = rows.join('');
  }

  function advSummary() {
    const a = state.adv;
    return `${a.ref} · ${a.commitLimit}/${a.historyLimit} · ${a.cacheMb} MB · ${a.readOnly ? 'read-only' : 'writable'}`;
  }

  function renderDialogChrome() {
    $('#adv-sum').textContent = advSummary();
    $('#foot-flags').textContent =
      (state.adv.readOnly ? 'read-only' : 'writable overlay') + ' · overlay ' + (state.adv.keepOverlay ? 'kept' : 'discarded');
    $('#mount-btn').textContent =
      state.mode === 'drive' ? `Mount to ${currentLetter()}:` : 'Mount';
    $('#mp-drive-wrap').hidden = state.mode !== 'drive';
    $('#mp-folder-wrap').hidden = state.mode !== 'folder';
  }

  function renderDialog() { renderLetters(); renderRepo(); renderTree(); renderDialogChrome(); }

  // ---------- трей ----------
  function trayState() {
    if (state.driverError) return 'error';
    if (state.mounts.some(m => m.status !== 'ok')) return 'degraded';
    return state.mounts.length ? 'mounted' : 'idle';
  }

  function renderTrayIcon() {
    const st = trayState();
    const n = state.mounts.length;
    const big = $('#tray-32'), small = $('#tray-16');
    const mk = (size, badge) =>
      `${icon('ic-mark-min', size)}${badge}`;
    let b32 = '', b16 = '', color = 'var(--color-neutral-400)';
    if (st === 'mounted') {
      color = 'var(--color-accent)';
      b32 = `<span class="badge-num">${n}</span>`;
      b16 = '<span class="badge-dot"></span>';            // в 16 px число выпадает, остаётся точка
    } else if (st === 'degraded') {
      color = 'var(--color-neutral-300)';
      b32 = `<span class="badge-ov warn">${icon('ic-warn', 15)}</span>`;
      b16 = `<span class="badge-ov warn">${icon('ic-warn', 9)}</span>`;
    } else if (st === 'error') {
      color = 'var(--color-neutral-300)';
      b32 = `<span class="badge-ov err">${icon('ic-err', 15)}</span>`;
      b16 = `<span class="badge-ov err">${icon('ic-err', 9)}</span>`;
    }
    big.style.color = color; small.style.color = color;
    big.innerHTML = mk(32, b32); small.innerHTML = mk(16, b16);
    $('#tray-state-label').textContent =
      ({ idle: 'Idle', mounted: `Mounted · ${n}`, degraded: 'Degraded', error: 'Driver error' })[st];
    $('#drv-toggle').textContent = state.driverError ? 'Clear driver error' : 'Simulate driver error';
  }

  function renderTrayMenu() {
    const box = $('#tray-menu');
    let html = '<div class="menu-head">Mounted</div>';
    if (!state.mounts.length) html += '<div class="menu-item quiet">Nothing mounted</div>';
    for (const m of state.mounts) {
      const open = state.submenuFor === m.repo;
      html += `<button class="menu-item" data-tray-repo="${m.repo}" aria-expanded="${open}">
        <span class="dot ${m.status === 'ok' ? 'ok' : 'warn'}"></span>
        <span class="name">${m.repo}</span>
        ${m.status === 'ok'
          ? `<span class="right">${m.mount}</span>${icon('ic-chev', 14, 'chev')}`
          : '<span class="state-warn">reopening…</span>'}
      </button>`;
    }
    html += `<div class="menu-sep"></div>
      <button class="menu-item" id="tray-mount">Mount…<span class="kbd">Ctrl+M</span></button>
      <button class="menu-item" id="tray-doctor">Doctor</button>
      <button class="menu-item">Settings</button>
      <div class="menu-sep"></div>
      <button class="menu-item quiet">Quit gitfs</button>`;
    box.innerHTML = html;

    const sub = $('#tray-submenu');
    const m = state.mounts.find(x => x.repo === state.submenuFor);
    sub.hidden = !m;
    if (m) {
      sub.innerHTML = `
        <div class="menu-path">${esc(m.path)} → ${m.mount}\\</div>
        <div class="menu-sep"></div>
        <button class="menu-item default-act" data-sub="open">Open in Explorer</button>
        <button class="menu-item" data-sub="log">Show log</button>
        <button class="menu-item" data-sub="copy">Copy path</button>
        <div class="menu-sep"></div>
        <button class="menu-item danger" data-sub="unmount">Unmount</button>`;
    }
  }

  // ---------- менеджер ----------
  function renderManager() {
    const wrap = $('#mgr-body');
    if (!state.mounts.length) {
      wrap.innerHTML = `<div class="empty">
        ${icon('ic-folder', 34, 'eic')}
        <div class="e1">Nothing is mounted</div>
        <p>Point gitfs at a repository and its history shows up as a drive you can browse.</p>
        <button class="btn btn-primary" id="empty-mount">Mount a repository</button>
      </div>`;
      return;
    }
    if (!state.mounts.some(m => m.repo === state.selected)) state.selected = state.mounts[0].repo;
    const rows = state.mounts.map(m => `
      <tr data-repo="${m.repo}" aria-selected="${m.repo === state.selected}">
        <td class="mono">${m.repo}</td>
        <td class="mono">${m.mount}</td>
        <td class="mono muted">${m.views}</td>
        <td class="mono muted">${m.uptime}</td>
        <td><span class="statuscell ${m.status === 'ok' ? 'ok' : 'warn'}"><span class="dot"></span>${m.status}</span></td>
      </tr>`).join('');
    const sel = state.mounts.find(m => m.repo === state.selected);
    const st = STATS[sel.repo] || { cache: '—', objects: '—', packs: '—', reopens: '—' };
    wrap.innerHTML = `
      <div class="mgr-grid">
        <div class="mgr-table"><table class="table">
          <thead><tr><th>Repository</th><th>Mount</th><th>Views</th><th>Uptime</th><th>Status</th></tr></thead>
          <tbody>${rows}</tbody>
        </table></div>
        <div class="details">
          <div class="dname">${sel.repo}</div>
          <div class="dpath">${esc(sel.path)} → ${sel.mount}\\</div>
          <div class="kv">
            <div><div class="k">Cache</div><div class="v">${st.cache}</div></div>
            <div><div class="k">Objects</div><div class="v">${st.objects}</div></div>
            <div><div class="k">Packs</div><div class="v">${st.packs}</div></div>
            <div><div class="k">Reopens</div><div class="v${st.reopens !== '0' ? ' warn' : ''}">${st.reopens}</div></div>
          </div>
          <div class="logcap">log.txt</div>
          <div class="term term-mini"><pre>${esc(LOGS[sel.repo] || '')}</pre></div>
          <div class="row" style="margin-top:14px">
            <button class="btn btn-secondary" disabled title="Демо: открыло бы ${sel.mount}\\ в Проводнике">Open</button>
            <button class="btn btn-ghost" style="color:var(--err)" id="mgr-unmount">Unmount</button>
          </div>
        </div>
      </div>`;
  }

  // ---------- doctor ----------
  function renderDoctor() {
    const rows = [];
    let ok = 0, warn = 0, fail = 0;
    const put = (st, name, val, fix, link) => {
      const pad = name.padEnd(22, ' ');
      const stTxt = { ok: '<span class="ok">ok  </span>', warn: '<span class="warn">warn</span>', fail: '<span class="err">fail</span>' }[st];
      ({ ok: () => ok++, warn: () => warn++, fail: () => fail++ })[st]();
      rows.push(`${stTxt} ${pad} ${val}`);
      if (fix) rows.push(`     <span class="dim">→ ${fix}</span>`);
      if (link) rows.push(`     <span class="dim2">  ${link}</span>`);
    };
    if (state.winfsp === 'ok') put('ok', 'winfsp', '1.12.22339');
    else put('fail', 'winfsp', 'not installed', 'install from winfsp.dev/rel', 'docs.gitfs.dev/e/winfsp-missing');
    if (state.driverError) put('fail', 'driver', 'not responding', 'restart with gitfs doctor --restart-driver', 'docs.gitfs.dev/e/driver');
    else put('ok', 'driver', 'running');
    put('ok', 'drive letters', freeLetters().join(' ') + ' free');
    put('warn', 'cache', '241 MB / 256 MB  (94%)', 'raise with gitfs mount --cache 512');
    const reopening = state.mounts.find(m => m.status === 'reopening');
    if (reopening) put('warn', 'packs', `reopening on ${reopening.mount} (git gc?)`, 'safe to wait; see .gitfs\\log.txt');
    rows.push('');
    rows.push(`<span class="dim">${ok} ok · ${warn} warning${warn === 1 ? '' : 's'} · ${fail} failure${fail === 1 ? '' : 's'}</span>`);
    $('#doctor-pre').innerHTML = '<span class="p">$ </span>gitfs doctor\n\n' + rows.join('\n');
  }

  // ---------- онбординг ----------
  function renderOnboarding() {
    const st = state.winfsp;
    const row = $('#onb-winfsp');
    if (st === 'ok') {
      row.innerHTML = `<span class="st ok">${icon('ic-check', 11)}</span><span>WinFsp found</span><span class="right">1.12.22339</span>`;
      $('#onb-install').hidden = true;
      $('#onb-next').hidden = false;
      $('#onb-foot').textContent = 'Шаг 2 из 2 · всё готово к первому монтированию';
    } else if (st === 'installing') {
      row.innerHTML = `<span class="st warn">${icon('ic-warn', 11)}</span><span>Installing WinFsp…</span><span class="right">~4 MB</span>`;
    } else {
      row.innerHTML = `<span class="st warn">${icon('ic-warn', 11)}</span><span>WinFsp missing</span><span class="right">~4 MB · no reboot</span>`;
    }
  }

  // ---------- общий рендер ----------
  function renderAll() {
    renderDialog(); renderTrayIcon(); renderTrayMenu(); renderManager(); renderDoctor(); renderOnboarding();
  }

  // ---------- действия ----------
  function mount() {
    if (!repoName(state.repo)) { renderRepo(); return; }
    if (state.mode === 'drive' && takenLetters().includes(currentLetter())) { renderLetters(); return; }
    const name = repoName(state.repo);
    const target = state.mode === 'drive' ? currentLetter() + ':' : state.folder;
    state.mounts.push({ repo: name, path: state.repo, mount: target, views: viewsPattern(state.views), uptime: '0m', status: 'ok' });
    STATS[name] = STATS[name] || { cache: '0 MB', objects: '—', packs: '—', reopens: '0' };
    LOGS[name] = `${new Date().toTimeString().slice(0, 8)} mounted ${target} in 1.2s`;
    state.selected = name;
    state.letter = null; // следующая свободная
    renderAll();
    const enabled = VIEW_KEYS.filter(k => state.views[k]).length;
    $('#toast-t1').innerHTML = `Mounted <span class="mono">${name}</span> to <span class="mono">${esc(target)}\\</span>`;
    $('#toast-t2').textContent = `${enabled} view${enabled === 1 ? '' : 's'} · 1.2 s`;
    const toast = $('#toast');
    toast.hidden = false;
    clearTimeout(mount._t);
    mount._t = setTimeout(() => { toast.hidden = true; }, 6000);
  }

  function unmount(repo) {
    state.mounts = state.mounts.filter(m => m.repo !== repo);
    if (state.submenuFor === repo) state.submenuFor = null;
    renderAll();
  }

  // ---------- события ----------
  function wire() {
    // тема
    $('#theme-toggle').addEventListener('click', () => {
      const root = document.documentElement;
      const next = root.getAttribute('data-theme') === 'light' ? 'dark' : 'light';
      root.setAttribute('data-theme', next);
      $('#theme-toggle').textContent = next === 'light' ? 'Тёмная тема' : 'Светлая тема';
    });

    // диалог: репозиторий
    $('#f-repo').addEventListener('input', e => { state.repo = e.target.value; renderRepo(); renderTree(); renderDialogChrome(); });
    $('#repo-chips').addEventListener('click', e => {
      const chip = e.target.closest('[data-path]');
      if (!chip) return;
      state.repo = chip.getAttribute('data-path');
      $('#f-repo').value = state.repo;
      renderDialog();
    });

    // точка монтирования
    $$('input[name="mp"]').forEach(r => r.addEventListener('change', e => {
      state.mode = e.target.value; renderDialog();
    }));
    $('#f-letter').addEventListener('change', e => { state.letter = e.target.value; renderDialog(); });
    $('#letter-msg').addEventListener('click', e => {
      if (e.target.id !== 'use-free') return;
      e.preventDefault();
      state.letter = freeLetters()[0];
      renderDialog();
    });
    $('#f-folder').addEventListener('input', e => { state.folder = e.target.value; renderTree(); });

    // вьюхи
    $('#viewlist').addEventListener('change', e => {
      const key = e.target.getAttribute('data-view');
      if (!key) return;
      state.views[key] = e.target.checked;
      renderTree(); renderTrayMenu();
    });

    // advanced
    $('#adv-head').addEventListener('click', () => {
      const adv = $('#adv');
      if (adv.hasAttribute('data-open')) adv.removeAttribute('data-open'); else adv.setAttribute('data-open', '');
    });
    const num = (id, key) => $(id).addEventListener('input', e => {
      const v = parseInt(e.target.value, 10);
      if (!Number.isNaN(v)) state.adv[key] = v;
      renderDialogChrome();
    });
    $('#a-ref').addEventListener('input', e => { state.adv.ref = e.target.value || 'HEAD'; renderDialogChrome(); });
    num('#a-commit', 'commitLimit'); num('#a-history', 'historyLimit');
    num('#a-cache', 'cacheMb'); num('#a-blob', 'blobMb');
    $$('input[name="policy"]').forEach(r => r.addEventListener('change', e => { state.adv.policy = e.target.value; }));
    $('#a-readonly').addEventListener('change', e => { state.adv.readOnly = e.target.checked; renderDialogChrome(); });
    $('#a-keep').addEventListener('change', e => { state.adv.keepOverlay = e.target.checked; renderDialogChrome(); });

    $('#mount-btn').addEventListener('click', mount);
    $('#toast-open').addEventListener('click', () => { $('#toast').hidden = true; });

    // трей
    $('#tray-menu').addEventListener('click', e => {
      const item = e.target.closest('[data-tray-repo]');
      if (item) {
        const repo = item.getAttribute('data-tray-repo');
        state.submenuFor = state.submenuFor === repo ? null : repo;
        renderTrayMenu();
        return;
      }
      if (e.target.closest('#tray-mount')) location.hash = '#mount-dialog';
      if (e.target.closest('#tray-doctor')) location.hash = '#doctor';
    });
    $('#tray-submenu').addEventListener('click', e => {
      const b = e.target.closest('[data-sub]');
      if (!b) return;
      const m = state.mounts.find(x => x.repo === state.submenuFor);
      if (!m) return;
      const act = b.getAttribute('data-sub');
      if (act === 'unmount') unmount(m.repo);
      if (act === 'copy') { try { navigator.clipboard.writeText(m.mount + '\\'); } catch (_) { /* демо */ } }
      if (act === 'log') { state.selected = m.repo; renderManager(); location.hash = '#manager'; }
    });
    $('#drv-toggle').addEventListener('click', () => {
      state.driverError = !state.driverError;
      renderTrayIcon(); renderDoctor();
      // Контракт канвы: driver error — единственное состояние, открывающее окно
      // без клика. В демо авто-открытие показано переходом к doctor.
      if (state.driverError) location.hash = '#doctor';
    });

    // менеджер (делегирование — содержимое перерисовывается)
    $('#mgr-body').addEventListener('click', e => {
      const row = e.target.closest('tr[data-repo]');
      if (row) { state.selected = row.getAttribute('data-repo'); renderManager(); return; }
      if (e.target.closest('#mgr-unmount')) { unmount(state.selected); return; }
      if (e.target.closest('#empty-mount')) location.hash = '#mount-dialog';
    });
    $('#mgr-mount').addEventListener('click', () => { location.hash = '#mount-dialog'; });
    $('#mgr-doctor').addEventListener('click', () => { location.hash = '#doctor'; });

    // онбординг
    $('#onb-install').addEventListener('click', () => {
      state.winfsp = 'installing';
      renderOnboarding();
      setTimeout(() => { state.winfsp = 'ok'; renderOnboarding(); renderDoctor(); }, 1100);
    });
    $('#onb-next').addEventListener('click', () => { location.hash = '#mount-dialog'; });
  }

  document.addEventListener('DOMContentLoaded', () => { wire(); renderAll(); });
})();
