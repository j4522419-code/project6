'use strict';

/* Pair screen: six digit boxes that handle typing, backspace, paste and SMS-style autofill. */
(() => {
  const $ = (id) => document.getElementById(id);
  const boxes = Array.from(document.querySelectorAll('#code-inputs input'));
  const button = $('pair-btn');
  const error = $('error');
  const nameInput = $('device-name');
  let busy = false;
  let lockTimer = null;

  PS.api('/api/me').then((me) => {
    if (me.role !== 'anonymous') { location.replace('/'); return; }
    $('host-name').textContent = me.hostName;
    document.title = `Pair with ${me.hostName} · PairShare`;
  }).catch(() => { /* shown when they try to pair */ });

  if (new URLSearchParams(location.search).get('quick') === 'expired') {
    const notice = $('notice');
    notice.textContent = 'That quick-pair QR code was already used or has expired. Scan the new one on the computer, or type the pair code below.';
    notice.hidden = false;
    history.replaceState(null, '', '/');
  }

  nameInput.placeholder = guessName();
  nameInput.value = PS.pref.get('ps.deviceName') || '';

  function guessName() {
    const ua = navigator.userAgent;
    if (/iPhone/.test(ua)) return 'iPhone';
    if (/iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1)) return 'iPad';
    if (/Android/.test(ua)) return /Mobile/.test(ua) ? 'Android phone' : 'Android tablet';
    if (/CrOS/.test(ua)) return 'Chromebook';
    if (/Windows/.test(ua)) return 'Windows PC';
    if (/Macintosh/.test(ua)) return 'Mac';
    if (/Linux/.test(ua)) return 'Linux PC';
    return 'My device';
  }

  const code = () => boxes.map((b) => b.value).join('');

  function fill(digits, start) {
    let i = start;
    for (const ch of digits) {
      if (i >= boxes.length) break;
      boxes[i++].value = ch;
    }
    afterChange(Math.min(i, boxes.length - 1));
  }

  function afterChange(focusIndex) {
    error.textContent = '';
    if (code().length === boxes.length) {
      button.focus(); // hides the phone keyboard so the Pair button is visible
    } else {
      boxes[focusIndex].focus();
    }
  }

  boxes.forEach((box, i) => {
    box.addEventListener('input', () => {
      const digits = box.value.replace(/\D/g, '');
      if (digits.length > 1) {
        box.value = '';
        fill(digits, digits.length >= boxes.length ? 0 : i);
        return;
      }
      box.value = digits;
      if (digits) afterChange(Math.min(i + 1, boxes.length - 1));
    });

    box.addEventListener('keydown', (e) => {
      if (e.key === 'Backspace' && !box.value && i > 0) {
        e.preventDefault();
        boxes[i - 1].value = '';
        boxes[i - 1].focus();
      } else if (e.key === 'ArrowLeft' && i > 0) {
        e.preventDefault();
        boxes[i - 1].focus();
      } else if (e.key === 'ArrowRight' && i < boxes.length - 1) {
        e.preventDefault();
        boxes[i + 1].focus();
      }
    });

    box.addEventListener('paste', (e) => {
      const digits = (e.clipboardData ? e.clipboardData.getData('text') : '').replace(/\D/g, '');
      if (!digits) return;
      e.preventDefault();
      fill(digits, digits.length >= boxes.length ? 0 : i);
    });

    box.addEventListener('focus', () => box.select());
  });

  function setDisabled(disabled) {
    busy = disabled;
    button.disabled = disabled;
    boxes.forEach((b) => { b.disabled = disabled; });
  }

  function lockFor(seconds) {
    clearInterval(lockTimer);
    setDisabled(true);
    let left = seconds;
    const show = () => { button.textContent = `Try again in ${left}s`; };
    show();
    lockTimer = setInterval(() => {
      left -= 1;
      if (left > 0) return show();
      clearInterval(lockTimer);
      setDisabled(false);
      button.textContent = 'Pair';
      error.textContent = '';
      boxes[0].focus();
    }, 1000);
  }

  function rejectCode() {
    const group = $('code-inputs');
    group.classList.remove('shake');
    void group.offsetWidth; // restart the animation
    group.classList.add('shake');
    boxes.forEach((b) => { b.value = ''; });
  }

  $('pair-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    if (busy) return;
    if (code().length !== boxes.length) {
      error.textContent = 'Enter all 6 digits of the pair code.';
      boxes[boxes.findIndex((b) => !b.value)].focus();
      return;
    }

    const name = nameInput.value.trim();
    PS.pref.set('ps.deviceName', name);
    setDisabled(true);
    button.textContent = 'Pairing…';

    try {
      await PS.api('/api/pair', { method: 'POST', json: { code: code(), name: name || nameInput.placeholder } });
      $('pair-view').hidden = true;
      $('success-view').hidden = false;
      setTimeout(() => location.replace('/'), 900);
    } catch (err) {
      rejectCode();
      if (err.status === 429 && err.data) {
        error.textContent = err.message;
        lockFor(err.data.retryAfterSeconds || 60);
        return;
      }
      let message = err.message;
      if (err.status === 401 && err.data && err.data.attemptsLeft != null) {
        const n = err.data.attemptsLeft;
        message += ` ${n} ${n === 1 ? 'try' : 'tries'} left.`;
      }
      error.textContent = message;
      setDisabled(false);
      button.textContent = 'Pair';
      boxes[0].focus();
    }
  });
})();
