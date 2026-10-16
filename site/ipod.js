// A click-wheel player to try on the page. A made-up demo library (the screenshots' own, the rest invented alike),
// no sound. Run a finger or the mouse around the wheel to scroll, the middle chooses, MENU goes back, the bottom plays or
// pauses, left and right skip. Once it has focus: arrow keys, Enter, Escape, and the mouse wheel. Extras hides a game.
(function () {
  var root = document.getElementById('ipod');
  if (!root) return;
  var scr = root.querySelector('.scr'), wheel = root.querySelector('.wheel'), center = root.querySelector('.center');
  var live = document.getElementById('ipod-live');
  var still = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  var ROWS = 7, STEP = 15;       // rows on the screen; degrees of wheel per step (24 a turn)

  // ---------- the library ----------
  var ALBUMS = [
    { a: 'Marlow Vance', t: 'Coastline FM', s: [['Radio Static Heart', 266], ['Coastline FM', 275], ['Rearview Neon', 178], ['Glass Pier', 234], ['Overnight Express', 298], ['Low Sun', 292]] },
    { a: 'The Paper Lanterns', t: 'Night Ferry', s: [['Harbour Lights', 202], ['Night Ferry', 184], ['Small Hours', 233], ['Gulls', 198], ['Lamplight', 221]] },
    { a: 'Citrus Club', t: 'Orange Season', s: [['Marmalade', 192], ['Sun Room', 167], ['Half Past June', 213], ['Peel', 178], ['Golden Hour Bus', 245]] },
    { a: 'Atlas & Ivy', t: 'Slow Mountains', s: [['Switchback', 252], ['Thin Air', 229], ['Valley Floor', 302], ['Snowline', 267], ['Summit Radio', 235]] },
    { a: 'Halftone Kids', t: 'Dot Matrix', s: [['Print Head', 161], ['Paper Jam', 185], ['Cyan Magenta', 208], ['Toner', 172], ['Line Feed', 197]] },
    { a: 'Pixel Choir', t: 'Arcade Lights', s: [['Insert Coin', 153], ['High Score', 181], ['Continue?', 168], ['Extra Life', 202], ['Game Over Waltz', 241]] },
    { a: 'Blue Hour Radio', t: 'Late Transmission', s: [['Static Prayer', 258], ['Callsign', 216], ['Night Shift', 284], ['Dead Air', 189]] },
    { a: 'Velvet Static', t: 'Road Movie', s: [['Mile Marker', 227], ['Motel Pool', 238], ['Interstate Hum', 260], ['Rest Stop', 175]] }
  ];
  var SONGS = [];
  ALBUMS.forEach(function (al, ai) {
    al.ids = al.s.map(function (s) { SONGS.push({ t: s[0], len: s[1], al: ai }); return SONGS.length - 1; });
  });
  function find(t) { for (var i = 0; i < SONGS.length; i++) if (SONGS[i].t === t) return i; return 0; }
  var PLAYLISTS = [
    ['Road Trip', ['Overnight Express', 'Rearview Neon', 'Mile Marker', 'Interstate Hum', 'Golden Hour Bus', 'Switchback', 'Motel Pool', 'Coastline FM', 'Rest Stop']],
    ['Sunday Morning', ['Marmalade', 'Sun Room', 'Half Past June', 'Peel', 'Thin Air', 'Valley Floor', 'Harbour Lights', 'Gulls']],
    ['Late Night', ['Small Hours', 'Night Ferry', 'Lamplight', 'Static Prayer', 'Night Shift', 'Dead Air', 'Low Sun', 'Callsign', 'Radio Static Heart']],
    ['Summer Mixtape', ['Radio Static Heart', 'Glass Pier', 'Marmalade', 'Insert Coin', 'High Score', 'Cyan Magenta', 'Motel Pool', 'Golden Hour Bus', 'Extra Life']]
  ].map(function (p) { return { t: p[0], ids: p[1].map(find) }; });
  function cmp(a, b) { return a.replace(/^the /i, '').localeCompare(b.replace(/^the /i, '')); }

  // ---------- covers: little SVG pictures in the spirit of the demo iPod's generated ones ----------
  function svg(body) {
    return 'url("data:image/svg+xml,' + encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">' + body + '</svg>') + '")';
  }
  var ART = (function () {
    var out = [], g, i, j;
    g = '<defs><linearGradient id="a" x2="0" y2="1"><stop offset="0" stop-color="#2b1055"/><stop offset=".6" stop-color="#7b2f7f"/><stop offset="1" stop-color="#d9467a"/></linearGradient></defs>' +
      '<rect width="100" height="100" fill="url(#a)"/><circle cx="50" cy="47" r="24" fill="#ffcf5a"/>' +
      '<g fill="#8a3380"><rect y="50" width="100" height="1.6"/><rect y="55" width="100" height="2.2"/><rect y="60.5" width="100" height="2.8"/><rect y="66" width="100" height="3.4"/></g>' +
      '<rect y="70" width="100" height="30" fill="#1d0b35"/><g stroke="#ff5fd2" stroke-width=".7">';
    for (i = -70; i <= 170; i += 20) g += '<line x1="50" y1="70" x2="' + i + '" y2="100"/>';
    [73, 77, 83, 91].forEach(function (y) { g += '<line x1="0" x2="100" y1="' + y + '" y2="' + y + '"/>'; });
    out.push(svg(g + '</g>'));
    out.push(svg('<rect width="100" height="100" fill="#111d38"/><rect y="62" width="100" height="38" fill="#0a1328"/><circle cx="64" cy="31" r="11" fill="#f4f0e2"/>' +
      '<g fill="#f4f0e2" opacity=".55"><rect x="58" y="67" width="12" height="1.5"/><rect x="60" y="72" width="8" height="1.5"/><rect x="62" y="77" width="4" height="1.5"/></g>' +
      '<path d="M26 62h14l-3 4H29z" fill="#f4f0e2"/><path d="M33 60V46l7 14z" fill="#f4f0e2"/>'));
    out.push(svg('<rect width="100" height="100" fill="#f1e9d6"/><circle cx="46" cy="44" r="26" fill="#e65f3a"/><rect y="72" width="100" height="28" fill="#f2c13b"/><path d="M8 90 92 68" stroke="#1d1d1d" stroke-width="2.5"/>'));
    out.push(svg('<defs><linearGradient id="a" x2="0" y2="1"><stop offset="0" stop-color="#f8d6b6"/><stop offset="1" stop-color="#e99c80"/></linearGradient></defs>' +
      '<rect width="100" height="100" fill="url(#a)"/><circle cx="70" cy="32" r="10" fill="#fff4e6"/><path d="M0 70 22 47 40 63 60 38 82 61 100 49V100H0z" fill="#b97a8c"/><path d="M0 85 18 70 38 82 58 65 78 80 100 71V100H0z" fill="#7a4b6c"/>'));
    g = '<rect width="100" height="100" fill="#b5502c"/><g fill="#f3e2c7">';
    for (i = 0; i < 8; i++) for (j = 0; j < 8; j++) g += '<circle cx="' + (8 + i * 12) + '" cy="' + (8 + j * 12) + '" r="' + (1.2 + (i + j) * 0.22).toFixed(2) + '"/>';
    out.push(svg(g + '</g>'));
    var cols = ['#ff5fa2', '#ffd23f', '#4a7bd9', '#46c27a'];
    g = '<rect width="100" height="100" fill="#1b1d29"/>';
    for (i = 0; i < 4; i++) for (j = 0; j < 5; j++) g += '<rect x="' + (12 + i * 20) + '" y="' + (6 + j * 18.5) + '" width="15" height="14" rx="5" fill="' + cols[(i + j * 3) % 4] + '"/>';
    out.push(svg(g));
    out.push(svg('<rect width="100" height="100" fill="#17223b"/><g fill="none" stroke="#f3e9d2" stroke-width="1.6"><circle cx="50" cy="50" r="17" opacity=".55"/><circle cx="50" cy="50" r="28" opacity=".35"/><circle cx="50" cy="50" r="39" opacity=".18"/></g><circle cx="50" cy="50" r="8" fill="#f3e9d2"/>'));
    out.push(svg('<rect width="100" height="100" fill="#f0e6d0"/><path d="M-10 70 70-10h14L4 70z" fill="#e2552d"/><path d="M4 84 84 4h14L18 84z" fill="#f2b134"/><path d="M18 98 98 18h14L32 98z" fill="#2a9d8f"/><path d="M32 112 112 32h14L46 112z" fill="#264653"/>'));
    return out;
  })();

  // ---------- the clicker (a few milliseconds of noise) and, on phones, a tiny buzz ----------
  var ac = null, clickBuf = null, clicker = true;
  function audio() {
    if (ac || !(window.AudioContext || window.webkitAudioContext)) return;
    try {
      ac = new (window.AudioContext || window.webkitAudioContext)();
      var n = Math.floor(ac.sampleRate * 0.006);
      clickBuf = ac.createBuffer(1, n, ac.sampleRate);
      var d = clickBuf.getChannelData(0);
      for (var i = 0; i < n; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / n, 6);
    } catch (e) { ac = null; }
  }
  function tick(touch) {
    if (touch && navigator.vibrate) try { navigator.vibrate(4); } catch (e) {}
    if (!clicker || !ac) return;
    try {
      if (ac.state === 'suspended') ac.resume();
      var s = ac.createBufferSource(), g = ac.createGain();
      g.gain.value = 0.2;
      s.buffer = clickBuf; s.connect(g); g.connect(ac.destination); s.start();
    } catch (e) {}
  }

  // ---------- what is playing (Radio Static Heart at 1:23, like the screenshot) ----------
  var np = { q: ALBUMS[0].ids.slice(), i: 0, pos: 83, on: true, vol: 0.6, volUntil: 0 };
  function song() { return SONGS[np.q[np.i]]; }

  // ---------- screens ----------
  var stack = [], shown = null;
  function cur() { return stack[stack.length - 1]; }
  function menu(title, items, split) { return { k: 'menu', title: title, items: items, sel: 0, top: 0, split: !!split }; }
  function songs(title, ids) {
    return menu(title, ids.map(function (id, n) { return { label: SONGS[id].t, act: function () { play(ids, n); } }; }));
  }
  function mainMenu() {
    return menu('iPod', [
      { label: 'Music', sub: musicMenu },
      { label: 'Extras', sub: extrasMenu },
      { label: 'Settings', sub: settingsMenu },
      { label: 'Shuffle Songs', act: shuffle },
      { label: 'Now Playing', sub: function () { return { k: 'np' }; } }
    ], true);
  }
  function musicMenu() {
    return menu('Music', [
      { label: 'Cover Flow', sub: function () { return { k: 'cf', sel: song().al }; } },
      { label: 'Playlists', sub: function () {
        return menu('Playlists', PLAYLISTS.map(function (p) { return { label: p.t, sub: function () { return songs(p.t, p.ids); } }; }));
      } },
      { label: 'Artists', sub: function () {
        return menu('Artists', ALBUMS.map(function (a) { return { label: a.a, sub: function () { return songs(a.a, a.ids); } }; })
          .sort(function (x, y) { return cmp(x.label, y.label); }));
      } },
      { label: 'Albums', sub: function () {
        return menu('Albums', ALBUMS.map(function (a) { return { label: a.t, sub: function () { return songs(a.t, a.ids); } }; })
          .sort(function (x, y) { return cmp(x.label, y.label); }));
      } },
      { label: 'Songs', sub: function () {
        return songs('Songs', SONGS.map(function (s, i) { return i; }).sort(function (x, y) { return cmp(SONGS[x].t, SONGS[y].t); }));
      } }
    ]);
  }
  function extrasMenu() {
    return menu('Extras', [
      { label: 'Clock', sub: function () { return { k: 'clock' }; } },
      { label: 'Games', sub: function () { return menu('Games', [{ label: 'Bricks', sub: function () { return { k: 'bricks' }; } }]); } }
    ]);
  }
  function settingsMenu() {
    return menu('Settings', [
      { label: 'About', sub: function () {
        return menu('About', [{ label: 'Songs', val: '39' }, { label: 'Albums', val: '8' }, { label: 'Playlists', val: '4' },
          { label: 'Available', val: '29.8 GB' }, { label: 'Synced with', val: 'Mixtape' }]);
      } },
      { label: 'Clicker', val: function () { return clicker ? 'On' : 'Off'; }, act: function () { clicker = !clicker; drawList(cur()); } }
    ]);
  }
  function shuffle() {
    var ids = SONGS.map(function (s, i) { return i; });
    for (var i = ids.length - 1; i > 0; i--) { var j = Math.floor(Math.random() * (i + 1)), t = ids[i]; ids[i] = ids[j]; ids[j] = t; }
    play(ids, 0);
  }
  function play(ids, n) { np.q = ids.slice(); np.i = n; np.pos = 0; np.on = true; push({ k: 'np' }); }

  function push(s) { stack.push(s); render(1); say(s.title || { np: 'Now Playing', cf: 'Cover Flow', clock: 'Clock', bricks: 'Bricks' }[s.k]); }
  function back() {
    if (stack.length < 2) return;
    stack.pop();
    render(-1);
    var s = cur();
    say(s.k === 'menu' ? s.title + ', ' + s.items[s.sel].label : s.k);
  }
  function select() {
    var s = cur();
    if (s.k === 'menu') {
      var it = s.items[s.sel];
      if (it && it.sub) push(it.sub()); else if (it && it.act) it.act();
    } else if (s.k === 'cf') push(songs(ALBUMS[s.sel].t, ALBUMS[s.sel].ids));
    else if (s.k === 'bricks') launch();
    else if (s.k === 'np') playPause();
  }
  function playPause() { np.on = !np.on; refreshBar(); }
  function skip(n) {
    if (n < 0 && np.pos > 3) np.pos = 0;
    else { np.i = (np.i + n + np.q.length) % np.q.length; np.pos = 0; }
    np.on = true;
    if (cur().k === 'np') render(); else { refreshBar(); if (cur().split) drawArt(); }
  }
  function scroll(n, touch) {
    var s = cur(), moved = false;
    if (s.k === 'menu') {
      var to = Math.max(0, Math.min(s.items.length - 1, s.sel + n));
      if (to !== s.sel) {
        s.sel = to; moved = true;
        if (s.sel < s.top) s.top = s.sel;
        if (s.sel > s.top + ROWS - 1) s.top = s.sel - ROWS + 1;
        drawList(s);
        var v = val(s.items[s.sel]);
        say(s.items[s.sel].label + (v ? ', ' + v : ''));
      }
    } else if (s.k === 'cf') {
      var t = Math.max(0, Math.min(ALBUMS.length - 1, s.sel + n));
      if (t !== s.sel) { s.sel = t; moved = true; layoutFlow(s); say(ALBUMS[t].t + ', ' + ALBUMS[t].a); }
    } else if (s.k === 'np') {
      var v2 = Math.max(0, Math.min(1, Math.round((np.vol + n * 0.05) * 20) / 20));
      moved = v2 !== np.vol; np.vol = v2; np.volUntil = Date.now() + 1500; drawBottom();
    } else if (s.k === 'bricks') { moved = paddle(n); }
    if (moved) tick(touch);
  }
  function say(t) { if (live && t) live.textContent = t; }

  // ---------- drawing ----------
  function esc(t) { return String(t).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function val(it) { return typeof it.val === 'function' ? it.val() : it.val; }
  function mmss(s) { s = Math.max(0, Math.round(s)); return Math.floor(s / 60) + ':' + ('0' + (s % 60)).slice(-2); }
  var PLAYING = '<path d="M2 1v8l7-4z"/>', PAUSED = '<path d="M2 1h2.4v8H2zM5.6 1H8v8H5.6z"/>';
  function bar(title) {
    return '<div class="bar"><svg class="st" viewBox="0 0 10 10" aria-hidden="true">' + (np.on ? PLAYING : PAUSED) + '</svg><span>' + esc(title) +
      '</span><span class="bat"><b></b></span></div>';
  }
  function refreshBar() {
    var st = scr.querySelector('.bar .st');
    if (st) st.innerHTML = np.on ? PLAYING : PAUSED;
  }
  function paint() {
    Array.prototype.forEach.call(scr.querySelectorAll('[data-art]'), function (el) { el.style.backgroundImage = ART[+el.getAttribute('data-art')]; });
  }
  function rowsHTML(s) {
    var h = '';
    for (var i = s.top; i < Math.min(s.items.length, s.top + ROWS); i++) {
      var it = s.items[i], v = val(it);
      h += '<div class="row' + (i === s.sel ? ' on' : '') + '" data-i="' + i + '"><span>' + esc(it.label) + '</span>' +
        (v != null ? '<em>' + esc(v) + '</em>' : it.sub ? '<i>›</i>' : '') + '</div>';
    }
    return h;
  }
  function thumb(s) {
    var th = Math.max(14, ROWS / s.items.length * 147), tp = s.top / (s.items.length - ROWS) * (147 - th);
    return 'height:' + th.toFixed(1) + 'px;top:' + tp.toFixed(1) + 'px';
  }
  function drawList(s) {
    var list = scr.querySelector('.list'), b = scr.querySelector('.sb b');
    if (!list) return;
    list.innerHTML = rowsHTML(s);
    if (b) b.setAttribute('style', thumb(s));
  }
  function drawArt() {
    var art = scr.querySelector('.art');
    if (art) { art.setAttribute('data-art', song().al); paint(); }
  }
  function bottomHTML() {
    if (Date.now() < np.volUntil) {
      return '<div class="vol"><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M1 4h2.5L6.5 1v10L3.5 8H1z"/></svg><div class="prog"><b style="width:' + Math.round(np.vol * 100) +
        '%"></b></div><svg viewBox="0 0 12 12" aria-hidden="true"><path d="M1 4h2.5L6.5 1v10L3.5 8H1z"/><path d="M8.3 3.5a3.5 3.5 0 0 1 0 5M9.8 2a5.5 5.5 0 0 1 0 8" fill="none" stroke="currentColor" stroke-width="1.2"/></svg></div>';
    }
    var s = song();
    return '<div class="prog"><b style="width:' + (np.pos / s.len * 100).toFixed(1) + '%"></b></div><div class="times"><span>' + mmss(np.pos) +
      '</span><span>-' + mmss(s.len - np.pos) + '</span></div>';
  }
  function drawBottom() { var b = scr.querySelector('.np .bot'); if (b) b.innerHTML = bottomHTML(); }
  function layoutFlow(s) {
    Array.prototype.forEach.call(scr.querySelectorAll('.cf .c'), function (c, j) {
      var d = j - s.sel, a = Math.abs(d), sg = d < 0 ? -1 : 1;
      c.style.transform = d === 0 ? 'translateZ(18px)' : 'translateX(' + sg * (52 + (a - 1) * 19) + 'px) rotateY(' + (-sg * 68) + 'deg)';
      c.style.zIndex = 20 - a;
      c.style.opacity = a > 4 ? 0 : 1;
    });
    var cap = scr.querySelector('.cf .cap');
    if (cap) cap.innerHTML = '<b>' + esc(ALBUMS[s.sel].t) + '</b><span>' + esc(ALBUMS[s.sel].a) + '</span>';
  }
  function clockTick() {
    var b = scr.querySelector('.clk b');
    if (!b) return;
    var d = new Date();
    b.textContent = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    scr.querySelector('.clk span').textContent = d.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long' });
  }

  function render(dir) {
    var s = cur(), anim = !still && dir ? (dir > 0 ? ' in-f' : ' in-b') : '', h;
    if (s.k !== 'bricks') stopGame();
    if (s.k === 'menu') {
      var long = s.items.length > ROWS;
      h = bar(s.title) + '<div class="pane' + (s.split ? ' split' : '') + (long ? ' scroll' : '') + anim + '"><div class="list">' + rowsHTML(s) + '</div>' +
        (long ? '<div class="sb"><b style="' + thumb(s) + '"></b></div>' : '') +
        (s.split ? '<div class="art" data-art="' + song().al + '"></div>' : '') + '</div>';
    } else if (s.k === 'np') {
      var sg = song(), al = ALBUMS[sg.al];
      h = bar('Now Playing') + '<div class="pane np' + anim + '"><span class="cnt">' + (np.i + 1) + ' of ' + np.q.length + '</span>' +
        '<div class="cov" data-art="' + sg.al + '"></div><div class="ref" data-art="' + sg.al + '"></div>' +
        '<div class="meta"><b>' + esc(sg.t) + '</b><span>' + esc(al.a) + '</span><span>' + esc(al.t) + '</span></div>' +
        '<div class="bot">' + bottomHTML() + '</div></div>';
    } else if (s.k === 'cf') {
      h = '<div class="pane cf' + anim + '"><div class="stage">';
      for (var j = 0; j < ALBUMS.length; j++) h += '<div class="c" data-art="' + j + '" data-j="' + j + '"></div>';
      h += '</div><div class="cap"></div></div>';
    } else if (s.k === 'clock') {
      h = bar('Clock') + '<div class="pane clk' + anim + '"><b></b><span></span></div>';
    } else if (s.k === 'bricks') {
      if (!dir && game) return;      // a play/pause or skip while playing: the game keeps its canvas
      h = bar('Bricks') + '<div class="pane' + anim + '"><canvas width="224" height="147"></canvas></div>';
    }
    scr.innerHTML = h;
    shown = s;
    paint();
    if (s.k === 'cf') layoutFlow(s);
    if (s.k === 'clock') clockTick();
    if (s.k === 'bricks') startGame(scr.querySelector('canvas'));
  }

  // ---------- Bricks: the wheel moves the paddle, the middle serves ----------
  var game = null, raf = 0, lastT = 0;
  var GW = 224, GH = 147, PW = 34, BR = 2.6, SPEED = 120, BCOLS = 7, BROWS = 4;
  var BCOL = ['#5b3196', '#6a5cb8', '#5a90c9', '#4f9d44'];
  function startGame(cv) {
    var dpr = Math.min(2, window.devicePixelRatio || 1);
    cv.width = GW * dpr; cv.height = GH * dpr;
    var ctx = cv.getContext('2d');
    ctx.scale(dpr, dpr);
    game = { ctx: ctx, px: (GW - PW) / 2, lives: 3, score: 0, state: 'ready', x: 0, y: 0, vx: 0, vy: 0, bricks: [] };
    for (var r = 0; r < BROWS; r++) for (var c = 0; c < BCOLS; c++) game.bricks.push({ r: r, c: c, on: true });
    cancelAnimationFrame(raf);
    lastT = 0;
    raf = requestAnimationFrame(loop);
  }
  function stopGame() { if (game) { cancelAnimationFrame(raf); game = null; } }
  function paddle(n) {
    if (!game) return false;
    var p = Math.max(0, Math.min(GW - PW, game.px + n * 9));
    var moved = p !== game.px;
    game.px = p;
    return moved;
  }
  function launch() {
    if (!game) return;
    if (game.state === 'ready') {
      game.state = 'play';
      var a = (Math.random() * 50 - 25) * Math.PI / 180;
      game.vx = SPEED * Math.sin(a); game.vy = -SPEED * Math.cos(a);
    } else if (game.state === 'over' || game.state === 'won') startGame(scr.querySelector('canvas'));
  }
  function brickRect(b) {
    var w = (GW - 12 - (BCOLS - 1) * 3) / BCOLS;
    return { x: 6 + b.c * (w + 3), y: 20 + b.r * 10, w: w, h: 7 };
  }
  function loop(t) {
    var g = game;
    if (!g) return;
    var dt = lastT ? Math.min(0.033, (t - lastT) / 1000) : 0;
    lastT = t;
    var py = GH - 10;
    if (g.state === 'ready') { g.x = g.px + PW / 2; g.y = py - BR - 1; }
    else if (g.state === 'play') {
      g.x += g.vx * dt; g.y += g.vy * dt;
      if (g.x < BR) { g.x = BR; g.vx = Math.abs(g.vx); }
      if (g.x > GW - BR) { g.x = GW - BR; g.vx = -Math.abs(g.vx); }
      if (g.y < 14 + BR) { g.y = 14 + BR; g.vy = Math.abs(g.vy); }
      if (g.vy > 0 && g.y + BR >= py && g.y + BR <= py + 7 && g.x >= g.px - 3 && g.x <= g.px + PW + 3) {
        var a = Math.max(-1, Math.min(1, (g.x - (g.px + PW / 2)) / (PW / 2))) * 60 * Math.PI / 180;
        g.vx = SPEED * Math.sin(a); g.vy = -SPEED * Math.cos(a);
        tick(false);
      }
      for (var i = 0; i < g.bricks.length; i++) {
        var b = g.bricks[i];
        if (!b.on) continue;
        var r = brickRect(b);
        if (g.x + BR > r.x && g.x - BR < r.x + r.w && g.y + BR > r.y && g.y - BR < r.y + r.h) {
          b.on = false; g.score += 10 * (BROWS - b.r); g.vy = -g.vy; tick(false);
          if (!g.bricks.some(function (x) { return x.on; })) g.state = 'won';
          break;
        }
      }
      if (g.y > GH + 8) { g.lives--; g.state = g.lives > 0 ? 'ready' : 'over'; }
    }
    draw(g, py);
    raf = requestAnimationFrame(loop);
  }
  function draw(g, py) {
    var c = g.ctx;
    c.fillStyle = '#f3f5f7'; c.fillRect(0, 0, GW, GH);
    c.fillStyle = '#3d454e'; c.font = '700 10px "Helvetica Neue", Arial, sans-serif'; c.textBaseline = 'middle';
    c.textAlign = 'left'; c.fillText('Score ' + g.score, 6, 7);
    for (var l = 0; l < g.lives; l++) { c.beginPath(); c.arc(GW - 8 - l * 9, 7, 2.6, 0, 7); c.fill(); }
    c.fillStyle = '#b6bdc5'; c.fillRect(0, 13, GW, 1);
    g.bricks.forEach(function (b) {
      if (!b.on) return;
      var r = brickRect(b);
      c.fillStyle = BCOL[b.r]; c.fillRect(r.x, r.y, r.w, r.h);
      c.fillStyle = 'rgba(255,255,255,.35)'; c.fillRect(r.x, r.y, r.w, 2);
    });
    c.fillStyle = '#2b3138'; c.fillRect(g.px, py, PW, 4);
    c.fillStyle = '#4f9d44'; c.beginPath(); c.arc(g.x, g.y, BR, 0, 7); c.fill();
    var msg = g.state === 'ready' ? 'Press the middle' : g.state === 'over' ? 'Game over · middle to retry' : g.state === 'won' ? 'Cleared! · middle to retry' : '';
    if (msg) { c.fillStyle = '#3d454e'; c.textAlign = 'center'; c.font = '700 11px "Helvetica Neue", Arial, sans-serif'; c.fillText(msg, GW / 2, 92); }
  }

  // ---------- the wheel ----------
  var drag = null;
  function angleAt(e) {
    var r = wheel.getBoundingClientRect();
    return Math.atan2(e.clientY - (r.top + r.height / 2), e.clientX - (r.left + r.width / 2)) * 180 / Math.PI;
  }
  function quadrant(a) { return a >= -135 && a < -45 ? 'top' : a >= -45 && a < 45 ? 'right' : a >= 45 && a < 135 ? 'bottom' : 'left'; }
  function press(q) {
    wheel.classList.add('p-' + q);
    setTimeout(function () { wheel.classList.remove('p-' + q); }, 150);
    tick(false);
    if (q === 'top') back(); else if (q === 'bottom') playPause(); else skip(q === 'right' ? 1 : -1);
  }
  function wake() { audio(); if (document.activeElement !== root) root.focus({ preventScroll: true }); }
  wheel.addEventListener('pointerdown', function (e) {
    if (e.button > 0 || e.target === center) return;
    wake();
    var a = angleAt(e);
    drag = { id: e.pointerId, last: a, start: a, acc: 0, moved: 0, t: Date.now(), touch: e.pointerType === 'touch' };
    try { wheel.setPointerCapture(e.pointerId); } catch (x) {}
    wheel.classList.add('dragging');
    e.preventDefault();
  });
  wheel.addEventListener('pointermove', function (e) {
    if (!drag || e.pointerId !== drag.id) return;
    var a = angleAt(e), d = a - drag.last;
    if (d > 180) d -= 360; else if (d < -180) d += 360;
    drag.last = a; drag.acc += d; drag.moved += Math.abs(d);
    while (drag.acc >= STEP) { drag.acc -= STEP; scroll(1, drag.touch); }
    while (drag.acc <= -STEP) { drag.acc += STEP; scroll(-1, drag.touch); }
  });
  function end(e) {
    if (!drag || e.pointerId !== drag.id) return;
    var d = drag;
    drag = null;
    wheel.classList.remove('dragging');
    if (e.type === 'pointerup' && d.moved < 10 && Date.now() - d.t < 700) press(quadrant(d.start));
  }
  wheel.addEventListener('pointerup', end);
  wheel.addEventListener('pointercancel', end);
  center.addEventListener('pointerdown', function (e) { if (e.button > 0) return; wake(); center.classList.add('down'); e.preventDefault(); });
  center.addEventListener('pointerup', function () { if (center.classList.contains('down')) { center.classList.remove('down'); tick(false); select(); } });
  center.addEventListener('pointerleave', function () { center.classList.remove('down'); });
  // the labels and the middle are buttons for screen readers; a pointer is handled above, so only a click without one counts here
  Array.prototype.forEach.call(wheel.querySelectorAll('button'), function (b) {
    b.addEventListener('click', function (e) {
      if (e.detail !== 0) return;
      if (b === center) select(); else press(b.getAttribute('data-k'));
    });
  });
  // the screen itself: a tap picks a row (again: opens it), or a cover
  scr.addEventListener('click', function (e) {
    wake();
    var s = cur(), row = e.target.closest('.row'), cov = e.target.closest('.c');
    if (row && s.k === 'menu') {
      var i = +row.getAttribute('data-i');
      if (i === s.sel) select(); else { s.sel = i; drawList(s); tick(false); }
    } else if (cov && s.k === 'cf') {
      var j = +cov.getAttribute('data-j');
      if (j === s.sel) select(); else { s.sel = j; layoutFlow(s); tick(false); }
    }
  });
  root.addEventListener('keydown', function (e) {
    if (e.altKey || e.ctrlKey || e.metaKey || e.target !== root) return;
    var k = e.key, side = cur().k === 'cf' || cur().k === 'bricks', done = true;
    audio();
    if (k === 'ArrowDown' || (side && k === 'ArrowRight')) scroll(1);
    else if (k === 'ArrowUp' || (side && k === 'ArrowLeft')) scroll(-1);
    else if (k === 'Enter' || k === ' ' || k === 'ArrowRight') { tick(false); select(); }
    else if (k === 'Escape' || k === 'Backspace' || k === 'ArrowLeft') { tick(false); back(); }
    else if (k === 'p' || k === 'P') playPause();
    else done = false;
    if (done) e.preventDefault();
  });
  // the mouse wheel turns it too, but only once the player has focus: scrolling past it scrolls the page
  var wacc = 0;
  root.addEventListener('wheel', function (e) {
    if (document.activeElement !== root) return;
    e.preventDefault();
    wacc += e.deltaMode === 1 ? e.deltaY * 30 : e.deltaY;
    while (wacc >= 40) { wacc -= 40; scroll(1); }
    while (wacc <= -40) { wacc += 40; scroll(-1); }
  }, { passive: false });

  // the song plays on (silently); the clock keeps time
  setInterval(function () {
    if (np.on) {
      np.pos++;
      if (np.pos >= song().len) {
        np.i = (np.i + 1) % np.q.length; np.pos = 0;
        if (cur().k === 'np') { render(); return; }
        if (cur().split) drawArt();
      }
    }
    var s = cur();
    if (s.k === 'np') drawBottom(); else if (s.k === 'clock') clockTick();
  }, 1000);

  stack.push(mainMenu());
  render();
})();
