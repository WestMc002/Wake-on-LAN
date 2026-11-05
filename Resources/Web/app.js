// Wake on LAN – Handy-Steuerung (läuft im Browser, Daten kommen vom PC)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
'use strict';

(function () {
  var INTERVALL = 3000;
  var $ = function (id) { return document.getElementById(id); };
  var zugeklappt = {};
  try { zugeklappt = JSON.parse(localStorage.getItem('wol.zugeklappt') || '{}') || {}; } catch (e) { zugeklappt = {}; }
  var timer = null, laeuft = false, letzteDaten = null, meldungTimer = null, fehlerZaehler = 0;

  // ---------- Symbole ----------
  var SVG = 'http://www.w3.org/2000/svg';
  function symbol(pfad, klasse) {
    var s = document.createElementNS(SVG, 'svg');
    s.setAttribute('viewBox', '0 0 24 24');
    s.setAttribute('aria-hidden', 'true');
    if (klasse) s.setAttribute('class', klasse);
    pfad.forEach(function (d) {
      var p = document.createElementNS(SVG, 'path');
      p.setAttribute('d', d);
      s.appendChild(p);
    });
    return s;
  }
  var EIN = ['M12 3v9', 'M6.3 6.6a8 8 0 1 0 11.4 0'];
  var PFEIL = ['M6 9l6 6 6-6'];
  var MEHR = ['M5 12h.01', 'M12 12h.01', 'M19 12h.01'];

  // Status-Zeichen wie im Programm: grün mit Haken, rot mit Kreuz, sonst Punkt bzw. Ring
  function statusZeichen(art) {
    var box = document.createElement('span');
    box.className = 'zeichen';
    if (art === 'online' || art === 'offline' || art === 'off') {
      var s = document.createElementNS(SVG, 'svg');
      s.setAttribute('viewBox', '0 0 22 22');
      var k = document.createElementNS(SVG, 'circle');
      k.setAttribute('cx', '11'); k.setAttribute('cy', '11'); k.setAttribute('r', '10');
      k.setAttribute('fill', art === 'online' ? 'var(--gruen)' : 'var(--rot)');
      s.appendChild(k);
      var m = document.createElementNS(SVG, 'path');
      m.setAttribute('d', art === 'online' ? 'M6.5 11.2l3 3 6-6.4' : 'M7.5 7.5l7 7M14.5 7.5l-7 7');
      m.setAttribute('fill', 'none');
      m.setAttribute('stroke', 'var(--zeichen)');
      m.setAttribute('stroke-width', '2');
      m.setAttribute('stroke-linecap', 'round');
      m.setAttribute('stroke-linejoin', 'round');
      s.appendChild(m);
      box.appendChild(s);
    } else if (art === 'waking' || art === 'busy') {
      var p = document.createElement('span'); p.className = 'puls'; box.appendChild(p);
    } else if (art === 'error') {
      var e = document.createElement('span'); e.className = 'punkt-gelb'; box.appendChild(e);
    } else {
      var r = document.createElement('span'); r.className = 'ring'; box.appendChild(r);
    }
    return box;
  }

  // ---------- Verbindung zum PC ----------
  function anfrage(methode, pfad, daten) {
    var opt = { method: methode, credentials: 'same-origin', cache: 'no-store', headers: {} };
    if (methode === 'POST') {
      opt.headers['X-WOL'] = '1';
      opt.headers['Content-Type'] = 'application/x-www-form-urlencoded';
      opt.body = Object.keys(daten || {}).map(function (k) { return encodeURIComponent(k) + '=' + encodeURIComponent(daten[k]); }).join('&');
    }
    return fetch(pfad, opt).then(function (r) {
      return r.json().catch(function () { return {}; }).then(function (j) { j._status = r.status; return j; });
    });
  }

  function zeigeAnmeldung() {
    stop();
    $('liste').hidden = true;
    $('abmelden').hidden = true;
    $('anmeldung').hidden = false;
    setTimeout(function () { $('pin').focus(); }, 50);
  }

  function laden() {
    if (laeuft) return;
    laeuft = true;
    anfrage('GET', '/api/status').then(function (d) {
      laeuft = false;
      if (d._status === 401) { zeigeAnmeldung(); return; }
      if (d._status !== 200) throw new Error('Status ' + d._status);
      fehlerZaehler = 0;
      $('verbindung').hidden = true;
      $('anmeldung').hidden = true;
      $('liste').hidden = false;
      $('abmelden').hidden = false;
      zeichnen(d);
    }).catch(function () {
      laeuft = false;
      if (++fehlerZaehler >= 2) $('verbindung').hidden = false;
    });
  }

  function start() {
    stop();
    laden();
    timer = setInterval(function () { if (document.visibilityState === 'visible') laden(); }, INTERVALL);
  }

  function stop() {
    if (timer) clearInterval(timer);
    timer = null;
  }

  // ---------- Darstellung ----------
  function el(tag, klasse, text) {
    var e = document.createElement(tag);
    if (klasse) e.className = klasse;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }

  function zeichnen(d) {
    letzteDaten = d;
    var v = $('version');
    v.textContent = 'Version ' + d.version;
    v.hidden = false;
    $('info-titel').textContent = d.app;
    $('info-version').textContent = 'Version ' + d.version;
    $('info-pc').textContent = d.pc;
    $('copyright').textContent = '© ' + d.year + ' Günther Schoder';
    var alle = $('alle');
    alle.querySelector('span').textContent = d.all > 0 ? 'Alle wecken (' + d.all + ')' : 'Alle wecken';
    alle.disabled = d.all === 0;

    var gruppen = $('gruppen');
    var neu = document.createDocumentFragment();
    var gesamt = 0;
    d.groups.forEach(function (g) {
      gesamt += g.devices.length;
      var bereich = el('div', 'gruppe');
      if (g.title) {
        if (zugeklappt[g.name]) bereich.classList.add('zu');
        var kopf = el('div', 'gruppenkopf');
        var klappe = el('button', 'klappe');
        klappe.type = 'button';
        klappe.appendChild(symbol(PFEIL));
        klappe.appendChild(el('span', 'titel', g.title));
        var online = g.devices.filter(function (x) { return x.kind === 'online'; }).length;
        var info = el('span', 'info' + (online > 0 ? ' an' : ''),
          g.devices.length + (g.devices.length === 1 ? ' Gerät' : ' Geräte') + (online > 0 ? ' · ' + online + ' online' : ''));
        klappe.appendChild(info);
        klappe.addEventListener('click', function () {
          zugeklappt[g.name] = !zugeklappt[g.name];
          try { localStorage.setItem('wol.zugeklappt', JSON.stringify(zugeklappt)); } catch (e) { }
          bereich.classList.toggle('zu', !!zugeklappt[g.name]);
        });
        kopf.appendChild(klappe);
        var gw = el('button', 'wecken');
        gw.type = 'button';
        gw.title = 'Alle Geräte der Gruppe wecken';
        gw.appendChild(symbol(EIN));
        gw.appendChild(el('span', 'breit', 'Gruppe wecken'));
        gw.appendChild(el('span', 'schmal', 'Wecken'));
        gw.addEventListener('click', function () { aktion('/api/group', { name: g.name }, gw); });
        kopf.appendChild(gw);
        bereich.appendChild(kopf);
      }
      var liste = el('div', 'geraete');
      g.devices.forEach(function (x) { liste.appendChild(geraet(x)); });
      bereich.appendChild(liste);
      neu.appendChild(bereich);
    });
    gruppen.textContent = '';
    gruppen.appendChild(neu);
    $('leer').hidden = gesamt > 0;
  }

  function geraet(x) {
    var zeile = el('div', 'geraet');
    zeile.appendChild(statusZeichen(x.kind));
    var daten = el('div', 'daten');
    daten.appendChild(el('div', 'name', x.name));
    var zustand = el('div', 'zustand ' + x.kind, x.text);
    if (x.ip) zustand.appendChild(el('span', 'ip', x.ip));
    daten.appendChild(zustand);
    if (x.error) daten.appendChild(el('div', 'fehlertext', x.error));
    zeile.appendChild(daten);
    // "…" links neben "Wecken": die Wecken-Knöpfe bilden rechts eine ruhige Spalte, bündig mit der Gruppe
    if (x.shutdown || x.ipmi) {
      var m = el('button', 'mehr');
      m.type = 'button';
      m.title = 'Herunterfahren …';
      m.setAttribute('aria-label', 'Weitere Aktionen für ' + x.name);
      m.appendChild(symbol(MEHR));
      m.addEventListener('click', function () { blattOeffnen(x); });
      zeile.appendChild(m);
    }
    var w = el('button', 'wecken');
    w.type = 'button';
    w.appendChild(symbol(EIN));
    w.appendChild(el('span', '', 'Wecken'));
    w.disabled = x.kind === 'busy';
    w.addEventListener('click', function () { aktion('/api/wake', { name: x.name }, w); });
    zeile.appendChild(w);
    return zeile;
  }

  // ---------- Aktionen ----------
  function aktion(pfad, daten, knopf) {
    if (knopf) knopf.disabled = true;
    return anfrage('POST', pfad, daten).then(function (a) {
      if (a._status === 401) { zeigeAnmeldung(); return; }
      meldung(a.text || (a.ok ? 'Erledigt.' : (a.error || 'Fehler')), a.ok ? 'ok' : 'error');
      setTimeout(laden, 400);
    }).catch(function () {
      meldung('Keine Verbindung zum PC.', 'error');
    }).then(function () {
      if (knopf) setTimeout(function () { knopf.disabled = false; }, 1500);
    });
  }

  function meldung(text, art) {
    var m = $('meldung');
    m.textContent = text;
    m.className = 'meldung ' + (art || '');
    m.hidden = false;
    clearTimeout(meldungTimer);
    meldungTimer = setTimeout(function () { m.hidden = true; }, 4000);
  }

  // Herunterfahren: Auswahl von unten, zweiter Tipp bestätigt
  function blattOeffnen(x) {
    $('blatt-titel').textContent = x.name;
    var knoepfe = $('blatt-knoepfe');
    knoepfe.textContent = '';
    var eintraege = [];
    if (x.shutdown) {
      eintraege.push({ text: 'Herunterfahren', modus: 'shutdown', frage: 'Ja, wirklich herunterfahren' });
      eintraege.push({ text: 'Neu starten', modus: 'restart', frage: 'Ja, wirklich neu starten' });
    }
    if (x.ipmi) eintraege.push({ text: 'Über IPMI ausschalten', modus: 'ipmi', frage: 'Ja, wirklich ausschalten' });
    eintraege.forEach(function (e) {
      var b = el('button', 'sekundaer gross');
      b.type = 'button';
      b.appendChild(symbol(EIN));
      var t = el('span', '', e.text);
      b.appendChild(t);
      var bereit = false;
      b.addEventListener('click', function () {
        if (!bereit) {
          bereit = true;
          b.classList.add('gefahr');
          t.textContent = e.frage;
          return;
        }
        blattSchliessen();
        aktion('/api/shutdown', { name: x.name, mode: e.modus });
      });
      knoepfe.appendChild(b);
    });
    $('blatt-hintergrund').hidden = false;
    $('blatt').hidden = false;
  }

  function blattSchliessen() {
    $('blatt').hidden = true;
    $('blatt-hintergrund').hidden = true;
  }

  // ---------- Start ----------
  $('anmeldeformular').addEventListener('submit', function (ev) {
    ev.preventDefault();
    var feld = $('pin');
    $('anmeldefehler').textContent = '';
    anfrage('POST', '/api/login', { passwort: feld.value }).then(function (a) {
      if (a.ok) { feld.value = ''; start(); return; }
      $('anmeldefehler').textContent = a.error || 'Anmeldung fehlgeschlagen.';
      feld.select();
    }).catch(function () { $('anmeldefehler').textContent = 'Keine Verbindung zum PC.'; });
  });
  $('alle').addEventListener('click', function () { aktion('/api/all', {}, $('alle')); });
  $('abmelden').addEventListener('click', function () {
    anfrage('POST', '/api/logout', {}).then(zeigeAnmeldung, zeigeAnmeldung);
  });
  $('blatt-abbrechen').addEventListener('click', blattSchliessen);
  function info(an) {
    $('info').hidden = !an;
    $('info-hintergrund').hidden = !an;
  }
  $('version').addEventListener('click', function () { info(true); });
  $('info-schliessen').addEventListener('click', function () { info(false); });
  $('info-hintergrund').addEventListener('click', function () { info(false); });
  document.addEventListener('keydown', function (ev) { if (ev.key === 'Escape') { info(false); blattSchliessen(); } });
  $('blatt-hintergrund').addEventListener('click', blattSchliessen);
  document.addEventListener('visibilitychange', function () { if (document.visibilityState === 'visible' && timer) laden(); });

  if ('serviceWorker' in navigator && window.isSecureContext) {
    navigator.serviceWorker.register('/sw.js').catch(function () { });
  }

  // Anmeldung per QR-Code: Einmal-Code steht hinter #pair= (wird nie an den Server als Adresse geschickt)
  function koppeln() {
    var m = /[#&]pair=([A-Za-z0-9]+)/.exec(location.hash || '');
    if (!m) return Promise.resolve();
    try { history.replaceState(null, '', location.pathname + location.search); } catch (e) { }
    return anfrage('POST', '/api/pair', { code: m[1] }).then(function (a) {
      if (!a.ok) $('anmeldefehler').textContent = a.error || 'Der QR-Code ist abgelaufen – bitte neu scannen oder das Passwort eingeben.';
    }, function () { });
  }
  koppeln().then(start);
})();
