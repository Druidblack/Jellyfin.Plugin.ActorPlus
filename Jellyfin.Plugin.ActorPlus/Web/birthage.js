/* ActorPlus - Jellyfin Web (auto injected by plugin)
 *
 * Based on the proven patterns from multi_tag.js:
 * - Wait for ApiClient
 * - Scan DOM for person portraits (cardImageContainer / listItemImage)
 * - Batch-call the plugin API and paint badges
 *
 * This build adds SAFE support for virtualized / recycled DOM items:
 * - We DO NOT observe global attribute changes (can freeze the UI)
 * - Instead we run a throttled periodic rescan + scroll-debounced rescan
 * - Each element tracks its last seen personId; when it changes we update badge
 */

(function () {
  'use strict';

  const API_STATUS = '/ActorPlus/status';
  const API_BATCH  = '/ActorPlus/ages';

  // Where person portraits appear in Jellyfin Web
  const TARGET_SELECTORS = ['a.cardImageContainer', 'a.cardImageContainer-withZoom', '.listItemImage'].join(',');

  // Scan strategy (kept intentionally conservative to avoid UI stalls)
  const PERIODIC_SCAN_MS = 1200;   // catches virtualized lists that recycle elements
  const SCROLL_DEBOUNCE_MS = 180;  // quicker reaction when user scrolls

  // id -> string|null (cached)
  const ageCache = new Map();

  // id -> "YYYY-MM-DD" (birth date from API)
  const birthDateCache = new Map();

  // id -> ISO2 birth country code (from API)
  const birthCountryIso2Cache = new Map();

  // id -> birthplace string (from API)
  const birthPlaceCache = new Map();

  // id -> bool (from API); presence in map means we already know deceased status
  const deceasedCache = new Map();

  // id -> Set<Element>
  const waiters = new Map();

  // element -> last normalized id (prevents stale overlays on recycled nodes)
  const elementId = new WeakMap();

  // queue / batch request
  const queued = new Set();
  let flushTimer = null;
  let enabled = null;
  let showAgeAtRelease = true;
  let showAgeIcons = false;
  let showBirthCountryFlag = true;
  let showBirthPlaceText = false;
  let showDeceasedOverlay = false;
  let enableHoverFilmography = false;
  let hoverFilmographyLimit = 12;
  let randomizeHoverFilmography = false;
  let enableHoverCastMenu = false;
  let hoverCastLimit = 12;
  let statusLoadedAt = 0;
  const STATUS_TTL_MS = 10000;

  // Twemoji flag SVG base (same idea as multi_tag.js)
  const TWEMOJI_FLAG_BASE = 'https://cdnjs.cloudflare.com/ajax/libs/twemoji/14.0.2/svg/';
  const twemojiUrlCache = new Map();

  // Current context (movie/series page) premiere date. Used for "age at release" badge.
  let contextItemId = null;           // normalized id
  let contextPremiereUtc = null;      // Date (UTC Y-M-D)
  let contextIsPerson = false;

  // Route id getter (assigned after ApiClient bootstrap)
  let getRouteId = () => null;

  // Some Jellyfin pages can reference Person ids before the Person item is fully materialized in the server database.
  // Opening a person details page forces Jellyfin to create/fetch the Person item (and often its metadata).
  // When the plugin API returns no data for a person, we do a lightweight "touch" via ApiClient.getItem(userId, id)
  // and then re-query the plugin once. This avoids requiring users to manually open each actor page.
  const touchedAt = new Map();
  const TOUCH_COOLDOWN_MS = 10 * 60 * 1000; // 10 minutes
  const TOUCH_MAX_PER_FLUSH = 25;

  // scanning throttle
  let scanScheduled = false;
  let scrollTimer = null;

  // ===== Helpers =====
  function normalizeId(id) {
    return String(id || '').toLowerCase().replace(/-/g, '');
  }

  function iso2ToTwemojiUrl(iso2) {
    const code = String(iso2 || '').trim().toUpperCase();
    if (!/^[A-Z]{2}$/.test(code)) return null;
    const cached = twemojiUrlCache.get(code);
    if (cached) return cached;

    // Regional Indicator Symbol Letters: U+1F1E6 + (A..Z)
    const a = 0x1F1E6 + (code.charCodeAt(0) - 65);
    const b = 0x1F1E6 + (code.charCodeAt(1) - 65);
    const url = TWEMOJI_FLAG_BASE + a.toString(16) + '-' + b.toString(16) + '.svg';
    twemojiUrlCache.set(code, url);
    return url;
  }

  function parseYmdToUtcDate(ymd) {
    if (!ymd) return null;
    const m = String(ymd).trim().match(/^(\d{4})-(\d{2})-(\d{2})/);
    if (!m) return null;
    const y = parseInt(m[1], 10);
    const mo = parseInt(m[2], 10) - 1;
    const d = parseInt(m[3], 10);
    if (!Number.isFinite(y) || !Number.isFinite(mo) || !Number.isFinite(d)) return null;
    return new Date(Date.UTC(y, mo, d));
  }

  function toUtcYmd(date) {
    if (!date || !(date instanceof Date) || isNaN(date.getTime())) return null;
    return new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  }

  function calcAgeYearsAt(birthUtc, atUtc) {
    if (!birthUtc || !atUtc) return null;
    const by = birthUtc.getUTCFullYear();
    const bm = birthUtc.getUTCMonth();
    const bd = birthUtc.getUTCDate();
    const ay = atUtc.getUTCFullYear();
    const am = atUtc.getUTCMonth();
    const ad = atUtc.getUTCDate();
    let years = ay - by;
    if (am < bm || (am === bm && ad < bd)) years--;
    return years;
  }

  function extractItemIdFromBg(el) {
    if (!el) return null;
    const inline = el.getAttribute && (el.getAttribute('style') || '');
    let m = inline && inline.match(/\/Items\/([a-f0-9]{32})\/Images/i);
    if (m) return normalizeId(m[1]);

    try {
      const bg = (getComputedStyle(el).backgroundImage || '');
      m = bg.match(/\/Items\/([a-f0-9]{32})\/Images/i);
      return m ? normalizeId(m[1]) : null;
    } catch {
      return null;
    }
  }

  // Returns the normalized Jellyfin item id (32 hex) for a portrait element
  function extractItemId(el) {
    if (!el) return null;

    // 1) nearest [data-id]
    const withDataId = el.closest && el.closest('[data-id]');
    if (withDataId && withDataId.dataset && withDataId.dataset.id && /^[a-f0-9]{32}$/i.test(withDataId.dataset.id)) {
      return normalizeId(withDataId.dataset.id);
    }

    // 2) link href contains id=
    const link = (el.closest && el.closest('a[href*="id="]')) || (el.tagName === 'A' ? el : null);
    if (link && link.getAttribute) {
      const href = link.getAttribute('href') || '';
      const m = href.match(/(?:\?|&)id=([0-9a-fA-F-]{32,36})/i);
      if (m) return normalizeId(m[1]);
    }

    // 3) listItemImage background
    if (el.classList && el.classList.contains('listItemImage')) {
      const bgId = extractItemIdFromBg(el);
      if (bgId) return bgId;
    }

    return null;
  }

  // Heuristic: only treat cardImageContainer as a Person card if its padder contains the "person" icon marker
  function isPersonCardAnchor(a) {
    if (!a || a.tagName !== 'A') return false;

    // Most reliable signal: the element lives inside the cast/people section of a details page.
    if (a.closest('#castContent, #cast, .castContent, .cast, .peopleSection, .detailsCast, .itemDetailsCast')) {
      return true;
    }

    // Try to locate the "person" marker icon in the nearest card wrapper (or adjacent padder).
    const wrapper = a.closest('.cardScalable, .cardBox, .card, .cardWrapper, .cardContainer') || a.parentElement;
    if (wrapper && wrapper.querySelector) {
      const icon = wrapper.querySelector('span.cardImageIcon.person, span.material-icons.person, .cardImageIcon.person');
      if (icon) return true;
    }

    const prev = a.previousElementSibling;
    if (prev && prev.querySelector) {
      const icon2 = prev.querySelector('span.cardImageIcon.person, span.material-icons.person, .cardImageIcon.person');
      if (icon2) return true;
    }

    const next = a.nextElementSibling;
    if (next && next.querySelector) {
      const icon3 = next.querySelector('span.cardImageIcon.person, span.material-icons.person, .cardImageIcon.person');
      if (icon3) return true;
    }

    // Fallback: some themes/components add an explicit person class.
    const cls = String(a.className || '');
    if (/personCard/i.test(cls)) return true;

    return false;
  }


  function removeBadge(container) {
    if (!container) return;
    const badge = container.querySelector && container.querySelector(':scope > .birthage-badge');
    if (badge && badge.parentNode) badge.parentNode.removeChild(badge);
  }

  function removeReleaseBadge(container) {
    if (!container) return;
    const badge = container.querySelector && container.querySelector(':scope > .birthage-release-badge');
    if (badge && badge.parentNode) badge.parentNode.removeChild(badge);
  }

  function removeFlag(container) {
    if (!container) return;
    const el = container.querySelector && container.querySelector(':scope > .birthage-flag');
    if (el && el.parentNode) el.parentNode.removeChild(el);
  }

  function removeDeceasedBadge(container) {
    if (!container) return;
    const el = container.querySelector && container.querySelector(':scope > .birthage-deceased');
    if (el && el.parentNode) el.parentNode.removeChild(el);
  }

  function removeDeceasedMask(container) {
    if (!container) return;
    const el = container.querySelector && container.querySelector(':scope > .birthage-deceased-mask');
    if (el && el.parentNode) el.parentNode.removeChild(el);
  }

  function removeBirthplaceLine(container) {
    if (!container) return;
    const el = container.querySelector && container.querySelector(':scope > .birthage-birthplace');
    if (el && el.parentNode) el.parentNode.removeChild(el);
  }

  function ensureBadge(container, text) {
    if (!container || !text) return;

    // Only touch layout-affecting properties once
    if (!container.classList.contains('birthage-container')) {
      container.classList.add('birthage-container');
      try {
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';
      } catch {}
    }

    let badge = container.querySelector(':scope > .birthage-badge');
    if (!badge) {
      badge = document.createElement('div');
      badge.className = 'birthage-badge';
      container.appendChild(badge);
    }
    // Avoid unnecessary DOM writes
    if (badge.textContent !== text) badge.textContent = text;
  }

  function ensureReleaseBadge(container, primaryText, secondaryText) {
    // Top-right badge: when ShowAgeAtRelease is enabled -> primary=age-at-release, secondary=current age
    // Otherwise -> primary=current age, secondary=null
    if (!container || !primaryText) return;

    if (!container.classList.contains('birthage-container')) {
      container.classList.add('birthage-container');
      try {
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';
      } catch {}
    }

    let badge = container.querySelector(':scope > .birthage-release-badge');
    if (!badge) {
      badge = document.createElement('div');
      badge.className = 'birthage-release-badge';
      container.appendChild(badge);
    }

    // Build inner HTML only when it changes (avoid reflow spam)
    const key = secondaryText ? (primaryText + '\n' + secondaryText) : primaryText;
    if (badge.getAttribute('data-birthage-key') !== key) {
      badge.setAttribute('data-birthage-key', key);
      badge.innerHTML = '';
      const line1 = document.createElement('div');
      line1.className = 'birthage-line birthage-line-primary';
      line1.textContent = primaryText;
      badge.appendChild(line1);

      if (secondaryText) {
        const line2 = document.createElement('div');
        line2.className = 'birthage-line birthage-line-secondary';
        line2.textContent = secondaryText;
        badge.appendChild(line2);
      }
    }
  }

  function ensureFlag(container, iso2) {
    if (!container || !iso2) return;
    const url = iso2ToTwemojiUrl(iso2);
    if (!url) return;

    if (!container.classList.contains('birthage-container')) {
      container.classList.add('birthage-container');
      try {
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';
      } catch {}
    }

    let wrapper = container.querySelector(':scope > .birthage-flag');
    if (!wrapper) {
      wrapper = document.createElement('div');
      wrapper.className = 'birthage-flag';
      const img = document.createElement('img');
      img.decoding = 'async';
      img.alt = iso2;
      wrapper.appendChild(img);
      container.appendChild(wrapper);
    }

    const img = wrapper.querySelector('img');
    if (img) {
      if (img.getAttribute('data-iso2') !== String(iso2).toUpperCase()) {
        img.setAttribute('data-iso2', String(iso2).toUpperCase());
        img.src = url;
      }
    }
  }

  function ensureBirthplaceLine(container, iso2, placeText) {
    if (!container || !iso2 || !placeText) return;
    const url = iso2ToTwemojiUrl(iso2);
    if (!url) return;

    if (!container.classList.contains('birthage-container')) {
      container.classList.add('birthage-container');
      try {
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';
      } catch {}
    }

    let wrapper = container.querySelector(':scope > .birthage-birthplace');
    if (!wrapper) {
      wrapper = document.createElement('div');
      wrapper.className = 'birthage-birthplace';

      const img = document.createElement('img');
      img.decoding = 'async';
      img.alt = String(iso2).toUpperCase();

      const span = document.createElement('span');
      span.className = 'birthage-birthplace-text';

      wrapper.appendChild(img);
      wrapper.appendChild(span);
      container.appendChild(wrapper);
    }

    const img = wrapper.querySelector('img');
    if (img) {
      const code = String(iso2).toUpperCase();
      if (img.getAttribute('data-iso2') !== code) {
        img.setAttribute('data-iso2', code);
        img.alt = code;
        img.src = url;
      }
    }

    const span = wrapper.querySelector('span');
    if (span) {
      const t = String(placeText).trim();
      if (span.textContent !== t) span.textContent = t;
      // show full text on hover
      wrapper.title = t;
    }
  }

  function ensureDeceasedMask(container) {
    if (!container) return;

    if (!container.classList.contains('birthage-container')) {
      container.classList.add('birthage-container');
      try {
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';
      } catch {}
    }

    let mask = container.querySelector(':scope > .birthage-deceased-mask');
    if (!mask) {
      mask = document.createElement('div');
      mask.className = 'birthage-deceased-mask';
      container.appendChild(mask);
    }
  }

  function ensureDeceasedBadge(container) {
    if (!container) return;

    if (!container.classList.contains('birthage-container')) {
      container.classList.add('birthage-container');
      try {
        const pos = getComputedStyle(container).position;
        if (pos === 'static') container.style.position = 'relative';
      } catch {}
    }

    let badge = container.querySelector(':scope > .birthage-deceased');
    if (!badge) {
      badge = document.createElement('div');
      badge.className = 'birthage-deceased';
      badge.textContent = '✝';
      container.appendChild(badge);
    }
  }

  function registerWaiter(id, el) {
    if (!id || !el) return;

    // If Jellyfin recycled this DOM node for another person, detach from old waiter set and remove stale badge
    const prevId = elementId.get(el);
    if (prevId && prevId !== id) {
      const prevSet = waiters.get(prevId);
      if (prevSet) prevSet.delete(el);
      removeBadge(el);
      removeReleaseBadge(el);
      removeFlag(el);
      removeBirthplaceLine(el);
      removeDeceasedBadge(el);
      removeDeceasedMask(el);
    }
    elementId.set(el, id);

    let set = waiters.get(id);
    if (!set) {
      set = new Set();
      waiters.set(id, set);
    }
    set.add(el);
  }

  function computeAgeAtUtc(birthUtc, refUtc) {
    if (!birthUtc || !refUtc) return null;
    const by = birthUtc.getUTCFullYear();
    const bm = birthUtc.getUTCMonth();
    const bd = birthUtc.getUTCDate();

    const ry = refUtc.getUTCFullYear();
    const rm = refUtc.getUTCMonth();
    const rd = refUtc.getUTCDate();

    let years = ry - by;
    if (rm < bm || (rm === bm && rd < bd)) years--;
    if (!Number.isFinite(years)) return null;
    if (years < 0) return null;
    return years;
  }

  function applyBadgesToElement(el, id) {
    if (!el || !id) return;

    // Top-right: always show CURRENT age.
    // If "Show age at release" is enabled and context date is known, show age-at-release ABOVE the current age.
    const current = ageCache.get(id);

    // Remove legacy bottom-right badge (we now render ages in the top-right corner)
    removeBadge(el);

    if (current) {
      const currentText = showAgeIcons ? ('🎂 ' + current) : current;
      let releaseText = null;

      if (showAgeAtRelease && contextPremiereUtc && !contextIsPerson && (!getRouteId || getRouteId() === contextItemId)) {
        const birthStr = birthDateCache.get(id);
        const birthUtc = parseYmdToUtcDate(birthStr);
        const years = computeAgeAtUtc(birthUtc, contextPremiereUtc);
        if (years != null) releaseText = years + ' y';
      }

      if (releaseText) {
        const primary = showAgeIcons ? ('🎬 ' + releaseText) : releaseText;
        ensureReleaseBadge(el, primary, currentText);
      } else {
        ensureReleaseBadge(el, currentText, null);
      }
    } else {
      removeReleaseBadge(el);
    }

    // Birth country flag / birthplace line (bottom-left)
    if (showBirthCountryFlag) {
      const iso2 = birthCountryIso2Cache.get(id);
      const place = birthPlaceCache.get(id);

      if (iso2 && showBirthPlaceText && place) {
        ensureBirthplaceLine(el, iso2, place);
        removeFlag(el);
        el.classList.add('birthage-has-birthplace');
      } else {
        removeBirthplaceLine(el);
        el.classList.remove('birthage-has-birthplace');
        if (iso2) ensureFlag(el, iso2);
        else removeFlag(el);
      }
    } else {
      removeFlag(el);
      removeBirthplaceLine(el);
      el.classList.remove('birthage-has-birthplace');
    }

    // Deceased overlay (✝ + gray portrait)
    if (showDeceasedOverlay) {
      const isDec = (deceasedCache.get(id) === true);
      if (isDec) {
        ensureDeceasedMask(el);
        ensureDeceasedBadge(el);
      } else {
        removeDeceasedBadge(el);
        removeDeceasedMask(el);
      }
    } else {
      removeDeceasedBadge(el);
      removeDeceasedMask(el);
    }
  }

  function deliver(id) {
    if (!id) return;
    const set = waiters.get(id);
    if (!set) return;
    set.forEach(el => {
      if (elementId.get(el) === id) applyBadgesToElement(el, id);
    });
  }

  // ===== ApiClient bootstrap (multi_tag.js approach) =====
  const ApiClientRef =
    (typeof window !== 'undefined' && (window.ApiClient || (window.unsafeWindow && window.unsafeWindow.ApiClient)))
      ? (window.ApiClient || window.unsafeWindow.ApiClient)
      : null;

  if (!ApiClientRef) {
    const MAX_WAIT_MS = 15000;
    const start = Date.now();
    const timer = setInterval(() => {
      const api = window.ApiClient || (window.unsafeWindow && window.unsafeWindow.ApiClient);
      if (api || (Date.now() - start) > MAX_WAIT_MS) {
        clearInterval(timer);
        if (api) bootstrap(api);
      }
    }, 300);
  } else {
    bootstrap(ApiClientRef);
  }

  function bootstrap(ApiClient) {
    try { console.debug('[ActorPlus] JS loaded (nav-safe)'); } catch {}

    function getUserIdSafe() {
      try {
        if (typeof ApiClient.getCurrentUserId === 'function') {
          const u = ApiClient.getCurrentUserId();
          if (u) return u;
        }
      } catch {}

      // Fallback: parse persisted credentials (best-effort; format can differ by version)
      try {
        const raw = window.localStorage && window.localStorage.getItem('jellyfin_credentials');
        if (!raw) return null;
        const parsed = JSON.parse(raw);
        const servers = parsed?.Servers || parsed?.servers;
        const s0 = Array.isArray(servers) ? servers[0] : null;
        return s0?.UserId || s0?.userId || null;
      } catch {
        return null;
      }
    }

    function delay(ms) {
      return new Promise(resolve => setTimeout(resolve, ms));
    }

    function getCurrentRouteItemId() {
      // Jellyfin is an SPA; URL can change via hash, pushState, replaceState, etc.
      return extractIdFromUrlString(window.location.hash) || extractIdFromUrlString(window.location.href);
    }

    // expose to outer helpers
    getRouteId = getCurrentRouteItemId;

    let contextRefreshTimer = null;
    async function refreshContext() {
      const itemId = getCurrentRouteItemId();
      if (!itemId) {
        contextItemId = null;
        contextPremiereUtc = null;
        contextIsPerson = false;
        return;
      }

      // Only refresh if the route id changed
      if (contextItemId === itemId && contextPremiereUtc !== null) return;

      contextItemId = itemId;
      contextPremiereUtc = null;
      contextIsPerson = false;

      const userId = getUserIdSafe();
      if (!userId) return;

      try {
        const item = await ApiClient.getItem(userId, itemId);
        if (!item) return;
        const type = String(item.Type || item.type || '').toLowerCase();
        contextIsPerson = (type === 'person');
        if (contextIsPerson) {
          contextPremiereUtc = null;
          return;
        }

        // Prefer exact premiere date; fallback to production year
        const premiere = item.PremiereDate || item.premiereDate || null;
        if (premiere) {
          const d = toUtcYmd(new Date(premiere));
          if (d) contextPremiereUtc = d;
        } else {
          const py = item.ProductionYear || item.productionYear;
          if (py) {
            const y = parseInt(String(py), 10);
            if (Number.isFinite(y) && y > 1800) contextPremiereUtc = new Date(Date.UTC(y, 0, 1));
          }
        }
      } catch {
        // ignore
      }
    }

function resetContextForRoute() {
  const itemId = getCurrentRouteItemId();
  if (!itemId) {
    contextItemId = null;
    contextPremiereUtc = null;
    contextIsPerson = false;
    return;
  }

  // If navigation happened, clear contextPremiereUtc immediately to avoid showing stale "age at release"
  // from the previous page while the new details item is still loading.
  if (contextItemId !== itemId) {
    contextItemId = itemId;
  }
  contextPremiereUtc = null;
  contextIsPerson = false;
}


    function scheduleContextRefresh() {
      if (contextRefreshTimer) clearTimeout(contextRefreshTimer);
      contextRefreshTimer = setTimeout(async () => {
        await refreshContext();
        // Context changes affect "age at release" badges, so rescan to update.
        scheduleScan(document, true);
      }, 120);
    }

    async function touchMissingPersons(ids) {
      if (!ids || !ids.length) return;
      const userId = getUserIdSafe();
      if (!userId) return;

      const now = Date.now();
      const todo = [];
      for (const id of ids) {
        const last = touchedAt.get(id) || 0;
        if ((now - last) < TOUCH_COOLDOWN_MS) continue;
        touchedAt.set(id, now);
        todo.push(id);
        if (todo.length >= TOUCH_MAX_PER_FLUSH) break;
      }
      if (!todo.length) return;

      // Concurrency-limited touching to avoid UI stalls
      const CONC = 4;
      let idx = 0;

      async function worker() {
        while (idx < todo.length) {
          const i = idx++;
          const pid = todo[i];
          try {
            // ApiClient.getItem will go through Jellyfin's standard API and may materialize the Person item.
            // Race with a small timeout so we never hang.
            await Promise.race([
              ApiClient.getItem(userId, pid),
              delay(4000).then(() => { throw new Error('touch timeout'); })
            ]);
          } catch {
            // ignore
          }
        }
      }

      const n = Math.min(CONC, todo.length);
      await Promise.all(Array.from({ length: n }, () => worker()));
    }

    async function loadStatus() {
      if (enabled !== null && (Date.now() - statusLoadedAt) < STATUS_TTL_MS) return enabled;
      try {
        const json = await ApiClient.ajax({
          type: 'GET',
          url: ApiClient.getUrl(API_STATUS),
          dataType: 'json'
        });
        const flag = json ? (json.Enabled ?? json.enabled) : null;
        const rel = json ? (json.ShowAgeAtRelease ?? json.showAgeAtRelease) : null;
        const ico = json ? (json.ShowAgeIcons ?? json.showAgeIcons) : null;
        const flg = json ? (json.ShowBirthCountryFlag ?? json.showBirthCountryFlag) : null;
        const bpt = json ? (json.ShowBirthPlaceText ?? json.showBirthPlaceText) : null;
        const dec = json ? (json.ShowDeceasedOverlay ?? json.showDeceasedOverlay) : null;
        const hfg = json ? (json.EnableHoverFilmography ?? json.enableHoverFilmography) : null;
        const hfl = json ? (json.HoverFilmographyLimit ?? json.hoverFilmographyLimit) : null;
        const hfr = json ? (json.RandomizeHoverFilmography ?? json.randomizeHoverFilmography) : null;
        const hcm = json ? (json.EnableHoverCastMenu ?? json.enableHoverCastMenu) : null;
        const hcl = json ? (json.HoverCastLimit ?? json.hoverCastLimit) : null;
        enabled = !!flag;
        showAgeAtRelease = (rel === null || rel === undefined) ? true : !!rel;
        showAgeIcons = (ico === null || ico === undefined) ? false : !!ico;
        showBirthCountryFlag = (flg === null || flg === undefined) ? true : !!flg;
        showBirthPlaceText = (bpt === null || bpt === undefined) ? false : !!bpt;
        showDeceasedOverlay = (dec === null || dec === undefined) ? false : !!dec;
        enableHoverFilmography = (hfg === null || hfg === undefined) ? false : !!hfg;
        hoverFilmographyLimit = (hfl === null || hfl === undefined) ? 12 : Math.max(1, Math.min(100, parseInt(hfl, 10) || 12));
        randomizeHoverFilmography = (hfr === null || hfr === undefined) ? false : !!hfr;
        enableHoverCastMenu = (hcm === null || hcm === undefined) ? false : !!hcm;
        hoverCastLimit = (hcl === null || hcl === undefined) ? 12 : Math.max(1, Math.min(100, parseInt(hcl, 10) || 12));
        statusLoadedAt = Date.now();
        return enabled;
      } catch {
        // keep previous value if any
        if (enabled === null) enabled = false;
        return enabled;
      }
    }

    function queueFetch(id) {
      if (!id) return;

      // Even if we already have the current-age text cached, we may still need
      // extra data (birth date for age-at-release, or country ISO2 for the flag).
      const needAge   = !ageCache.has(id);
      const needBirth = (showAgeAtRelease && !birthDateCache.has(id));
      const needIso2  = (showBirthCountryFlag && !birthCountryIso2Cache.has(id));
      const needPlace = (showBirthCountryFlag && showBirthPlaceText && !birthPlaceCache.has(id));
      const needDec   = (showDeceasedOverlay && !deceasedCache.has(id));

      if (!needAge && !needBirth && !needIso2 && !needPlace && !needDec) {
        deliver(id);
        return;
      }

      if (queued.has(id)) return;
      queued.add(id);
      if (flushTimer) clearTimeout(flushTimer);
      flushTimer = setTimeout(flushQueue, 120);
    }

    async function flushQueue() {
      flushTimer = null;
      const ids = Array.from(queued);
      queued.clear();
      if (!ids.length) return;

      try {
        const resp = await ApiClient.ajax({
          type: 'POST',
          url: ApiClient.getUrl(API_BATCH),
          dataType: 'json',
          contentType: 'application/json',
          data: JSON.stringify({ personIds: ids })
        });

        const byId = Object.create(null);
        if (resp && typeof resp === 'object') {
          for (const k of Object.keys(resp)) byId[normalizeId(k)] = resp[k];
        }

        const missing = [];

        for (const id of ids) {
          const rec = byId[id];

          const birth = rec ? (rec.BirthDate ?? rec.birthDate) : null;
          if (birth) birthDateCache.set(id, String(birth).trim());

          const iso2 = rec ? (rec.BirthCountryIso2 ?? rec.birthCountryIso2) : null;
          if (iso2) birthCountryIso2Cache.set(id, String(iso2).trim());

          const place = rec ? (rec.BirthPlace ?? rec.birthPlace) : null;
          if (place) birthPlaceCache.set(id, String(place).trim());

          // Cache deceased status (presence in map means "known")
          const decRaw = rec ? (rec.IsDeceased ?? rec.isDeceased) : null;
          if (decRaw !== null && decRaw !== undefined) {
            deceasedCache.set(id, !!decRaw);
          }

          const ageTextRaw = rec ? (rec.AgeText ?? rec.ageText ?? rec.AgeYears ?? rec.ageYears) : null;
          const n = (ageTextRaw != null) ? String(ageTextRaw).trim() : '';
          const text = n ? (n + ' y') : null;

          // IMPORTANT: don't "poison" the cache with null permanently.
          // If metadata appears later (e.g. after a Person item is materialized), we want a chance to re-fetch.
          if (text) {
            ageCache.set(id, text);
          }

          // Decide if we need a "touch" for this person:
          // - missing current-age text
          // - missing birth date (needed for age-at-release)
          // - missing ISO2 (needed for birth country flag)
          const needAge   = !ageCache.has(id);
          const needBirth = (showAgeAtRelease && !birthDateCache.has(id));
          const needIso2  = (showBirthCountryFlag && !birthCountryIso2Cache.has(id));
          const needPlace = (showBirthCountryFlag && showBirthPlaceText && !birthPlaceCache.has(id));
          const needDec   = (showDeceasedOverlay && !deceasedCache.has(id));

          if (needAge || needBirth || needIso2 || needPlace || needDec) {
            missing.push(id);
          }
        }

        for (const id of ids) {
          if (ageCache.has(id) || birthDateCache.has(id) || birthCountryIso2Cache.has(id) || birthPlaceCache.has(id) || deceasedCache.has(id)) deliver(id);
        }

        // If the plugin returned no data for some ids, "touch" those Person items via Jellyfin's standard API,
        // then re-query the plugin once for just the missing subset.
        if (missing.length) {
          await touchMissingPersons(missing);
          try {
            const resp2 = await ApiClient.ajax({
              type: 'POST',
              url: ApiClient.getUrl(API_BATCH),
              dataType: 'json',
              contentType: 'application/json',
              data: JSON.stringify({ personIds: missing })
            });

            const byId2 = Object.create(null);
            if (resp2 && typeof resp2 === 'object') {
              for (const k of Object.keys(resp2)) byId2[normalizeId(k)] = resp2[k];
            }

            for (const id of missing) {
              const rec = byId2[id];
              const birth2 = rec ? (rec.BirthDate ?? rec.birthDate) : null;
              if (birth2) birthDateCache.set(id, String(birth2).trim());
              const iso22 = rec ? (rec.BirthCountryIso2 ?? rec.birthCountryIso2) : null;
              if (iso22) birthCountryIso2Cache.set(id, String(iso22).trim());

              const place2 = rec ? (rec.BirthPlace ?? rec.birthPlace) : null;
              if (place2) birthPlaceCache.set(id, String(place2).trim());

              const decRaw2 = rec ? (rec.IsDeceased ?? rec.isDeceased) : null;
              if (decRaw2 !== null && decRaw2 !== undefined) {
                deceasedCache.set(id, !!decRaw2);
              }
              const ageTextRaw = rec ? (rec.AgeText ?? rec.ageText ?? rec.AgeYears ?? rec.ageYears) : null;
              const n = (ageTextRaw != null) ? String(ageTextRaw).trim() : '';
              const text = n ? (n + ' y') : null;
              if (text) ageCache.set(id, text);
              if (ageCache.has(id) || birthDateCache.has(id) || birthCountryIso2Cache.has(id) || birthPlaceCache.has(id) || deceasedCache.has(id)) deliver(id);
            }
          } catch {
            // ignore
          }
        }
      } catch {
        // transient error - retry later (no negative caching)
      }
    }

    function extractIdFromUrlString(url) {
      if (!url) return null;
      const m = String(url).match(/(?:\?|&)id=([0-9a-fA-F-]{32,36})/i);
      return m ? normalizeId(m[1]) : null;
    }

    function scan(root) {
      // Safety: ensure context matches current route even if navigation event wasn't caught yet.
      try {
        const rid = getCurrentRouteItemId();
        if (rid && contextItemId !== rid) {
          contextItemId = rid;
          contextPremiereUtc = null;
          contextIsPerson = false;
          scheduleContextRefresh();
        }
      } catch {}
      if (!root || typeof root.querySelectorAll !== 'function') return;

      // Cards/grids
      root.querySelectorAll(TARGET_SELECTORS).forEach(el => {
        if (!el || !(el instanceof Element)) return;

        // Filter: person cards only (anchors)
        // Filter: only process people cards (avoid triggering requests for non-person posters).
    if (el.tagName === 'A') {
      if (!isPersonCardAnchor(el)) return;
    } else {
      // For non-anchor elements (e.g. .listItemImage), only process inside cast/people sections.
      if (!el.closest('#castContent, #cast, .castContent, .cast, .peopleSection, .detailsCast, .itemDetailsCast')) return;
    }

        const id = extractItemId(el);
        if (!id) return;

        registerWaiter(id, el);
        queueFetch(id);
      });

      // Person details page: hash routing (#/details?id=...)
      const hashId = extractIdFromUrlString(window.location.hash) || extractIdFromUrlString(window.location.href);
      if (hashId) {
        const detail = document.querySelector('.detailImageContainer, .detailImage, .detailPrimaryImageContainer') || null;
        if (detail) {
          registerWaiter(hashId, detail);
          queueFetch(hashId);
        }
      }
    }

    function scheduleScan(root, force) {
      if (force) scanScheduled = false;
      if (scanScheduled) return;
      scanScheduled = true;

      const run = () => {
        scanScheduled = false;
        if (document.hidden) return;
        try { scan(root || document); } catch {}
      };

      // Prefer idle time; fallback to timeout
      if (typeof window.requestIdleCallback === 'function') {
        window.requestIdleCallback(run, { timeout: 600 });
      } else {
        setTimeout(run, 0);
      }
    }

// ===== Hover filmography popup =====
const filmographyCache = new Map(); // personId -> { items: [], ts: number }
const filmographyInFlight = new Map(); // personId -> Promise
const FILMOGRAPHY_TTL_MS = 10 * 60 * 1000; // 10 minutes
const filmographyTotalCache = new Map(); // personId -> { total: number, ts: number }
const FILMOGRAPHY_TOTAL_TTL_MS = 6 * 60 * 60 * 1000; // 6 hours

let filmPopup = null;
let filmPopupHideTimer = null;
let filmPopupShowTimer = null;
let hoverTargetEl = null;

function ensureFilmPopup() {
  if (filmPopup) return filmPopup;
  filmPopup = document.createElement('div');
  filmPopup.className = 'birthage-filmography-popup';
  filmPopup.style.display = 'none';
  filmPopup.addEventListener('pointerenter', () => {
    if (filmPopupHideTimer) { clearTimeout(filmPopupHideTimer); filmPopupHideTimer = null; }
  });
  filmPopup.addEventListener('pointerleave', () => {
    scheduleHideFilmPopup(200);
  });
  document.body.appendChild(filmPopup);
  return filmPopup;
}

function scheduleHideFilmPopup(ms) {
  if (filmPopupShowTimer) { clearTimeout(filmPopupShowTimer); filmPopupShowTimer = null; }
  if (filmPopupHideTimer) clearTimeout(filmPopupHideTimer);
  filmPopupHideTimer = setTimeout(() => hideFilmPopup(), ms);
}

function hideFilmPopup() {
  if (!filmPopup) return;
  filmPopup.style.display = 'none';
  filmPopup.innerHTML = '';
  hoverTargetEl = null;
}

function positionFilmPopup(anchorEl) {
  if (!filmPopup || !anchorEl) return;
  const r = anchorEl.getBoundingClientRect();
  const pad = 10;
  const width = Math.min(420, Math.max(260, Math.floor(window.innerWidth * 0.30)));
  filmPopup.style.width = width + 'px';

  // Default: to the right, aligned to top
  let left = r.right + pad;
  let top = r.top;

  // If doesn't fit right, place left
  if (left + width + pad > window.innerWidth) {
    left = Math.max(pad, r.left - width - pad);
  }

  // Clamp vertically
  const maxH = Math.min(360, Math.floor(window.innerHeight * 0.55));
  filmPopup.style.maxHeight = maxH + 'px';
  if (top + maxH + pad > window.innerHeight) {
    top = Math.max(pad, window.innerHeight - maxH - pad);
  }

  filmPopup.style.left = Math.round(left) + 'px';
  filmPopup.style.top  = Math.round(top) + 'px';
}

function formatFilmItem(it) {
  const name = it.Name || it.name || '—';
  const year = it.ProductionYear || it.productionYear;
  const type = (it.Type || it.type || '').toLowerCase();
  const icon = type === 'series' ? '📺' : '🎞️';
  const y = year ? ` (${year})` : '';
  return `${icon} ${name}${y}`;
}

async function fetchFilmographyTotal(pid) {
  const cached = filmographyTotalCache.get(pid);
  if (cached && (Date.now() - cached.ts) < FILMOGRAPHY_TOTAL_TTL_MS) {
    return cached.total || 0;
  }

  // minimal query: ask for 1 item but request total count
  return (async () => {
    const userId = getUserIdSafe();
    if (!userId) return 0;

    const q = {
      PersonIds: pid,
      IncludeItemTypes: 'Movie,Series',
      Recursive: true,
      Limit: '1',
      StartIndex: '0',
      EnableTotalRecordCount: 'true'
    };

    const json = await ApiClient.ajax({
      type: 'GET',
      url: ApiClient.getUrl('Users/' + userId + '/Items', q),
      dataType: 'json'
    });

    const total = (json && (json.TotalRecordCount || json.totalRecordCount))
      ? (json.TotalRecordCount || json.totalRecordCount)
      : 0;

    filmographyTotalCache.set(pid, { total: total, ts: Date.now() });
    return total || 0;
  })().catch(() => 0);
}

function fetchFilmography(personId, limit, randomize) {
  const pid = normalizeId(personId);
  if (!pid) return { items: [], total: 0 };

  // Random mode: normally show a fresh sample on each hover.
// Но если всё помещается в лимит, делаем список стабильным (и можем использовать кэш).
const cached = filmographyCache.get(pid);
if (cached && (Date.now() - cached.ts) < FILMOGRAPHY_TTL_MS) {
  const cachedItems = cached.items || [];
  const cachedTotal = cached.total || cachedItems.length || 0;
  const lim0 = (limit || 12);
  if (!randomize || (cachedTotal > 0 && cachedTotal <= lim0)) {
    return { items: cachedItems, total: cachedTotal };
  }
}

  // Coalesce in-flight requests (also in random mode) to avoid duplicate calls.
  const inflightKey = pid + '|' + (randomize ? 'rnd' : 'norm') + '|' + String(limit || 0);
  if (filmographyInFlight.has(inflightKey)) return filmographyInFlight.get(inflightKey);

  const p = (async () => {
    const userId = getUserIdSafe();
    if (!userId) return { items: [], total: 0 };

        let doRandom = !!randomize;
    const lim = (limit || 12);
    if (doRandom) {
      const totalCount = await fetchFilmographyTotal(pid);
      if (totalCount > 0 && totalCount <= lim) {
        // If everything fits into the limit, keep a stable order (not random).
        doRandom = false;
      }
    }

    const q = {
      PersonIds: pid,
      IncludeItemTypes: 'Movie,Series',
      Recursive: true,
      Limit: String(limit || 12),
      Fields: 'ProductionYear',
      EnableTotalRecordCount: 'true'
    };

    if (doRandom) {
      // Jellyfin поддерживает сортировку Random для выборок.
      q.SortBy = 'Random';
    } else {
      q.SortBy = 'PremiereDate,SortName';
      q.SortOrder = 'Descending';
    }

    const json = await ApiClient.ajax({
      type: 'GET',
      url: ApiClient.getUrl('Users/' + userId + '/Items', q),
      dataType: 'json'
    });

    const items = (json && (json.Items || json.items)) ? (json.Items || json.items) : [];
    const clean = Array.isArray(items) ? items.filter(x => x && (x.Id || x.id) && (x.Name || x.name)) : [];
    const total = (json && (json.TotalRecordCount || json.totalRecordCount)) ? (json.TotalRecordCount || json.totalRecordCount) : (clean.length || 0);

    if (!doRandom) {
      filmographyCache.set(pid, { items: clean, total: total, ts: Date.now() });
    }

    return { items: clean, total: total };
  })().catch(() => ({ items: [], total: 0 }))
    .finally(() => filmographyInFlight.delete(inflightKey));

  filmographyInFlight.set(inflightKey, p);
  return p;
}

function getPrimaryImageUrl(itemId, width, height) {
  const id = (itemId || '').toString();
  if (!id) return null;
  const q = {
    fillWidth: String(width || 72),
    fillHeight: String(height || 108),
    quality: '90'
  };
  try {
    return ApiClient.getUrl('Items/' + id + '/Images/Primary', q);
  } catch {
    return null;
  }
}

function renderFilmPopup(anchorEl, personId, personName, items, total, useRandom, truncated) {
  const pop = ensureFilmPopup();

  const header = document.createElement('div');
  header.className = 'birthage-filmography-header';

  const title = document.createElement('div');
  title.className = 'birthage-filmography-title';
  title.textContent = personName ? `Filmography: ${personName}` : 'Filmography';

  const meta = document.createElement('div');
  meta.className = 'birthage-filmography-metaheader';
  if (total && total > 0) {
    const shown = (items && items.length) ? items.length : 0;
    const rnd = useRandom ? ' • random' : '';
    const trunc = truncated ? ' • limited' : '';
    meta.textContent = `Shown ${shown} of ${total}${rnd}${trunc}`;
  } else {
    meta.textContent = '';
  }

  header.appendChild(title);
  header.appendChild(meta);

  const list = document.createElement('div');
  list.className = 'birthage-filmography-list';

  if (!items || items.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'birthage-filmography-empty';
    empty.textContent = 'Nothing found in the library.';
    list.appendChild(empty);
  } else {
    for (const it of items) {
      const id = (it.Id || it.id || '').toString();
      const name = it.Name || it.name || '—';
      const year = it.ProductionYear || it.productionYear;
      const type = (it.Type || it.type || '').toLowerCase();
      const typeRu = (type === 'series') ? 'Series' : 'Movie';
      const subtitle = year ? `${typeRu} • ${year}` : typeRu;

      const a = document.createElement('a');
      a.className = 'birthage-filmography-item';
      a.href = '#/details?id=' + encodeURIComponent(id);

      const thumb = document.createElement('div');
      thumb.className = 'birthage-filmography-thumb';

      const url = getPrimaryImageUrl(id, 72, 108);
      if (url) {
        const img = document.createElement('img');
        img.loading = 'lazy';
        img.decoding = 'async';
        img.alt = name;
        img.src = url;
        img.addEventListener('error', () => {
          // Fallback: show placeholder
          thumb.classList.add('birthage-thumb-missing');
          try { img.remove(); } catch { /* ignore */ }
        });
        thumb.appendChild(img);
      } else {
        thumb.classList.add('birthage-thumb-missing');
      }

      const body = document.createElement('div');
      body.className = 'birthage-filmography-body';

      const t = document.createElement('div');
      t.className = 'birthage-filmography-name';
      t.textContent = name;

      const s = document.createElement('div');
      s.className = 'birthage-filmography-sub';
      s.textContent = subtitle;

      body.appendChild(t);
      body.appendChild(s);

      a.appendChild(thumb);
      a.appendChild(body);

      a.addEventListener('click', () => hideFilmPopup());
      list.appendChild(a);
    }
  }

  pop.innerHTML = '';
  pop.appendChild(header);
  pop.appendChild(list);

  positionFilmPopup(anchorEl);
  pop.style.display = 'block';
}

async function showFilmographyForEl(el) {
  if (!el) return;
  await loadStatus();
  if (!enabled || !enableHoverFilmography) return;

  const anchor = el.tagName === 'A' ? el : (el.closest && el.closest('a.cardImageContainer')) || el;
  if (!anchor) return;

  const pid = extractItemId(anchor);
  if (!pid) return;
  if (anchor.tagName === 'A' && !isPersonCardAnchor(anchor)) return;

  // Cancel hide
  if (filmPopupHideTimer) { clearTimeout(filmPopupHideTimer); filmPopupHideTimer = null; }

  const name = anchor.getAttribute('aria-label') || '';
  const pop = ensureFilmPopup();
  pop.innerHTML = '<div class="birthage-filmography-header"><div class="birthage-filmography-title">Filmography</div><div class="birthage-filmography-metaheader"></div></div><div class="birthage-filmography-loading">Loading…</div>';
  positionFilmPopup(anchor);
  pop.style.display = 'block';
  hoverTargetEl = anchor;

  try {
    const useRandom = !!randomizeHoverFilmography;
    const data = await fetchFilmography(pid, hoverFilmographyLimit, useRandom);
    const items = (data && data.items) ? data.items : [];
    const total = (data && data.total) ? data.total : items.length;

    // If hover moved away, don't overwrite
    if (hoverTargetEl !== anchor) return;
    renderFilmPopup(anchor, pid, name, items, total, useRandom, false);
  } catch {
    if (hoverTargetEl !== anchor) return;
    renderFilmPopup(anchor, pid, name, [], 0, false, false);
  }
}

function setupHoverFilmography() {
  // Use event delegation (virtualized lists reuse nodes).
  document.addEventListener('pointerover', function (e) {
    if (!enableHoverFilmography) return;
    const t = e.target;
    if (!t || !t.closest) return;
    const anchor = t.closest('a.cardImageContainer, a.cardImageContainer-withZoom');
    if (!anchor) return;
    if (!isPersonCardAnchor(anchor)) return;

    // entering new target
    if (hoverTargetEl === anchor) return;
    hoverTargetEl = anchor;

    if (filmPopupHideTimer) { clearTimeout(filmPopupHideTimer); filmPopupHideTimer = null; }
    if (filmPopupShowTimer) clearTimeout(filmPopupShowTimer);
    filmPopupShowTimer = setTimeout(() => {
      showFilmographyForEl(anchor);
    }, 220);
  }, true);

  document.addEventListener('pointerout', function (e) {
    if (!filmPopup) return;
    const t = e.target;
    if (!t || !t.closest) return;
    const anchor = t.closest('a.cardImageContainer, a.cardImageContainer-withZoom');
    if (!anchor) return;
    if (hoverTargetEl !== anchor) return;

    const related = e.relatedTarget;
    if (related && filmPopup && filmPopup.contains(related)) return;

    scheduleHideFilmPopup(200);
  }, true);
}
    

// ===== Hover cast menu popup (poster hover) =====
const castCache = new Map();      // itemId -> { title: string, people: [], ts: number }
const castInFlight = new Map();   // itemId|fields -> Promise
const CAST_TTL_MS = 10 * 60 * 1000; // 10 minutes

let castPopup = null;
let castPopupHideTimer = null;
let castPopupShowTimer = null;
let castHoverTargetEl = null;

function ensureCastPopup() {
  if (castPopup) return castPopup;
  castPopup = document.createElement('div');
  castPopup.className = 'birthage-cast-popup';
  castPopup.style.display = 'none';
  castPopup.addEventListener('pointerenter', () => {
    if (castPopupHideTimer) { clearTimeout(castPopupHideTimer); castPopupHideTimer = null; }
  });
  castPopup.addEventListener('pointerleave', () => {
    scheduleHideCastPopup(200);
  });
  document.body.appendChild(castPopup);
  return castPopup;
}

function scheduleHideCastPopup(ms) {
  if (castPopupShowTimer) { clearTimeout(castPopupShowTimer); castPopupShowTimer = null; }
  if (castPopupHideTimer) clearTimeout(castPopupHideTimer);
  castPopupHideTimer = setTimeout(() => hideCastPopup(), ms);
}

function hideCastPopup() {
  if (!castPopup) return;
  castPopup.style.display = 'none';
  castPopup.innerHTML = '';
  castHoverTargetEl = null;
}

function positionCastPopup(anchorEl) {
  if (!castPopup || !anchorEl) return;
  const r = anchorEl.getBoundingClientRect();
  const pad = 10;
  const width = Math.min(520, Math.max(260, Math.floor(window.innerWidth * 0.34)));
  castPopup.style.width = width + 'px';

  // Default: to the right, aligned to top
  let left = r.right + pad;
  let top = r.top;

  // If doesn't fit right, place left
  if (left + width + pad > window.innerWidth) {
    left = Math.max(pad, r.left - width - pad);
  }

  // Clamp vertically
  const maxH = Math.min(420, Math.floor(window.innerHeight * 0.60));
  castPopup.style.maxHeight = maxH + 'px';
  if (top + maxH + pad > window.innerHeight) {
    top = Math.max(pad, window.innerHeight - maxH - pad);
  }

  castPopup.style.left = Math.round(left) + 'px';
  castPopup.style.top  = Math.round(top) + 'px';
}

function normalizePeopleArray(arr) {
  if (!arr) return [];
  if (Array.isArray(arr)) return arr;
  return [];
}

function extractCastPeople(itemJson) {
  const people = normalizePeopleArray(itemJson?.People ?? itemJson?.people);
  if (!people.length) return [];

  // Prefer actors/guest stars
  const actors = people.filter(p => {
    const t = (p.Type ?? p.type ?? '').toString().toLowerCase();
    return t === 'actor' || t === 'gueststar' || t === 'guest star' || t === 'guest_star';
  });

  const use = actors.length ? actors : people;
  // Sort by SortOrder then name
  use.sort((a, b) => {
    const ao = (a.SortOrder ?? a.sortOrder ?? 9999);
    const bo = (b.SortOrder ?? b.sortOrder ?? 9999);
    if (ao !== bo) return ao - bo;
    const an = (a.Name ?? a.name ?? '').toString();
    const bn = (b.Name ?? b.name ?? '').toString();
    return an.localeCompare(bn);
  });

  return use;
}

async function fetchCastForItem(itemId) {
  const id = (itemId || '').toString();
  if (!id) return { title: '', people: [] };

  const cached = castCache.get(id);
  if (cached && (Date.now() - cached.ts) < CAST_TTL_MS) {
    return { title: cached.title || '', people: cached.people || [] };
  }

  const inflightKey = id;
  if (castInFlight.has(inflightKey)) return castInFlight.get(inflightKey);

  const p = (async () => {
    const userId = getUserIdSafe();
    if (!userId) return { title: '', people: [] };

    // Fetch item with People field
    const url = ApiClient.getUrl('Users/' + userId + '/Items/' + id, { Fields: 'People' });
    const item = await ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' });

    const title = (item?.Name ?? item?.name ?? '') || '';
    const type = (item?.Type ?? item?.type ?? '').toString().toLowerCase();

    // Limit to movie/series only (as requested). If not, still show if people exist.
    const people = extractCastPeople(item);

    castCache.set(id, { title, people, ts: Date.now(), type });

    return { title, people, type };
  })().catch(() => ({ title: '', people: [], type: '' })).finally(() => {
    try { castInFlight.delete(inflightKey); } catch { /* ignore */ }
  });

  castInFlight.set(inflightKey, p);
  return p;
}

function renderCastPopup(anchorEl, itemTitle, people, limit) {
  const pop = ensureCastPopup();

  const header = document.createElement('div');
  header.className = 'birthage-cast-header';

  const title = document.createElement('div');
  title.className = 'birthage-cast-title';
  title.textContent = itemTitle ? `Cast: ${itemTitle}` : 'Cast';

  const meta = document.createElement('div');
  meta.className = 'birthage-cast-metaheader';
  const total = people ? people.length : 0;
  const lim = Math.max(1, Math.min(100, parseInt(limit, 10) || 12));
  const shown = Math.min(total, lim);
  meta.textContent = total ? `Shown ${shown} of ${total}` : '';

  header.appendChild(title);
  header.appendChild(meta);

  const list = document.createElement('div');
  list.className = 'birthage-cast-list';

  const slice = (people || []).slice(0, Math.max(1, Math.min(100, parseInt(limit, 10) || 12)));

  if (!slice.length) {
    const empty = document.createElement('div');
    empty.className = 'birthage-cast-empty';
    empty.textContent = 'No cast data.';
    list.appendChild(empty);
  } else {
    for (const p of slice) {
      const pid = (p.Id ?? p.id ?? '').toString();
      const name = (p.Name ?? p.name ?? '—').toString();
      const role = (p.Role ?? p.role ?? '').toString();

      const a = document.createElement('a');
      a.className = 'birthage-cast-item';
      if (pid) a.href = '#/details?id=' + encodeURIComponent(pid);

      const thumb = document.createElement('div');
      thumb.className = 'birthage-cast-thumb';

      if (pid) {
        const imgUrl = getPrimaryImageUrl(pid, 88, 132);
        if (imgUrl) {
          const img = document.createElement('img');
          img.loading = 'lazy';
          img.decoding = 'async';
          img.alt = name;
          img.src = imgUrl;
          img.addEventListener('error', () => {
            thumb.classList.add('birthage-thumb-missing');
            try { img.remove(); } catch {}
          });
          thumb.appendChild(img);
        } else {
          thumb.classList.add('birthage-thumb-missing');
        }
      } else {
        thumb.classList.add('birthage-thumb-missing');
      }

      const body = document.createElement('div');
      body.className = 'birthage-cast-body';

      const t = document.createElement('div');
      t.className = 'birthage-cast-name';

      const nameRow = document.createElement('div');
      nameRow.className = 'birthage-cast-name-row';

      const nameSpan = document.createElement('span');
      nameSpan.className = 'birthage-cast-name-text';
      nameSpan.textContent = name;
      nameRow.appendChild(nameSpan);

      // Birth country flag (twemoji) to the right of the actor name
      const nid = normalizeId(pid);
      const iso2 = birthCountryIso2Cache.get(nid);
      if (iso2) {
        const flagUrl = iso2ToTwemojiUrl(iso2);
        if (flagUrl) {
          const imgFlag = document.createElement('img');
          imgFlag.className = 'birthage-cast-flag';
          imgFlag.alt = iso2;
          imgFlag.src = flagUrl;
          const place = birthPlaceCache.get(nid);
          imgFlag.title = place ? place : iso2;
          nameRow.appendChild(imgFlag);
        }
      }

      t.appendChild(nameRow);
const s = document.createElement('div');
      s.className = 'birthage-cast-sub';
      s.textContent = role ? role : '';

      body.appendChild(t);
      if (role) body.appendChild(s);

      a.appendChild(thumb);
      a.appendChild(body);

      a.addEventListener('click', () => hideCastPopup());
      list.appendChild(a);
    }
  }

  pop.innerHTML = '';
  pop.appendChild(header);
  pop.appendChild(list);

  positionCastPopup(anchorEl);
  pop.style.display = 'block';
}

async function showCastForEl(anchor) {
  if (!anchor) return;
  await loadStatus();
  if (!enabled || !enableHoverCastMenu) return;

  // Only show on item *details* cards. This prevents popups when hovering media libraries (views).
  try {
    const href = (anchor.getAttribute && (anchor.getAttribute('href') || '')) || '';
    if (!/#\/details\?id=/i.test(href)) return;
  } catch { /* ignore */ }

  // Cancel hide
  if (castPopupHideTimer) { clearTimeout(castPopupHideTimer); castPopupHideTimer = null; }

  const itemId = extractItemId(anchor);
  if (!itemId) return;

  const pop = ensureCastPopup();
  pop.innerHTML = '<div class="birthage-cast-header"><div class="birthage-cast-title">Cast</div><div class="birthage-cast-metaheader"></div></div><div class="birthage-cast-loading">Loading…</div>';
  positionCastPopup(anchor);
  pop.style.display = 'block';
  castHoverTargetEl = anchor;

  try {
    const data = await fetchCastForItem(itemId);
    const people = data?.people || [];
    // Pre-fetch birth country info for cast list so we can show flags next to actor names
    try {
      const lim = Math.max(1, Math.min(100, parseInt(hoverCastLimit, 10) || 12));
      const slice = (people || []).slice(0, lim);
      const ids = slice.map(p => normalizeId((p.Id ?? p.id ?? '').toString())).filter(Boolean);
      if (ids.length) {
        const resp = await ApiClient.ajax({
          type: 'POST',
          url: ApiClient.getUrl(API_BATCH),
          dataType: 'json',
          contentType: 'application/json',
          data: JSON.stringify({ personIds: ids })
        });

        const byId = Object.create(null);
        if (resp && typeof resp === 'object') {
          for (const k of Object.keys(resp)) byId[normalizeId(k)] = resp[k];
        }

        for (const id of ids) {
          const rec = byId[id];
          if (!rec) continue;
          const iso2 = rec ? (rec.BirthCountryIso2 ?? rec.birthCountryIso2) : null;
          if (iso2) birthCountryIso2Cache.set(id, String(iso2).trim());
          const place = rec ? (rec.BirthPlace ?? rec.birthPlace) : null;
          if (place) birthPlaceCache.set(id, String(place).trim());
        }
      }
    } catch {
      // ignore
    }
    const title = data?.title || (anchor.getAttribute('aria-label') || '');
    if (castHoverTargetEl !== anchor) return;
    renderCastPopup(anchor, title, people, hoverCastLimit);
  } catch {
    if (castHoverTargetEl !== anchor) return;
    renderCastPopup(anchor, '', [], hoverCastLimit);
  }
}

function setupHoverCastMenu() {
  document.addEventListener('pointerover', function (e) {
    if (!enableHoverCastMenu) return;
    const t = e.target;
    if (!t || !t.closest) return;

    const anchor = t.closest('a.cardImageContainer, a.cardImageContainer-withZoom');
    if (!anchor) return;

    // Only show on item *details* cards (not on media libraries / views).
    try {
      const href = (anchor.getAttribute && (anchor.getAttribute('href') || '')) || '';
      if (!/#\/details\?id=/i.test(href)) return;
    } catch { /* ignore */ }

    // Do not conflict with person-hover filmography
    if (isPersonCardAnchor(anchor)) return;

    const id = extractItemId(anchor);
    if (!id) return;

    // entering new target
    if (castHoverTargetEl === anchor) return;
    castHoverTargetEl = anchor;

    if (castPopupHideTimer) { clearTimeout(castPopupHideTimer); castPopupHideTimer = null; }
    if (castPopupShowTimer) clearTimeout(castPopupShowTimer);
    castPopupShowTimer = setTimeout(() => {
      showCastForEl(anchor);
    }, 240);
  }, true);

  document.addEventListener('pointerout', function (e) {
    if (!castPopup) return;
    const t = e.target;
    if (!t || !t.closest) return;

    const anchor = t.closest('a.cardImageContainer, a.cardImageContainer-withZoom');
    if (!anchor) return;

    if (castHoverTargetEl !== anchor) return;

    const related = e.relatedTarget;
    if (related && castPopup && castPopup.contains(related)) return;

    scheduleHideCastPopup(200);
  }, true);
}


async function init() {
      const ok = await loadStatus();
      if (!ok) return;

      // Determine current details-page context date (premiere date) for "age at release" badges.
      resetContextForRoute();
      scheduleContextRefresh();

      // Initial scan
      scheduleScan(document, true);

      // Observe additions only (safe)
      const mo = new MutationObserver(mutations => {
        for (const m of mutations) {
          if (!m.addedNodes || !m.addedNodes.length) continue;
          m.addedNodes.forEach(n => {
            if (n && n.nodeType === 1) scheduleScan(n);
          });
        }
      });
      mo.observe(document.documentElement || document.body, { childList: true, subtree: true });

      // Periodic scan to handle virtualized / recycled lists (safe approach)
      setInterval(() => scheduleScan(document), PERIODIC_SCAN_MS);

      // Scroll-debounced rescan (quick feedback)
      window.addEventListener('scroll', () => {
        if (scrollTimer) clearTimeout(scrollTimer);
        scrollTimer = setTimeout(() => scheduleScan(document), SCROLL_DEBOUNCE_MS);
      }, { passive: true });

            // ===== SPA navigation handling (based on multi_tag.js) =====
      let lastUrl = window.location.href;

      function onUrlChange() {
        const cur = window.location.href;
        if (cur === lastUrl) return;
        lastUrl = cur;

        resetContextForRoute();
        // Try a few times because details payload can arrive slightly позже DOM.
        scheduleContextRefresh();
        setTimeout(scheduleContextRefresh, 350);
        setTimeout(scheduleContextRefresh, 1200);

        // Force a couple of passes to repaint badges after layout settles.
        scheduleScan(document, true);
        setTimeout(() => scheduleScan(document, true), 400);
      }

      // Hook history API
      (function hookHistory() {
        try {
          const _push = history.pushState;
          const _replace = history.replaceState;
          history.pushState = function () { const r = _push.apply(this, arguments); onUrlChange(); return r; };
          history.replaceState = function () { const r = _replace.apply(this, arguments); onUrlChange(); return r; };
          window.addEventListener('popstate', onUrlChange, { passive: true });
        } catch { /* ignore */ }
      })();

      // hashchange still useful on some builds
      window.addEventListener('hashchange', onUrlChange, { passive: true });

      // Safety net: detect URL changes not covered by hooks
      setInterval(() => {
        if (window.location.href !== lastUrl) onUrlChange();
      }, 700);

      setupHoverFilmography();
      setupHoverCastMenu();

      window.addEventListener('resize', () => scheduleScan(document), { passive: true });
    }

    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', init);
    } else {
      init();
    }
  }
})();
