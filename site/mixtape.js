// The app hides a Konami code; so does its page. Up Up Down Down Left Right Left Right B A turns the tape over.
(function () {
  var seq = ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a'];
  var pos = 0, tape = document.querySelector('.cassette'), label = document.getElementById('side');
  if (!tape || !label) return;
  var still = window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches;
  function flip() {
    var b = /Side A/.test(label.textContent);
    var swap = function () { label.textContent = 'Mixtape · Side ' + (b ? 'B' : 'A'); tape.classList.remove('flipping'); };
    if (still) { swap(); return; }
    tape.classList.add('flipping');
    setTimeout(swap, 230);
  }
  document.addEventListener('keydown', function (e) {
    var k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    pos = k === seq[pos] ? pos + 1 : (k === seq[0] ? 1 : 0);
    if (pos === seq.length) { pos = 0; flip(); }
  });

  // the reels follow the scroll, like winding the tape with a pencil
  if (!still) {
    var hubs = Array.prototype.map.call(tape.querySelectorAll('.reel'), function (r) { return r.parentNode; });
    var base = hubs.map(function (g) { return g.getAttribute('transform'); });
    var angle = 0, last = window.scrollY, queued = false;
    addEventListener('scroll', function () {
      if (queued) return;
      queued = true;
      requestAnimationFrame(function () {
        queued = false;
        var y = window.scrollY;
        angle = (angle + (y - last) * 0.8) % 360;
        last = y;
        hubs.forEach(function (g, i) { g.setAttribute('transform', base[i] + ' rotate(' + angle.toFixed(1) + ')'); });
      });
    }, { passive: true });
  }
})();
