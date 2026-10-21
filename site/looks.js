// Mixtape's looks on its own page - Modern, Windows 95 and Cyber, as in Settings › Appearance › Look. Remembered in
// this browser; a link ending in #look-95 or #look-cyber opens the page in that look (the head's inline script applies
// it before the first paint). The click-wheel player hears about the change ('mixtape-look') to repaint its game.
(function () {
  var root = document.documentElement, KEY = 'mixtape-look';
  var buttons = Array.prototype.slice.call(document.querySelectorAll('.looks button'));
  var tint = document.querySelector('meta[name="theme-color"]');
  var TINT = { modern: '#1d1e22', '95': '#008080', cyber: '#0c0a0b' };
  function set(look, save) {
    if (!/^(95|cyber)$/.test(look)) look = 'modern';
    if (look === 'modern') root.removeAttribute('data-look'); else root.setAttribute('data-look', look);
    buttons.forEach(function (b) { b.setAttribute('aria-pressed', String(b.getAttribute('data-look') === look)); });
    if (tint) tint.setAttribute('content', TINT[look]);
    if (save) {
      try { localStorage.setItem(KEY, look); } catch (e) {}
      if (/^#look-/.test(location.hash)) history.replaceState(null, '', location.pathname + location.search);
      document.dispatchEvent(new CustomEvent('mixtape-look'));
    }
  }
  buttons.forEach(function (b) { b.addEventListener('click', function () { set(b.getAttribute('data-look'), true); }); });
  set(root.getAttribute('data-look') || 'modern', false);
})();
