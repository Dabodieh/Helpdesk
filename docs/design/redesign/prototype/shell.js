/* Helpdesk redesign prototype — shared shell (rail, top bar, theme, palette concept, icons). Prototype only. */
(function () {
  var KEY_THEME = 'hd-theme';
  var KEY_RAIL = 'hd-rail';
  function store(op, k, v) {
    try { return op === 'get' ? localStorage.getItem(k) : localStorage.setItem(k, v); } catch (e) { return null; }
  }

  /* ---- theme: ?theme= wins, then saved choice, then OS preference. Runs in <head> so there is no flash. ---- */
  var q = new URLSearchParams(location.search).get('theme');
  var theme = q || store('get', KEY_THEME) || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  document.documentElement.setAttribute('data-theme', theme);

  var P = {
    inbox: '<polyline points="22 12 16 12 14 15 10 15 8 12 2 12"/><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z"/>',
    users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
    user: '<path d="M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/>',
    usercheck: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><polyline points="16 11 18 13 22 9"/>',
    list: '<line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/>',
    star: '<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>',
    alert: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/>',
    book: '<path d="M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H20v20H6.5a2.5 2.5 0 0 1 0-5H20"/>',
    search: '<circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>',
    grid: '<rect x="3" y="3" width="7" height="9"/><rect x="14" y="3" width="7" height="5"/><rect x="14" y="12" width="7" height="9"/><rect x="3" y="16" width="7" height="5"/>',
    shield: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>',
    building: '<rect x="4" y="2" width="16" height="20" rx="2"/><path d="M9 22v-4h6v4"/><path d="M8 6h.01M16 6h.01M12 6h.01M12 10h.01M12 14h.01M16 10h.01M16 14h.01M8 10h.01M8 14h.01"/>',
    mail: '<rect x="2" y="4" width="20" height="16" rx="2"/><path d="m22 7-10 6L2 7"/>',
    clock: '<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>',
    zap: '<polygon points="13 2 3 14 12 14 11 22 21 10 12 10 13 2"/>',
    file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/>',
    plus: '<line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>',
    bell: '<path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41"/>',
    moon: '<path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z"/>',
    lock: '<rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/>',
    right: '<polyline points="9 18 15 12 9 6"/>',
    down: '<polyline points="6 9 12 15 18 9"/>',
    x: '<line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>',
    check: '<polyline points="20 6 9 17 4 12"/>',
    more: '<circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/><circle cx="5" cy="12" r="1"/>',
    merge: '<circle cx="18" cy="18" r="3"/><circle cx="6" cy="6" r="3"/><path d="M6 21V9a9 9 0 0 0 9 9"/>',
    clip: '<path d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66l-9.2 9.19a2 2 0 0 1-2.83-2.83l8.49-8.48"/>',
    reply: '<polyline points="9 17 4 12 9 7"/><path d="M20 18v-2a4 4 0 0 0-4-4H4"/>',
    panel: '<rect x="3" y="3" width="18" height="18" rx="2"/><line x1="9" y1="3" x2="9" y2="21"/>',
    link: '<path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/><path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>',
    edit: '<path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z"/>',
    info: '<circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/>',
    up: '<line x1="12" y1="19" x2="12" y2="5"/><polyline points="5 12 12 5 19 12"/>',
    activity: '<polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/>',
    layers: '<polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/>',
    cal: '<rect x="3" y="4" width="18" height="18" rx="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/>',
    msg: '<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>',
    send: '<line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/>',
    refresh: '<polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"/>',
    eye: '<path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/>',
    trash: '<polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/>'
  };
  function svg(name, cls) {
    return '<svg class="i ' + (cls || '') + '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + (P[name] || '') + '</svg>';
  }
  window.hdIcon = svg;

  var MARK = '<svg class="brand-mark" viewBox="0 0 28 28" aria-hidden="true"><path fill="currentColor" d="M4 8a3 3 0 0 1 3-3h14a3 3 0 0 1 3 3v3.1a3 3 0 0 0 0 5.8V20a3 3 0 0 1-3 3H7a3 3 0 0 1-3-3v-3.1a3 3 0 0 0 0-5.8z"/><path d="m10 14 3 3 5.5-6" fill="none" stroke="#0f1b28" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/></svg>';

  function link(o) {
    var cur = o.id && o.id === document.body.dataset.nav;
    var href = o.href || '#';
    var stub = !o.href ? ' data-stub="1"' : '';
    return '<a class="rail-link" href="' + href + '"' + stub + (cur ? ' aria-current="page"' : '') + ' title="' + o.label + '">' + svg(o.icon) + '<span class="lbl">' + o.label + '</span>' + (o.count != null ? '<span class="rail-count' + (o.hot ? ' hot' : '') + '">' + o.count + '</span>' : '') + '</a>';
  }

  function rail(compact) {
    return '<aside class="rail" aria-label="Primary">' +
      '<a class="rail-brand" href="index.html" title="Prototype index">' + MARK + '<span class="wordmark">Helpdesk</span></a>' +
      '<nav class="rail-scroll" aria-label="Main">' +
      '<div class="rail-group"><div class="rail-head">Work</div>' +
      link({ icon: 'grid', label: 'Dashboard' }) +
      link({ id: 'my', icon: 'inbox', label: 'My Queue', count: 4, href: 'workspace.html' }) +
      link({ icon: 'users', label: 'Team Queue', count: 5, href: 'workspace.html?queue=team' }) +
      link({ icon: 'user', label: 'Unassigned', count: 1, href: 'workspace.html?queue=unassigned' }) +
      link({ icon: 'list', label: 'All Tickets', count: 14, href: 'workspace.html?queue=all' }) +
      link({ icon: 'star', label: 'Following' }) +
      link({ icon: 'alert', label: 'SLA breached', count: 1, hot: true }) +
      '</div>' +
      '<div class="rail-group"><div class="rail-head">Pinned views</div>' +
      link({ icon: 'layers', label: 'Waiting on vendor' }) +
      link({ icon: 'layers', label: 'High priority, open' }) +
      '</div>' +
      '<div class="rail-group"><div class="rail-head">Tools</div>' +
      link({ icon: 'book', label: 'Knowledge' }) +
      link({ icon: 'search', label: 'Search' }) +
      link({ icon: 'msg', label: 'Responses' }) +
      '</div></nav>' +
      '<div class="rail-foot">' +
      link({ id: 'admin', icon: 'shield', label: 'Administration', href: 'admin.html' }) +
      '<div class="rail-live"><span class="live-dot"></span><span>Live updates on</span></div>' +
      '<div class="rail-user"><span class="avatar">CR</span><span class="who"><b>Carol Reyes</b><span>IT Support · TeamLead</span></span>' +
      '<button class="collapse-btn" type="button" id="railToggle" aria-label="' + (compact ? 'Expand navigation' : 'Collapse navigation') + '" title="Collapse or expand navigation">' + svg('panel') + '</button></div>' +
      '</div></aside>';
  }

  function topbar() {
    var crumbs = [];
    try { crumbs = JSON.parse(document.body.dataset.crumbs || '[]'); } catch (e) {}
    var c = crumbs.map(function (x, i) {
      var last = i === crumbs.length - 1;
      return (i ? '<span class="sep">/</span>' : '') + (last ? '<span aria-current="page">' + x[0] + '</span>' : '<a href="' + x[1] + '">' + x[0] + '</a>');
    }).join('');
    return '<header class="topbar"><nav class="crumbs" aria-label="Breadcrumb">' + c + '</nav>' +
      '<button class="cmd" type="button" id="cmdOpen">' + svg('search', 'sm') + '<span class="grow">Search tickets, people, articles…</span><kbd>Ctrl K</kbd></button>' +
      '<div class="top-actions">' +
      '<a class="btn primary" href="#" data-stub="1">' + svg('plus', 'sm') + 'New ticket</a>' +
      '<button class="icon-btn" type="button" aria-label="Notifications" data-stub="1">' + svg('bell') + '<span class="badge-dot"></span></button>' +
      '<button class="icon-btn" type="button" id="themeToggle" aria-label="Toggle light / dark theme"></button>' +
      '</div></header>';
  }

  var PALETTE = '<div class="palette-wrap" id="palette" role="dialog" aria-modal="true" aria-label="Command palette (concept)"><div class="palette">' +
    '<div class="in">' + svg('search') + '<input id="palIn" placeholder="Type a command or search…" aria-label="Command palette"><span class="concept">Concept</span></div>' +
    '<div class="grp">Go to</div>' +
    '<div class="it">' + svg('inbox', 'sm') + 'My Queue<span class="r">G then M</span></div>' +
    '<div class="it">' + svg('shield', 'sm') + 'Administration<span class="r">G then A</span></div>' +
    '<div class="grp">Recent tickets</div>' +
    '<div class="it">' + svg('file', 'sm') + 'IT-14 · VPN drops every 30 minutes on floor 3</div>' +
    '<div class="it">' + svg('file', 'sm') + 'IT-12 · Printer jam on floor 3</div>' +
    '<div class="foot"><span class="concept">Concept</span> Visual preview only. A command palette is not part of the current product and is not committed to.</div>' +
    '</div></div>';

  /* Department section navigation: groups and section names mirror the current Department page; dots mirror the Overview states. */
  var SECTIONS = [
    ['Department', [['overview', 'Overview', 'building', null, 'department-overview.html']]],
    ['People', [['teams', 'Teams', 'users', 'ok'], ['members', 'Members', 'user', 'ok'], ['requesters', 'Requester identities', 'usercheck', null]]],
    ['Communication', [['mailboxes', 'Mailboxes', 'mail', 'ok', 'department-mailboxes.html'], ['responses', 'Responses', 'msg', null]]],
    ['Ticket setup', [['ticketing', 'Ticketing', 'layers', 'warn']]],
    ['Service management', [['hours', 'Business hours', 'cal', 'ok'], ['sla', 'SLA policies', 'clock', 'ok'], ['automations', 'Automations', 'zap', 'off']]],
    ['Self service', [['knowledge', 'Knowledge', 'book', null]]],
    ['Productivity', [['macros', 'Macros', 'zap', null]]]
  ];
  function sectionNav(el) {
    var cur = el.dataset.current, h = '';
    SECTIONS.forEach(function (g) {
      h += '<div class="grp">' + g[0] + '</div>';
      g[1].forEach(function (s) {
        h += '<a href="' + (s[4] || '#') + '"' + (s[4] ? '' : ' data-stub="1"') + (s[0] === cur ? ' aria-current="page"' : '') + '>' + svg(s[2]) + s[1] + (s[3] ? '<span class="st ' + s[3] + '" title="' + ({ ok: 'Configured', warn: 'Needs attention', off: 'Not configured' })[s[3]] + '"></span>' : '') + '</a>';
      });
    });
    h += '<div class="legend"><span><i class="st ok"></i>Configured</span><span><i class="st warn"></i>Attention</span><span><i class="st off"></i>Off</span></div>';
    el.innerHTML = h;
  }

  function setThemeIcon() {
    var b = document.getElementById('themeToggle');
    if (b) b.innerHTML = svg(document.documentElement.getAttribute('data-theme') === 'dark' ? 'sun' : 'moon');
  }

  function toast(msg) {
    var t = document.getElementById('toast');
    if (!t) { t = document.createElement('div'); t.id = 'toast'; t.className = 'toast'; t.setAttribute('role', 'status'); document.body.appendChild(t); }
    t.textContent = msg; t.classList.add('on');
    clearTimeout(toast.h); toast.h = setTimeout(function () { t.classList.remove('on'); }, 2200);
  }
  window.hdToast = toast;

  function boot() {
    var page = document.getElementById('page');
    if (page && document.body.dataset.shell !== 'none') {
      var saved = store('get', KEY_RAIL);
      var compact = saved ? saved === 'compact' : document.body.dataset.rail === 'compact';
      var app = document.createElement('div');
      app.className = 'app' + (compact ? ' compact' : '');
      app.innerHTML = rail(compact) + '<div class="main-col">' + topbar() + '<div class="content" id="content"></div></div>';
      document.body.insertBefore(app, page);
      var content = app.querySelector('#content');
      content.appendChild(page);
      if (document.body.dataset.flush) { content.style.overflow = 'hidden'; }
      document.body.insertAdjacentHTML('beforeend', PALETTE);
      app.querySelector('#railToggle').addEventListener('click', function () {
        var c = app.classList.toggle('compact');
        store('set', KEY_RAIL, c ? 'compact' : 'full');
        this.setAttribute('aria-label', c ? 'Expand navigation' : 'Collapse navigation');
      });
      var pal = document.getElementById('palette');
      var openPal = function () { pal.classList.add('on'); document.getElementById('palIn').focus(); };
      var closePal = function () { pal.classList.remove('on'); };
      document.getElementById('cmdOpen').addEventListener('click', openPal);
      pal.addEventListener('click', function (e) { if (e.target === pal) closePal(); });
      document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); openPal(); }
        if (e.key === 'Escape') closePal();
      });
    }
    var sn = document.getElementById('sectionNav');
    if (sn) sectionNav(sn);
    document.querySelectorAll('[data-i]').forEach(function (el) { el.outerHTML = svg(el.dataset.i, el.dataset.cls || ''); });
    var tt = document.getElementById('themeToggle');
    setThemeIcon();
    if (tt) tt.addEventListener('click', function () {
      var n = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      document.documentElement.setAttribute('data-theme', n);
      store('set', KEY_THEME, n); setThemeIcon();
    });
    document.addEventListener('click', function (e) {
      var s = e.target.closest('[data-stub]');
      if (s) { e.preventDefault(); toast('Not part of this prototype'); }
    });
    // Carry an explicit ?theme= through prototype links so file:// storage quirks never lose the choice.
    if (q) document.querySelectorAll('a[href]').forEach(function (a) {
      var h = a.getAttribute('href');
      if (h && !h.startsWith('#') && !h.startsWith('http') && h.indexOf('theme=') < 0) a.setAttribute('href', h + (h.indexOf('?') < 0 ? '?' : '&') + 'theme=' + q);
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot); else boot();
})();
