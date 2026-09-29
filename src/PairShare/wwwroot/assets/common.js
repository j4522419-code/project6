'use strict';

/* Shared helpers for the host dashboard and the device app. */
const PS = (() => {
  async function api(path, { method = 'GET', json } = {}) {
    const init = { method, headers: { 'X-PairShare': '1' }, credentials: 'same-origin', cache: 'no-store' };
    if (json !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(json);
    }

    let res;
    try {
      res = await fetch(path, init);
    } catch {
      throw Object.assign(new Error("Can't reach the computer. Is PairShare still running?"), { status: 0 });
    }

    const isJson = (res.headers.get('Content-Type') || '').includes('json');
    const data = isJson ? await res.json().catch(() => null) : null;
    if (!res.ok) {
      throw Object.assign(new Error((data && data.error) || `Something went wrong (${res.status}).`), { status: res.status, data });
    }
    return data;
  }

  /* Tiny DOM builder. Text is always inserted as text, never as HTML. */
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (value == null || value === false) continue;
      if (key === 'class') el.className = value;
      else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
      else el.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) {
      if (child == null || child === false) continue;
      el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  const pref = {
    get(key) { try { return localStorage.getItem(key); } catch { return null; } },
    set(key, value) { try { localStorage.setItem(key, value); } catch { /* private mode */ } },
  };

  function formatSize(bytes) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    return unit === 0 ? `${Math.round(bytes)} B` : `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
  }

  function timeAgo(iso) {
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seconds < 45) return 'just now';
    if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
    if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
    return new Date(iso).toLocaleDateString();
  }

  const KINDS = [
    [/\.(jpe?g|png|gif|webp|heic|heif|bmp|svg|tiff?|raw|dng)$/i, '🖼️'],
    [/\.(mp4|mov|m4v|avi|mkv|webm|3gp|wmv)$/i, '🎬'],
    [/\.(mp3|m4a|wav|flac|aac|ogg|opus|wma)$/i, '🎵'],
    [/\.(zip|rar|7z|tar|gz|tgz|bz2|xz)$/i, '🗜️'],
    [/\.pdf$/i, '📕'],
    [/\.(docx?|odt|rtf|pages)$/i, '📝'],
    [/\.(xlsx?|csv|ods|numbers)$/i, '📊'],
    [/\.(pptx?|odp|key)$/i, '📽️'],
    [/\.(apk|exe|msi|dmg|pkg|deb|rpm|appimage)$/i, '📦'],
    [/\.(txt|md|log|json|xml|ya?ml|ini|cs|js|ts|py|c|h|cpp|java|html|css|sh)$/i, '📄'],
  ];
  const fileIcon = (name) => (KINDS.find(([re]) => re.test(name)) || [null, '📎'])[1];

  function toast(message, kind = '') {
    const host = document.getElementById('toasts');
    if (!host) return;
    const el = h('div', { class: `toast ${kind}`, role: 'status' }, message);
    host.append(el);
    setTimeout(() => { el.classList.add('out'); setTimeout(() => el.remove(), 300); }, 3800);
  }

  /* Renders the shared-file list and toasts about files that arrive from someone else. */
  function createFileView(listEl, emptyEl) {
    let known = null;
    let loading = null;

    async function remove(file) {
      const question = file.mine
        ? `Stop sharing "${file.name}"?`
        : `Delete "${file.name}" from the shared folder?`;
      if (!confirm(question)) return;
      try {
        await api(`/api/files/${encodeURIComponent(file.id)}`, { method: 'DELETE' });
        await load();
      } catch (err) {
        toast(err.message, 'error');
      }
    }

    function row(file, fresh) {
      return h('li', { class: fresh ? 'file fresh' : 'file' },
        h('div', { class: 'file-icon', 'aria-hidden': 'true' }, fileIcon(file.name)),
        h('div', { class: 'file-main' },
          h('div', { class: 'file-name', title: file.name }, file.name),
          h('div', { class: 'file-meta' },
            h('span', null, formatSize(file.size)),
            h('span', { 'aria-hidden': 'true' }, '·'),
            file.mine ? h('span', { class: 'from-you' }, 'from you') : h('span', null, `from ${file.addedBy}`),
            h('span', { 'aria-hidden': 'true' }, '·'),
            h('span', { title: new Date(file.modified).toLocaleString() }, timeAgo(file.modified)))),
        h('div', { class: 'file-actions' },
          h('a', { class: 'btn small primary', href: `/api/files/${encodeURIComponent(file.id)}`, download: file.name, title: 'Download', 'aria-label': `Download ${file.name}` },
            h('span', { class: 'btn-icon', 'aria-hidden': 'true' }, '⬇'),
            h('span', { class: 'btn-text' }, 'Download')),
          file.canDelete
            ? h('button', { class: 'btn small icon ghost danger', type: 'button', title: 'Remove', 'aria-label': `Remove ${file.name}`, onclick: () => remove(file) }, '✕')
            : null));
    }

    async function load() {
      if (loading) return loading;
      loading = (async () => {
        try {
          const files = await api('/api/files');
          const fresh = new Set();
          if (known) {
            for (const f of files) {
              if (known.has(f.id)) continue;
              fresh.add(f.id);
              if (!f.mine) toast(`📥 ${f.addedBy} shared ${f.name}`);
            }
          }
          known = new Set(files.map((f) => f.id));
          listEl.replaceChildren(...files.map((f) => row(f, fresh.has(f.id))));
          emptyEl.hidden = files.length > 0;
        } catch (err) {
          if (err.status === 401) location.replace('/');
          else toast(err.message, 'error');
        } finally {
          loading = null;
        }
      })();
      return loading;
    }

    return { load };
  }

  /* Sends files one at a time with a progress bar each. */
  function createUploader({ listEl, maxBytes, onDone }) {
    const queue = [];
    let busy = false;

    function add(files) {
      for (const file of files) {
        if (maxBytes && file.size > maxBytes) {
          toast(`${file.name} is too big (limit ${formatSize(maxBytes)}).`, 'error');
          continue;
        }
        const bar = h('span');
        const status = h('span', { class: 'upload-status' }, 'Waiting…');
        const el = h('li', { class: 'upload' },
          h('div', { class: 'upload-top' }, h('span', { class: 'upload-name', title: file.name }, file.name), status),
          h('div', { class: 'bar' }, bar));
        listEl.append(el);
        queue.push({ file, el, bar, status });
      }
      pump();
    }

    async function pump() {
      if (busy) return;
      busy = true;
      while (queue.length) {
        const job = queue.shift();
        try {
          await send(job);
          job.el.classList.add('done');
          job.status.textContent = 'Sent ✓';
          job.bar.style.width = '100%';
          setTimeout(() => job.el.remove(), 2500);
          if (onDone) onDone();
        } catch (err) {
          job.el.classList.add('error');
          job.status.textContent = err.message;
          setTimeout(() => job.el.remove(), 9000);
        }
      }
      busy = false;
    }

    function send({ file, bar, status }) {
      return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        const started = performance.now();
        xhr.open('POST', `/api/files?name=${encodeURIComponent(file.name)}`);
        xhr.setRequestHeader('X-PairShare', '1');
        xhr.setRequestHeader('Content-Type', 'application/octet-stream');
        status.textContent = 'Sending…';
        xhr.upload.onprogress = (e) => {
          if (!e.lengthComputable || !e.total) return;
          const fraction = e.loaded / e.total;
          const seconds = (performance.now() - started) / 1000;
          bar.style.width = `${(fraction * 100).toFixed(1)}%`;
          status.textContent = `${Math.floor(fraction * 100)}%` + (seconds > 0.5 ? ` · ${formatSize(e.loaded / seconds)}/s` : '');
        };
        xhr.onload = () => {
          if (xhr.status >= 200 && xhr.status < 300) return resolve();
          let message = `Failed (${xhr.status})`;
          try { message = JSON.parse(xhr.responseText).error || message; } catch { /* not JSON */ }
          if (xhr.status === 401) setTimeout(() => location.replace('/'), 1500);
          reject(new Error(message));
        };
        xhr.onerror = () => reject(new Error('Connection lost'));
        xhr.send(file);
      });
    }

    return { add };
  }

  /* Highlights the drop zone while files are dragged anywhere over the window. */
  function enableDrop(onFiles) {
    let depth = 0;
    const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
    const reset = () => { depth = 0; document.body.classList.remove('dragging'); };
    window.addEventListener('dragenter', (e) => {
      if (!hasFiles(e)) return;
      e.preventDefault();
      depth++;
      document.body.classList.add('dragging');
    });
    window.addEventListener('dragleave', (e) => {
      if (!hasFiles(e)) return;
      depth = Math.max(0, depth - 1);
      if (!depth) reset();
    });
    window.addEventListener('dragover', (e) => { if (hasFiles(e)) e.preventDefault(); });
    window.addEventListener('drop', (e) => {
      if (!hasFiles(e)) return;
      e.preventDefault();
      reset();
      if (e.dataTransfer.files.length) onFiles(Array.from(e.dataTransfer.files));
    });
  }

  /* Live updates over Server-Sent Events, with recovery if the session disappears. */
  function connectEvents(handlers, onStatus) {
    let source;
    let retry;

    function open() {
      source = new EventSource('/api/events');
      source.onopen = () => onStatus && onStatus(true);
      source.onerror = () => {
        if (onStatus) onStatus(false);
        if (source.readyState === EventSource.CLOSED) {
          clearTimeout(retry);
          retry = setTimeout(recover, 3000);
        }
      };
      for (const [name, handler] of Object.entries(handlers)) {
        source.addEventListener(name, (e) => handler(JSON.parse(e.data || '{}')));
      }
    }

    async function recover() {
      try {
        const me = await api('/api/me');
        if (me.role === 'anonymous') { location.replace('/'); return; } // unpaired, or PairShare restarted
      } catch { /* still offline */ }
      open();
    }

    open();
  }

  return { api, h, pref, formatSize, timeAgo, toast, createFileView, createUploader, enableDrop, connectEvents };
})();
