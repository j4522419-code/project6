'use strict';

/* Host dashboard: pairing codes + QR codes, paired devices, shared files. */
(() => {
  const $ = (id) => document.getElementById(id);
  let info = null;
  let deadline = 0;
  let refreshing = false;
  let knownDevices = null;

  // ---- pairing ------------------------------------------------------------------

  async function loadInfo() {
    try {
      info = await PS.api('/api/host/info');
      deadline = Date.now() + info.secondsLeft * 1000;
      renderInfo();
    } catch (err) {
      PS.toast(err.message, 'error');
    }
  }

  function selectedAddress() {
    const wanted = PS.pref.get('ps.address');
    return info.addresses.find((a) => a.address === wanted) || info.addresses[0];
  }

  function setSrc(img, src) {
    if (img.getAttribute('src') !== src) img.src = src;
  }

  function renderInfo() {
    const address = selectedAddress();
    const addr = encodeURIComponent(address.address);

    $('host-name').textContent = info.hostName;
    document.title = `PairShare · ${info.hostName}`;
    $('pair-code').textContent = `${info.code.slice(0, 3)} ${info.code.slice(3)}`;
    $('link').value = address.link;
    $('folder-path').textContent = info.folder;
    $('folder-path').title = info.folder;
    setSrc($('qr-link'), `/api/host/qr?kind=link&addr=${addr}`);
    setSrc($('qr-quick'), `/api/host/qr?kind=quick&addr=${addr}&v=${info.version}`);
    $('offline-warning').hidden = address.address !== 'localhost';

    const picker = $('addr-picker');
    picker.hidden = info.addresses.length < 2;
    if (!picker.hidden) {
      $('addr-select').replaceChildren(...info.addresses.map((a) =>
        PS.h('option', { value: a.address, selected: a.address === address.address }, a.address)));
    }
    tick();
  }

  function tick() {
    if (!info) return;
    const left = Math.max(0, deadline - Date.now());
    const seconds = Math.ceil(left / 1000);
    $('expiry-bar').style.width = `${Math.min(100, (left / (info.lifetimeSeconds * 1000)) * 100).toFixed(2)}%`;
    $('expiry-text').textContent = left > 0
      ? `New code in ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
      : 'Refreshing…';
    if (left <= 0 && !refreshing) {
      refreshing = true;
      setTimeout(async () => { await loadInfo(); refreshing = false; }, 800);
    }
  }

  $('addr-select').addEventListener('change', (e) => {
    PS.pref.set('ps.address', e.target.value);
    renderInfo();
  });

  $('copy-link').addEventListener('click', async () => {
    const input = $('link');
    try {
      await navigator.clipboard.writeText(input.value);
    } catch {
      input.select();
      document.execCommand('copy');
    }
    PS.toast('Link copied');
  });

  $('regenerate').addEventListener('click', async () => {
    try {
      await PS.api('/api/host/regenerate', { method: 'POST' });
      await loadInfo();
      PS.toast('New pair code ready');
    } catch (err) {
      PS.toast(err.message, 'error');
    }
  });

  // ---- devices ------------------------------------------------------------------

  const deviceIcon = (name) => (/phone|iphone|android|ipad|tablet/i.test(name) ? '📱' : '💻');

  async function loadDevices() {
    let devices;
    try {
      devices = await PS.api('/api/host/devices');
    } catch (err) {
      PS.toast(err.message, 'error');
      return;
    }

    if (knownDevices) {
      for (const d of devices) if (!knownDevices.has(d.id)) PS.toast(`✓ ${d.name} paired`, 'ok');
    }
    knownDevices = new Set(devices.map((d) => d.id));

    $('device-count').textContent = devices.length;
    $('no-devices').hidden = devices.length > 0;
    $('device-list').replaceChildren(...devices.map((d) =>
      PS.h('li', { class: 'device' },
        PS.h('div', { class: 'device-icon', 'aria-hidden': 'true' }, deviceIcon(d.name)),
        PS.h('div', { class: 'device-main' },
          PS.h('div', { class: 'device-name' },
            PS.h('span', { class: d.online ? 'dot live' : 'dot', title: d.online ? 'Online' : 'Not connected right now' }),
            PS.h('span', { class: 'truncate' }, d.name)),
          PS.h('div', { class: 'device-meta' }, `${d.address} · paired ${PS.timeAgo(d.pairedAt)}`)),
        PS.h('button', { class: 'btn small ghost danger', type: 'button', onclick: () => unpair(d) }, 'Unpair'))));
  }

  async function unpair(device) {
    if (!confirm(`Unpair ${device.name}? It will need a new pair code to connect again.`)) return;
    try {
      await PS.api(`/api/host/devices/${encodeURIComponent(device.id)}`, { method: 'DELETE' });
      await loadDevices();
    } catch (err) {
      PS.toast(err.message, 'error');
    }
  }

  // ---- files --------------------------------------------------------------------

  const files = PS.createFileView($('file-list'), $('no-files'));
  const uploader = PS.createUploader({ listEl: $('upload-list'), onDone: files.load });

  $('file-input').addEventListener('change', (e) => {
    const picked = Array.from(e.target.files);
    e.target.value = '';
    uploader.add(picked);
  });
  PS.enableDrop(uploader.add);

  $('open-folder').addEventListener('click', async () => {
    try {
      await PS.api('/api/host/open-folder', { method: 'POST' });
    } catch (err) {
      PS.toast(err.message, 'error');
    }
  });

  // ---- live updates -------------------------------------------------------------

  let connectedOnce = false;
  PS.connectEvents(
    { files: files.load, pairing: loadInfo, devices: loadDevices },
    (online) => {
      $('status-dot').className = online ? 'dot live' : 'dot warn';
      if (online && connectedOnce) { loadInfo(); loadDevices(); files.load(); }
      if (online) connectedOnce = true;
    });

  setInterval(tick, 1000);
  loadInfo();
  loadDevices();
  files.load();
})();
