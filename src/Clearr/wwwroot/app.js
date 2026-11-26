(function () {
    'use strict';

    var $ = function (s, root) { return (root || document).querySelector(s); };
    var $$ = function (s, root) { return Array.prototype.slice.call((root || document).querySelectorAll(s)); };
    var state = { status: null, report: null, page: 'shows', open: {} };

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function fmtSize(b) { if (!b) return '0 GB'; var gb = b / 1073741824; return gb >= 1 ? gb.toFixed(gb >= 100 ? 0 : 1) + ' GB' : Math.max(1, Math.round(b / 1048576)) + ' MB'; }
    function fmtDate(d) { if (!d) return ''; var x = new Date(d); return isNaN(x) ? '' : x.toLocaleString(); }

    function api(path, method, body) {
        return fetch('api/' + path, {
            method: method || 'GET',
            credentials: 'same-origin',
            headers: body !== undefined ? { 'Content-Type': 'application/json' } : {},
            body: body !== undefined ? JSON.stringify(body) : undefined
        }).then(function (r) {
            if (r.status === 401 && path !== 'login') { showLogin(); throw new Error('Sign in required'); }
            return r.json().catch(function () { return {}; }).then(function (j) {
                if (!r.ok) { var e = new Error(j.error || ('HTTP ' + r.status)); e.body = j; throw e; }
                return j;
            });
        });
    }

    // ------------------------------------------------------------ boot / auth
    function showLogin() { $('#app').classList.add('hidden'); $('#login').classList.remove('hidden'); $('#loginPassword').focus(); }

    $('#loginForm').addEventListener('submit', function (e) {
        e.preventDefault();
        api('login', 'POST', { password: $('#loginPassword').value }).then(function () {
            $('#loginPassword').value = ''; $('#loginError').textContent = ''; boot();
        }).catch(function (err) { $('#loginError').textContent = err.message; });
    });

    $('#logout').addEventListener('click', function (e) { e.preventDefault(); api('logout', 'POST').then(function () { location.reload(); }); });

    function boot() {
        api('status').then(function (s) {
            state.status = s;
            if (s.passwordRequired && !s.authenticated) { showLogin(); return; }
            $('#login').classList.add('hidden');
            $('#app').classList.remove('hidden');
            $('#logout').classList.toggle('hidden', !s.passwordRequired);
            renderStatus();
            route();
        });
    }

    function renderStatus() {
        var s = state.status;
        $('#version').textContent = s.version;
        var label = { Off: 'Off', DryRun: 'Dry run', Delete: 'Delete mode' }[s.mode] || s.mode;
        $('#statusMode').textContent = label;
        $('#statusMode').className = 'mode-badge mode-' + s.mode;
        $('#statusNext').textContent = s.mode === 'Off' ? 'Daily run off' : 'Next run ' + fmtDate(s.nextRun);
        $('#statusLast').textContent = s.lastRunSummary ? 'Last: ' + s.lastRunSummary : '';
        var banner = $('#banner');
        if (!s.configured) {
            banner.innerHTML = 'Welcome to Clearr. Start by connecting <b>Jellyfin</b> (and Sonarr/Radarr) under <a href="#settings">Settings</a>.';
            banner.classList.remove('hidden');
        } else banner.classList.add('hidden');
        $('#deleteNow').classList.toggle('hidden', s.mode !== 'Delete');
        $('#modeNote').textContent = s.mode === 'Delete'
            ? 'Delete mode: "Delete eligible now" removes files right after you confirm the list.'
            : 'Cleanup mode is ' + label + ', so nothing can be removed. "Preview cleanup" shows what would go. Use Keep to protect anything.';
    }

    // ------------------------------------------------------------ routing
    function route() {
        var page = (location.hash || '#shows').slice(1);
        if (['shows', 'movies', 'history', 'settings'].indexOf(page) < 0) page = 'shows';
        state.page = page;
        $$('.nav-link').forEach(function (a) { a.classList.toggle('active', a.getAttribute('data-page') === page); });
        $$('.page').forEach(function (p) { p.classList.toggle('hidden', p.getAttribute('data-section').split(' ').indexOf(page) < 0); });
        if (page === 'shows' || page === 'movies') {
            $('#libTitle').textContent = page === 'shows' ? 'Shows' : 'Movies';
            $('#result').classList.add('hidden');
            if (state.report) renderLibrary(); else loadReport();
        }
        if (page === 'history') loadHistory();
        if (page === 'settings') loadSettings();
    }
    window.addEventListener('hashchange', route);

    // ------------------------------------------------------------ library
    function loadReport() {
        if (!state.status.configured) { $('#summary').textContent = ''; $('#list').innerHTML = ''; return Promise.resolve(); }
        $('#summary').textContent = 'Loading from Jellyfin…';
        return api('report').then(function (r) { state.report = r; renderLibrary(); })
            .catch(function (e) { $('#summary').textContent = ''; $('#list').innerHTML = '<div class="warn">' + esc(e.message) + '</div>'; });
    }

    function flagLabel(f) { return f === 'MidEpisode' ? (state.page === 'movies' ? 'mid-movie' : 'mid-episode') : f.toLowerCase(); }

    function chip(u, counts) {
        var cls = 'chip st-' + u.status + (u.flags.length ? ' flagged' : '');
        var t = esc(u.userName);
        if (u.status === 'Finished') t += ' ✓';
        else if (u.status === 'InProgress' && counts) t += ' ' + u.watched + '/' + u.total;
        if (u.flags.length) t += ' · ' + u.flags.map(flagLabel).join(', ');
        var title = u.status + (u.lastEpisode ? ' — last ' + u.lastEpisode : '') + (u.lastActivity ? ' on ' + fmtDate(u.lastActivity) : '');
        return '<span class="' + cls + '" title="' + esc(title) + '">' + t + '</span>';
    }

    function chips(users, counts) {
        var c = users.filter(function (u) { return u.status !== 'NotStarted'; }).map(function (u) { return chip(u, counts); }).join('');
        return c || '<span class="muted small">nobody started</span>';
    }

    function keepBtn(id, kind, kept, keptBy) {
        var external = kept && keptBy && keptBy !== 'Clearr' && keptBy !== 'Sonarr tag' && keptBy !== 'Radarr tag';
        return '<button class="keep' + (kept ? ' on' : '') + '" data-id="' + esc(id) + '" data-kind="' + kind + '" data-keep="' + (!kept) + '"' +
            (external ? ' disabled title="Kept by a ' + esc(keptBy) + '; remove it there"' : ' title="' + (kept ? 'Kept (' + esc(keptBy) + '). Click to remove.' : 'Never remove this') + '"') +
            '>' + (kept ? 'Kept ✓' : 'Keep') + '</button>';
    }

    function matches(item, users, eligibleAny, flaggedAny, filter, q) {
        if (q && item.name.toLowerCase().indexOf(q) < 0) return false;
        var started = users.some(function (u) { return u.status !== 'NotStarted'; });
        switch (filter) {
            case 'ready': return eligibleAny;
            case 'flagged': return flaggedAny;
            case 'watching': return users.some(function (u) { return u.status === 'InProgress' && !u.flags.length; });
            case 'untouched': return !started;
            case 'kept': return item.kept || (item.seasons || []).some(function (x) { return x.kept; });
            case 'unmanaged': return !item.managed;
            default: return true;
        }
    }

    function renderLibrary() {
        var r = state.report, shows = state.page === 'shows';
        var filter = $('#filter').value, q = $('#search').value.trim().toLowerCase();
        $('#filter').options[6].textContent = shows ? 'Not in Sonarr' : 'Not in Radarr';

        $('#warnings').innerHTML = r.warnings.map(function (w) { return '<div class="warn">' + esc(w) + '</div>'; }).join('') +
            (shows && !r.sonarrConnected ? '<div class="warn">Sonarr isn\'t connected, so no show can be removed yet.</div>' : '') +
            (!shows && !r.radarrConnected ? '<div class="warn">Radarr isn\'t connected, so no movie can be removed yet.</div>' : '');

        var html;
        if (shows) {
            var readyShows = r.shows.filter(function (s) { return s.eligible; }).length;
            var readySeasons = r.shows.reduce(function (n, s) { return n + (s.eligible ? 0 : s.seasons.filter(function (x) { return x.eligible; }).length); }, 0);
            $('#summary').innerHTML = r.shows.length + ' shows · tracking ' + esc(r.trackedUsers.join(', ')) + ' · <b>' + readyShows + '</b> whole shows and <b>' + readySeasons + '</b> seasons ready to clear';
            html = r.shows.filter(function (s) {
                var flagged = s.users.some(function (u) { return u.flags.length; }) || s.seasons.some(function (x) { return x.users.some(function (u) { return u.flags.length; }); });
                return matches(s, s.users, s.eligible || s.seasons.some(function (x) { return x.eligible; }), flagged, filter, q);
            }).map(showCard).join('');
        } else {
            var ready = r.movies.filter(function (m) { return m.eligible; }).length;
            $('#summary').innerHTML = r.movies.length + ' movies · tracking ' + esc(r.trackedUsers.join(', ')) + ' · <b>' + ready + '</b> ready to clear';
            html = r.movies.filter(function (m) {
                return matches(m, m.users, m.eligible, m.users.some(function (u) { return u.flags.length; }), filter, q);
            }).map(movieCard).join('');
        }

        $('#list').innerHTML = html || '<p class="muted">Nothing matches.</p>';
        bindList();
    }

    function showCard(s) {
        var readySeasons = s.seasons.filter(function (x) { return x.eligible; }).length;
        var pill = !s.managed ? '<span class="pill pill-unmanaged" title="Clearr only removes shows Sonarr manages">not in Sonarr</span>'
            : s.eligible ? '<span class="pill pill-ready">show ready</span>'
            : readySeasons ? '<span class="pill pill-partial">' + readySeasons + ' season' + (readySeasons > 1 ? 's' : '') + ' ready</span>' : '';
        var rows = s.seasons.map(function (x) {
            return '<tr><td>' + esc(x.name) + '<div class="muted small">' + x.episodeCount + ' episodes</div></td>' +
                '<td><div class="chips">' + chips(x.users, true) + '</div></td>' +
                '<td class="reason' + (x.eligible ? ' ok' : '') + '">' + (x.eligible ? '✓ ' : '') + esc(x.reason) + '</td>' +
                '<td>' + (s.kept ? '<span class="muted small">show kept</span>' : keepBtn(x.seasonId, 'season', x.kept, x.keptBy)) + '</td></tr>';
        }).join('');
        return '<div class="card' + (state.open[s.showId] ? ' open' : '') + '" data-id="' + esc(s.showId) + '">' +
            '<div class="card-head"><div class="name">' + esc(s.name) + '<span class="sub">' + esc(s.status || 'unknown status') + ' · ' + s.episodeCount + ' episodes</span></div>' +
            '<div class="chips">' + chips(s.users, true) + '</div>' + pill + keepBtn(s.showId, 'show', s.kept, s.keptBy) + '</div>' +
            '<div class="card-body"><p class="reason' + (s.eligible ? ' ok' : '') + '"><b>Whole show:</b> ' + esc(s.reason) + '</p>' +
            '<table><thead><tr><th>Season</th><th>Viewers</th><th>Cleanup</th><th></th></tr></thead><tbody>' + rows + '</tbody></table></div></div>';
    }

    function movieCard(m) {
        var pill = !m.managed ? '<span class="pill pill-unmanaged" title="Clearr only removes movies Radarr manages">not in Radarr</span>'
            : m.eligible ? '<span class="pill pill-ready">ready</span>' : '';
        return '<div class="card' + (state.open[m.movieId] ? ' open' : '') + '" data-id="' + esc(m.movieId) + '">' +
            '<div class="card-head"><div class="name">' + esc(m.name) + (m.year ? '<span class="sub">' + m.year + '</span>' : '') + '</div>' +
            '<div class="chips">' + chips(m.users, false) + '</div>' + pill + keepBtn(m.movieId, 'movie', m.kept, m.keptBy) + '</div>' +
            '<div class="card-body"><p class="reason' + (m.eligible ? ' ok' : '') + '">' + (m.eligible ? '✓ ' : '') + esc(m.reason) + '</p></div></div>';
    }

    function bindList() {
        $$('#list .keep').forEach(function (b) {
            b.addEventListener('click', function (e) {
                e.stopPropagation();
                b.disabled = true;
                api('keep', 'POST', { id: b.getAttribute('data-id'), kind: b.getAttribute('data-kind'), keep: b.getAttribute('data-keep') === 'true' })
                    .then(function () { return loadReport(); })
                    .catch(function (err) { b.disabled = false; alert(err.message); });
            });
        });
        $$('#list .card-head').forEach(function (h) {
            h.addEventListener('click', function () {
                var c = h.parentNode; c.classList.toggle('open'); state.open[c.getAttribute('data-id')] = c.classList.contains('open');
            });
        });
    }

    function line(a) {
        return esc(a.title) + (a.kind === 'Season' ? ' – ' + esc(a.seasonName) : a.kind === 'Show' ? ' (whole show)' : '') + ' · ' + fmtSize(a.bytes);
    }

    function showResult(res) {
        var box = $('#result');
        box.classList.remove('hidden');
        if (res.error) { box.innerHTML = '<div class="error-text">' + esc(res.error) + '</div>'; return; }
        var main = res.actions.filter(function (a) { return a.action === 'WouldDelete' || a.action === 'Deleted' || a.action === 'Failed'; });
        var skipped = res.actions.filter(function (a) { return a.action === 'Skipped'; });
        if (!main.length && !skipped.length) { box.innerHTML = 'Nothing is ready to clear right now.'; return; }
        box.innerHTML = '<b>' + (res.dryRun ? 'Would remove' : 'Result') + ': ' + main.length + ' item(s), ' + fmtSize(res.totalBytes) + '</b><ul>' +
            main.map(function (a) { return '<li>' + (res.dryRun ? '' : '<b class="a-' + a.action + '">' + esc(a.action) + '</b> ') + line(a) + ' <span class="muted small">' + esc(a.detail) + '</span></li>'; }).join('') + '</ul>' +
            (skipped.length ? '<div class="muted small">Held back (' + skipped.length + '):</div><ul class="muted small">' + skipped.map(function (a) { return '<li>' + line(a) + ' — ' + esc(a.detail) + '</li>'; }).join('') + '</ul>' : '');
    }

    $('#filter').addEventListener('change', renderLibrary);
    $('#search').addEventListener('input', renderLibrary);
    $('#refresh').addEventListener('click', loadReport);
    $('#preview').addEventListener('click', function () {
        var b = this; b.disabled = true;
        api('cleanup?dryRun=true', 'POST').then(showResult).catch(function (e) { showResult(e.body || { error: e.message }); }).finally(function () { b.disabled = false; });
    });
    $('#deleteNow').addEventListener('click', function () {
        if (state.status.mode !== 'Delete') return;
        var b = this; b.disabled = true;
        api('cleanup?dryRun=true', 'POST').then(function (p) {
            showResult(p);
            var items = p.actions.filter(function (a) { return a.action === 'WouldDelete'; });
            if (!items.length) return null;
            var list = items.slice(0, 25).map(function (a) { return '• ' + a.title + (a.kind === 'Season' ? ' – ' + a.seasonName : a.kind === 'Show' ? ' (whole show)' : '') + ' · ' + fmtSize(a.bytes); }).join('\n');
            if (items.length > 25) list += '\n… and ' + (items.length - 25) + ' more';
            if (!confirm('PERMANENTLY remove ' + items.length + ' item(s), ' + fmtSize(p.totalBytes) + '?\n\n' + list + '\n\nSonarr/Radarr delete the files. This cannot be undone.')) return null;
            return api('cleanup?dryRun=false', 'POST').then(function (res) { showResult(res); return loadReport(); });
        }).catch(function (e) { showResult(e.body || { error: e.message }); }).finally(function () { b.disabled = false; });
    });

    // ------------------------------------------------------------ history
    function loadHistory() {
        api('history').then(function (list) {
            if (!list.length) { $('#history').innerHTML = '<p class="muted">Nothing has run yet.</p>'; return; }
            $('#history').innerHTML = '<table><thead><tr><th>When</th><th>Action</th><th>What</th><th>Size</th><th>Detail</th></tr></thead><tbody>' +
                list.map(function (h) {
                    var what = esc(h.title) + (h.kind === 'Season' ? ' – ' + esc(h.seasonName) : h.kind === 'Show' ? ' (whole show)' : '');
                    var label = h.action === 'WouldDelete' ? 'Would remove (dry run)' : h.action;
                    return '<tr><td>' + esc(fmtDate(h.date)) + '</td><td class="a-' + h.action + '">' + esc(label) + '</td><td>' + what + '</td><td>' + (h.bytes ? fmtSize(h.bytes) : '') + '</td><td class="reason">' + esc(h.detail) + '</td></tr>';
                }).join('') + '</tbody></table>';
        }).catch(function (e) { $('#history').textContent = e.message; });
    }

    // ------------------------------------------------------------ settings
    var form = $('#settingsForm');

    function getPath(o, p) { return p.split('.').reduce(function (a, k) { return a == null ? a : a[k]; }, o); }
    function setPath(o, p, v) { var ks = p.split('.'); var last = ks.pop(); ks.forEach(function (k) { o = o[k] = o[k] || {}; }); o[last] = v; }

    function loadSettings() {
        api('settings').then(function (r) {
            var s = r.settings;
            state.settings = s;
            $$('[name]', form).forEach(function (el) {
                var v = getPath(s, el.name);
                if (el.type === 'checkbox') el.checked = !!v; else el.value = v == null ? '' : v;
            });
            $('#apiKey').value = r.apiKey;
            renderHttps();
            $('#pwState').textContent = r.hasPassword ? 'A password is set. Enter a new one to change it.' : 'No password is set. Anyone who can reach Clearr can use it; set one if it\'s reachable by others.';
            $('#clearPwRow').classList.toggle('hidden', !r.hasPassword);
            $('#newPassword').value = ''; $('#clearPassword').checked = false;
            loadUsers(s.excludedUserIds || []);
        });
    }

    function renderHttps() {
        var h = state.status.https || {};
        $('#httpsState').innerHTML = h.active
            ? '<span class="ok-text">HTTPS is on, port ' + h.port + ' (' + esc(h.certificate) + ').</span>'
            : h.error ? '<span class="error-text">' + esc(h.error) + '</span>' : 'HTTPS is off.';
        $('#restartRow').classList.toggle('hidden', !h.restartNeeded);
    }

    $('#checkCert').addEventListener('click', function () {
        var out = $('#certResult'); out.textContent = 'Checking…'; out.className = 'test-result';
        api('https/check', 'POST', {
            enabled: true, port: parseInt(form.elements['https.port'].value, 10) || 8586,
            certificatePath: form.elements['https.certificatePath'].value.trim(), keyPath: form.elements['https.keyPath'].value.trim(),
            certificatePassword: form.elements['https.certificatePassword'].value
        }).then(function (r) { out.textContent = r.message; out.className = 'test-result ' + (r.ok ? 'ok-text' : 'error-text'); })
          .catch(function (e) { out.textContent = e.message; out.className = 'test-result error-text'; });
    });

    $('#restart').addEventListener('click', function () {
        var b = this; b.disabled = true; b.textContent = 'Restarting…';
        api('restart', 'POST').catch(function () {}).then(function () {
            var tries = 0;
            (function wait() {
                setTimeout(function () {
                    fetch('ping', { cache: 'no-store' }).then(function (r) { if (r.ok) location.reload(); else throw 0; })
                        .catch(function () { if (++tries < 60) wait(); else { b.textContent = 'Clearr didn\'t come back. Is the container set to restart: unless-stopped?'; } });
                }, 1500);
            })();
        });
    });

    function loadUsers(excluded) {
        $('#users').innerHTML = '<span class="muted small">Loading users…</span>';
        api('users').then(function (users) {
            $('#users').innerHTML = users.length ? users.map(function (u) {
                return '<label class="check"><input type="checkbox" class="exclude" value="' + esc(u.id) + '"' + (excluded.indexOf(u.id) >= 0 ? ' checked' : '') + '> Ignore ' + esc(u.name) + '</label>';
            }).join('') : '<span class="muted small">Connect Jellyfin to see users.</span>';
        }).catch(function (e) { $('#users').innerHTML = '<span class="error-text">' + esc(e.message) + '</span>'; });
    }

    $$('.test', form).forEach(function (b) {
        b.addEventListener('click', function () {
            var svc = b.getAttribute('data-test'), out = b.nextElementSibling;
            out.textContent = 'Testing…'; out.className = 'test-result';
            api('test/' + svc, 'POST', { url: form.elements[svc + '.url'].value.trim(), apiKey: form.elements[svc + '.apiKey'].value.trim() })
                .then(function (r) { out.textContent = r.message; out.className = 'test-result ' + (r.ok ? 'ok-text' : 'error-text'); })
                .catch(function (e) { out.textContent = e.message; out.className = 'test-result error-text'; });
        });
    });

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        var s = JSON.parse(JSON.stringify(state.settings || {}));
        $$('[name]', form).forEach(function (el) {
            var v = el.type === 'checkbox' ? el.checked : el.type === 'number' ? (parseInt(el.value, 10) || 0) : el.value.trim();
            setPath(s, el.name, v);
        });
        s.excludedUserIds = $$('.exclude:checked').map(function (x) { return x.value; });
        api('settings', 'PUT', { settings: s, newPassword: $('#newPassword').value, clearPassword: $('#clearPassword').checked }).then(function () {
            $('#saveMsg').textContent = 'Saved'; $('#saveMsg').className = 'ok-text';
            setTimeout(function () { $('#saveMsg').textContent = ''; }, 3000);
            state.report = null;
            return api('status').then(function (st) { state.status = st; renderStatus(); $('#logout').classList.toggle('hidden', !st.passwordRequired); loadSettings(); });
        }).catch(function (err) { $('#saveMsg').textContent = err.message; $('#saveMsg').className = 'error-text'; });
    });

    // Optional support link: set "buyMeACoffee" in support.json to show it.
    fetch('support.json', { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : {}; }).then(function (j) {
        if (j && j.buyMeACoffee) { var a = $('#coffee'); a.href = 'https://buymeacoffee.com/' + encodeURIComponent(j.buyMeACoffee); a.classList.remove('hidden'); }
    }).catch(function () {});

    boot();
})();
