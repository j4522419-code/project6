'use strict';

/* The web app a paired phone / laptop sees. */
(async () => {
  const $ = (id) => document.getElementById(id);

  let me;
  try {
    me = await PS.api('/api/me');
  } catch (err) {
    PS.toast(err.message, 'error');
    return;
  }
  if (me.role !== 'device') {
    location.replace('/');
    return;
  }

  document.querySelectorAll('.host-name').forEach((el) => { el.textContent = me.hostName; });
  $('me-name').textContent = me.name;
  document.title = `PairShare · ${me.hostName}`;

  const files = PS.createFileView($('file-list'), $('no-files'));
  const uploader = PS.createUploader({ listEl: $('upload-list'), maxBytes: me.maxUploadBytes, onDone: files.load });

  $('file-input').addEventListener('change', (e) => {
    const picked = Array.from(e.target.files);
    e.target.value = '';
    uploader.add(picked);
  });
  PS.enableDrop(uploader.add);

  $('disconnect').addEventListener('click', async () => {
    if (!confirm(`Disconnect from ${me.hostName}? You'll need a new pair code to connect again.`)) return;
    try {
      await PS.api('/api/unpair', { method: 'POST' });
    } finally {
      location.replace('/');
    }
  });

  function setOnline(online) {
    $('status-dot').className = online ? 'dot live' : 'dot warn';
    $('status-text').replaceChildren(
      online ? 'Connected to ' : 'Reconnecting to ',
      PS.h('strong', { class: 'host-name' }, me.hostName),
      online ? '' : '…');
  }

  let connectedOnce = false;
  PS.connectEvents(
    {
      files: files.load,
      revoked: () => {
        PS.toast('This device was unpaired from the computer.', 'error');
        setTimeout(() => location.replace('/'), 1500);
      },
    },
    (online) => {
      setOnline(online);
      if (online && connectedOnce) files.load();
      if (online) connectedOnce = true;
    });

  // Phones pause background tabs; catch up when the tab comes back.
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') files.load();
  });

  files.load();
})();
