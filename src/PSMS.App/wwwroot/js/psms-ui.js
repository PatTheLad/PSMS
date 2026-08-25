(() => {
  const html = () => document.documentElement;

  const clamp = (n, min, max) => Math.min(max, Math.max(min, n));

  window.psmsLayout = {
    apply(explorerWidth, editorRatio) {
      const root = html();
      const w = clamp(explorerWidth || 280, 220, 480);
      const r = clamp(editorRatio || 0.55, 0.2, 0.8);
      root.style.setProperty('--explorer-width', w + 'px');
      root.style.setProperty('--editor-flex', String(r));
      const explorer = document.querySelector('.psms-explorer');
      if (explorer) {
        explorer.style.width = w + 'px';
        explorer.style.flexBasis = w + 'px';
      }
    }
  };

  function ensureGhost(kind) {
    let ghost = document.getElementById('psms-split-ghost');
    if (!ghost) {
      ghost = document.createElement('div');
      ghost.id = 'psms-split-ghost';
      document.body.appendChild(ghost);
    }
    ghost.className = 'psms-split-ghost psms-split-ghost-' + kind;
    ghost.style.display = 'block';
    return ghost;
  }

  function hideGhost() {
    const ghost = document.getElementById('psms-split-ghost');
    if (ghost) {
      ghost.style.display = 'none';
    }
  }

  window.psmsDrag = {
    start(dotnet, mode) {
      const body = document.querySelector('.psms-body');
      const main = document.querySelector('.psms-main');
      const explorer = document.querySelector('.psms-explorer');
      if (!body) {
        return;
      }

      html().classList.add('psms-dragging');
      html().classList.add(mode === 'explorer' ? 'psms-dragging-col' : 'psms-dragging-row');

      let lastExplorer = parseFloat(getComputedStyle(html()).getPropertyValue('--explorer-width')) || 280;
      let lastRatio = parseFloat(getComputedStyle(html()).getPropertyValue('--editor-flex')) || 0.55;
      let raf = 0;
      let pending = null;

      // Editor split: ghost only during drag (avoid Monaco/results reflow every frame).
      // Explorer: update explorer width only (flex sibling absorbs space).
      const apply = () => {
        raf = 0;
        if (!pending) {
          return;
        }
        const e = pending;
        pending = null;

        if (mode === 'explorer') {
          const rect = body.getBoundingClientRect();
          const w = clamp(e.clientX - rect.left, 220, 480);
          lastExplorer = w;
          if (explorer) {
            explorer.style.width = w + 'px';
            explorer.style.flexBasis = w + 'px';
          }
          html().style.setProperty('--explorer-width', w + 'px');
        } else if (main) {
          const rect = main.getBoundingClientRect();
          const y = clamp(e.clientY - rect.top, rect.height * 0.2, rect.height * 0.8);
          lastRatio = clamp(y / Math.max(rect.height, 1), 0.2, 0.8);
          const ghost = ensureGhost('row');
          ghost.style.left = rect.left + 'px';
          ghost.style.width = rect.width + 'px';
          ghost.style.top = (rect.top + y) + 'px';
        }
      };

      const move = (e) => {
        pending = e;
        if (!raf) {
          raf = requestAnimationFrame(apply);
        }
      };

      const up = () => {
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', up);
        window.removeEventListener('pointercancel', up);
        if (raf) {
          cancelAnimationFrame(raf);
          raf = 0;
        }
        if (pending) {
          apply();
        }

        if (mode === 'editor') {
          html().style.setProperty('--editor-flex', String(lastRatio));
          hideGhost();
        }

        html().classList.remove('psms-dragging', 'psms-dragging-col', 'psms-dragging-row');
        if (mode === 'explorer') {
          dotnet.invokeMethodAsync('OnDragEnd', 'explorer', lastExplorer, 0);
        } else {
          dotnet.invokeMethodAsync('OnDragEnd', 'editor', 0, lastRatio);
        }
      };

      window.addEventListener('pointermove', move);
      window.addEventListener('pointerup', up);
      window.addEventListener('pointercancel', up);
    }
  };

  window.psmsColResize = {
    start(dotnet, colIndex, startX, startWidth) {
      const host = document.querySelector('.psms-results-grid-host .psms-grid-inner');
      if (!host) {
        return;
      }

      html().classList.add('psms-col-resizing');
      let width = startWidth;
      let raf = 0;
      let pendingX = null;

      let styleEl = document.getElementById('psms-col-resize-style');
      if (!styleEl) {
        styleEl = document.createElement('style');
        styleEl.id = 'psms-col-resize-style';
        document.head.appendChild(styleEl);
      }

      const paint = (w) => {
        styleEl.textContent =
          '.psms-grid-inner [data-col="' + colIndex + '"]{' +
          '--col-w:' + w + 'px !important;' +
          'width:' + w + 'px !important;' +
          'min-width:' + w + 'px !important;' +
          'max-width:' + w + 'px !important;' +
          'flex:0 0 ' + w + 'px !important;}';
        let total = 52;
        host.querySelectorAll('.psms-grid-th-wrap').forEach((h) => {
          const col = h.getAttribute('data-col');
          if (col === String(colIndex)) {
            total += w;
            h.setAttribute('data-w', String(w));
          } else {
            total += parseFloat(h.getAttribute('data-w')) || 120;
          }
        });
        host.style.width = total + 'px';
      };

      paint(startWidth);

      const apply = () => {
        raf = 0;
        if (pendingX == null) {
          return;
        }
        width = clamp(startWidth + (pendingX - startX), 56, 600);
        pendingX = null;
        paint(width);
      };

      const move = (e) => {
        pendingX = e.clientX;
        if (!raf) {
          raf = requestAnimationFrame(apply);
        }
      };

      const up = () => {
        window.removeEventListener('pointermove', move);
        window.removeEventListener('pointerup', up);
        window.removeEventListener('pointercancel', up);
        if (raf) {
          cancelAnimationFrame(raf);
          raf = 0;
        }
        if (pendingX != null) {
          apply();
        }
        styleEl.textContent = '';
        html().classList.remove('psms-col-resizing');
        dotnet.invokeMethodAsync('OnColResizeEnd', colIndex, width);
      };

      window.addEventListener('pointermove', move);
      window.addEventListener('pointerup', up);
      window.addEventListener('pointercancel', up);
    }
  };

  window.psmsIsMonacoFocused = () => {
    const el = document.activeElement;
    return !!(el && (el.closest('.monaco-editor') || el.closest('.monaco-host')));
  };
})();
