/*
 * Clearr UI — shared by the Clearr app and the Clearr Jellyfin plugin.
 *
 *   var ui = ClearrUI.mount(rootElement, {
 *       host: 'app' | 'plugin',
 *       api: function (path, method, body) { return Promise<json> },   // rejects with Error (.status, .body)
 *       onStatus: function (status) {}                                  // optional
 *   });
 *   ui.show('shows' | 'movies' | 'history' | 'settings');
 */
(function () {
    'use strict';

    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function fmtSize(b) { if (!b) return '0 GB'; var gb = b / 1073741824; return gb >= 1 ? gb.toFixed(gb >= 100 ? 0 : 1) + ' GB' : Math.max(1, Math.round(b / 1048576)) + ' MB'; }
    function fmtDate(d) { if (!d) return ''; var x = new Date(d); return isNaN(x) ? '' : x.toLocaleString(); }

    function template(host) {
        var app = host === 'app';
        var conn = function (svc, title, note, ph, keyHint) {
            return '<div class="cl-conn">' +
                '<h3>' + title + ' <span class="cl-opt">' + note + '</span></h3>' +
                '<label>URL<input name="' + svc + '.url" placeholder="' + ph + '"></label>' +
                '<label>API key<input name="' + svc + '.apiKey" type="password" placeholder="' + keyHint + '"></label>' +
                '<button type="button" class="cl-btn cl-small cl-test" data-test="' + svc + '">Test</button><span class="cl-test-result"></span></div>';
        };
        return '' +
            '<div class="cl-banner cl-hidden" data-el="banner"></div>' +

            '<section class="cl-page" data-page="library">' +
            '  <h1 data-el="libTitle">Shows</h1>' +
            '  <div class="cl-toolbar">' +
            '    <select data-el="filter">' +
            '      <option value="all">All</option><option value="ready">Ready to clear</option><option value="flagged">Dropped-off viewers</option>' +
            '      <option value="watching">Someone is watching</option><option value="untouched">Nobody started</option><option value="kept">Kept</option>' +
            '      <option value="unmanaged">Not in Sonarr/Radarr</option>' +
            '    </select>' +
            '    <input type="search" data-el="search" placeholder="Search">' +
            '    <button class="cl-btn" data-el="refresh">Refresh</button>' +
            '    <button class="cl-btn" data-el="preview">Preview cleanup</button>' +
            '    <button class="cl-btn cl-danger cl-hidden" data-el="deleteNow">Delete eligible now</button>' +
            '  </div>' +
            '  <div class="cl-note" data-el="modeNote"></div>' +
            '  <div class="cl-result cl-hidden" data-el="result"></div>' +
            '  <div data-el="warnings"></div>' +
            '  <div class="cl-muted cl-summary" data-el="summary"></div>' +
            '  <div data-el="list"></div>' +
            '</section>' +

            '<section class="cl-page" data-page="history"><h1>History</h1><div data-el="history"></div></section>' +

            '<section class="cl-page" data-page="settings"><h1>Settings</h1>' +
            '<form class="cl-settings" data-el="form" autocomplete="off">' +
            '<h2>Connections</h2>' +
            (app ? conn('jellyfin', 'Jellyfin', 'required', 'http://192.168.1.6:8096', 'Dashboard → API Keys → +') : '') +
            conn('sonarr', 'Sonarr', 'for shows', 'http://192.168.1.6:8989', 'Settings → General → API Key') +
            conn('radarr', 'Radarr', 'for movies', 'http://192.168.1.6:7878', 'Settings → General → API Key') +
            '<p class="cl-hint">Sonarr and Radarr delete the files, and Clearr unmonitors what\'s removed so it isn\'t downloaded again. Shows stay monitored, so new seasons still arrive.</p>' +
            (app ? '' : '<label class="cl-check"><input type="checkbox" name="allowDirectDelete"> Also remove items Sonarr/Radarr don\'t manage, through Jellyfin</label>' +
                '<p class="cl-hint">Off by default. When on, finished shows and movies that Sonarr/Radarr don\'t know about are deleted from disk by Jellyfin itself. They are not unmonitored anywhere, so if another tool manages them they may come back.</p>') +

            '<h2>Cleanup</h2>' +
            '<label>Cleanup mode<select name="mode"><option value="Off">Off – only track and flag</option><option value="DryRun">Dry run – log what would be removed</option><option value="Delete">Delete – remove files</option></select>' +
            '<span class="cl-hint">Applies to everything: the daily run and the "Delete eligible now" button. Nothing is ever removed unless this is Delete.</span></label>' +
            (app ? '<label>Daily run at<input name="scheduleTime" type="time"><span class="cl-hint">Container time zone (set TZ in Docker).</span></label>'
                 : '<p class="cl-hint">The daily run is a Jellyfin scheduled task: <b>Dashboard → Scheduled Tasks → Clearr</b>.</p>') +
            '<label>Max GB removed per run<input name="maxDeleteGbPerRun" type="number" min="0"><span class="cl-hint">0 = no limit. Anything over the limit waits for the next run.</span></label>' +
            '<label>Grace period (days after the last viewer finished)<input name="graceDays" type="number" min="0"></label>' +
            '<label class="cl-check"><input type="checkbox" name="deleteSeasons"> Remove finished seasons</label>' +
            '<label class="cl-check"><input type="checkbox" name="deleteShows"> Remove whole shows when finished</label>' +
            '<label class="cl-check"><input type="checkbox" name="requireEndedForShowDelete"> Only remove whole shows that have Ended</label>' +
            '<label class="cl-check"><input type="checkbox" name="protectLatestSeasonOfContinuing"> Never remove the newest season of a continuing show</label>' +
            '<label class="cl-check"><input type="checkbox" name="deleteMovies"> Remove finished movies</label>' +
            '<label class="cl-check"><input type="checkbox" name="includeSpecials"> Count specials (season 0)</label>' +
            '<label class="cl-check"><input type="checkbox" name="refreshJellyfinAfterCleanup"> Refresh the Jellyfin library after a cleanup</label>' +
            '<label>Keep tag<input name="keepTag"><span class="cl-hint">Items with this tag in Jellyfin, Sonarr or Radarr are never removed. Keep buttons add it in Sonarr/Radarr too.</span></label>' +

            '<h2>Dropped-off viewers</h2>' +
            '<label>Flag a viewer after this many days without progress<input name="inactiveDays" type="number" min="1"></label>' +
            '<label>"Sampled" means watched at most this many episodes<input name="sampledMaxEpisodes" type="number" min="0"><span class="cl-hint">More than that is "Stalled". "Mid-episode" is added if they stopped partway through.</span></label>' +
            '<label class="cl-check"><input type="checkbox" name="abandonedUsersBlockCleanup"> Dropped-off viewers still hold back cleanup</label>' +

            '<h2>Users</h2><p class="cl-hint">Ticked users are ignored completely (for example a test or kids account).</p><div data-el="users" class="cl-users"></div>' +

            (app ? '<h2>Security</h2><p class="cl-hint" data-el="pwState"></p>' +
                '<label>New password<input data-el="newPassword" type="password" autocomplete="new-password" placeholder="Leave empty to keep"></label>' +
                '<label class="cl-check cl-hidden" data-el="clearPwRow"><input type="checkbox" data-el="clearPassword"> Remove the password</label>' +
                '<label>API key <span class="cl-hint">(for scripts / Home Assistant: X-Api-Key header)</span><input data-el="apiKey" readonly></label>' +

                '<h2>HTTPS</h2>' +
                '<p class="cl-hint">Not needed if a reverse proxy (like Nginx Proxy Manager) already handles HTTPS for Clearr. Plain HTTP always stays on port 8585.</p>' +
                '<p class="cl-hint" data-el="httpsState"></p>' +
                '<label class="cl-check"><input type="checkbox" name="https.enabled"> Enable HTTPS</label>' +
                '<label>HTTPS port<input name="https.port" type="number" min="1" max="65535"><span class="cl-hint">Inside the container. Map it in Docker too, e.g. 8586:8586.</span></label>' +
                '<label>Certificate file<input name="https.certificatePath" placeholder="Leave empty for a self-signed certificate"><span class="cl-hint">A path inside the container: .pfx/.p12, or a PEM certificate such as /certs/fullchain.pem. Mount the folder read-only.</span></label>' +
                '<label>Private key file<input name="https.keyPath" placeholder="/certs/privkey.pem"><span class="cl-hint">For PEM certificates only. Not needed for .pfx.</span></label>' +
                '<label>Certificate password<input name="https.certificatePassword" type="password" autocomplete="off"><span class="cl-hint">Only for password-protected .pfx files or encrypted keys.</span></label>' +
                '<button type="button" class="cl-btn cl-small" data-el="checkCert">Check certificate</button><span data-el="certResult" class="cl-test-result"></span>' +
                '<div data-el="restartRow" class="cl-banner cl-hidden" style="margin-top:14px">HTTPS changes take effect after a restart. <button type="button" class="cl-btn cl-small cl-primary" data-el="restart">Restart Clearr</button></div>'
                : '') +

            '<div class="cl-save-row"><button type="submit" class="cl-btn cl-primary">Save</button><span data-el="saveMsg"></span></div>' +
            '</form></section>';
    }

    function mount(root, opts) {
        var host = opts.host || 'app';
        var api = opts.api;
        var state = { status: null, report: null, page: 'shows', open: {}, settings: null };
        root.classList.add('clearr');
        root.setAttribute('data-host', host);
        root.innerHTML = template(host);
        var el = function (name) { return root.querySelector('[data-el="' + name + '"]'); };
        var all = function (sel) { return Array.prototype.slice.call(root.querySelectorAll(sel)); };
        var form = el('form');

        // ------------------------------------------------------------ status
        function loadStatus() {
            return api('status').then(function (s) { state.status = s; renderStatus(); if (opts.onStatus) opts.onStatus(s); return s; });
        }

        function modeLabel(m) { return { Off: 'Off', DryRun: 'Dry run', Delete: 'Delete mode' }[m] || m; }

        function renderStatus() {
            var s = state.status;
            var banner = el('banner');
            if (!s.configured) {
                banner.innerHTML = 'Welcome to Clearr. Start by connecting <b>' + (host === 'app' ? 'Jellyfin' : 'Sonarr') + '</b> (and Sonarr/Radarr) in <a href="#" data-goto="settings">Settings</a>.';
                banner.classList.remove('cl-hidden');
            } else banner.classList.add('cl-hidden');
            el('deleteNow').classList.toggle('cl-hidden', s.mode !== 'Delete');
            el('modeNote').innerHTML = '<span class="cl-mode cl-mode-' + s.mode + '">' + modeLabel(s.mode) + '</span> ' + (s.mode === 'Delete'
                ? '"Delete eligible now" removes files right after you confirm the list.'
                : 'Nothing can be removed in this mode. "Preview cleanup" shows what would go. Use Keep to protect anything.');
        }

        root.addEventListener('click', function (e) {
            var g = e.target.closest && e.target.closest('[data-goto]');
            if (g) { e.preventDefault(); show(g.getAttribute('data-goto')); if (opts.onNavigate) opts.onNavigate(g.getAttribute('data-goto')); }
        });

        // ------------------------------------------------------------ pages
        function show(page) {
            if (['shows', 'movies', 'history', 'settings'].indexOf(page) < 0) page = 'shows';
            state.page = page;
            all('.cl-page').forEach(function (p) {
                var name = p.getAttribute('data-page');
                p.classList.toggle('cl-hidden', !(name === page || (name === 'library' && (page === 'shows' || page === 'movies'))));
            });
            var ready = state.status ? Promise.resolve() : loadStatus();
            ready.then(function () {
                if (page === 'shows' || page === 'movies') {
                    el('libTitle').textContent = page === 'shows' ? 'Shows' : 'Movies';
                    el('result').classList.add('cl-hidden');
                    if (state.report) renderLibrary(); else loadReport();
                }
                if (page === 'history') loadHistory();
                if (page === 'settings') loadSettings();
            });
        }

        // ------------------------------------------------------------ library
        function loadReport() {
            if (!state.status.configured) { el('summary').textContent = ''; el('list').innerHTML = ''; return Promise.resolve(); }
            el('summary').textContent = 'Loading…';
            return api('report').then(function (r) { state.report = r; renderLibrary(); })
                .catch(function (e) { el('summary').textContent = ''; el('list').innerHTML = '<div class="cl-warn">' + esc(e.message) + '</div>'; });
        }

        function flagLabel(f) { return f === 'MidEpisode' ? (state.page === 'movies' ? 'mid-movie' : 'mid-episode') : f.toLowerCase(); }

        function chip(u, counts) {
            var cls = 'cl-chip cl-st-' + u.status + (u.flags.length ? ' cl-flagged' : '');
            var t = esc(u.userName);
            if (u.status === 'Finished') t += ' ✓';
            else if (u.status === 'InProgress' && counts) t += ' ' + u.watched + '/' + u.total;
            if (u.flags.length) t += ' · ' + u.flags.map(flagLabel).join(', ');
            var title = u.status + (u.lastEpisode ? ' — last ' + u.lastEpisode : '') + (u.lastActivity ? ' on ' + fmtDate(u.lastActivity) : '');
            return '<span class="' + cls + '" title="' + esc(title) + '">' + t + '</span>';
        }

        function chips(users, counts) {
            var c = users.filter(function (u) { return u.status !== 'NotStarted'; }).map(function (u) { return chip(u, counts); }).join('');
            return c || '<span class="cl-muted cl-small">nobody started</span>';
        }

        function keepBtn(id, kind, kept, keptBy) {
            var external = kept && keptBy && keptBy !== 'Clearr' && keptBy !== 'Sonarr tag' && keptBy !== 'Radarr tag';
            return '<button type="button" class="cl-keep' + (kept ? ' cl-on' : '') + '" data-id="' + esc(id) + '" data-kind="' + kind + '" data-keep="' + (!kept) + '"' +
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
            var filter = el('filter').value, q = el('search').value.trim().toLowerCase();
            el('filter').options[6].textContent = shows ? 'Not in Sonarr' : 'Not in Radarr';

            el('warnings').innerHTML = r.warnings.map(function (w) { return '<div class="cl-warn">' + esc(w) + '</div>'; }).join('') +
                (shows && !r.sonarrConnected && !r.directDelete ? '<div class="cl-warn">Sonarr isn\'t connected, so no show can be removed yet.</div>' : '') +
                (!shows && !r.radarrConnected && !r.directDelete ? '<div class="cl-warn">Radarr isn\'t connected, so no movie can be removed yet.</div>' : '');

            var html;
            if (shows) {
                var readyShows = r.shows.filter(function (s) { return s.eligible; }).length;
                var readySeasons = r.shows.reduce(function (n, s) { return n + (s.eligible ? 0 : s.seasons.filter(function (x) { return x.eligible; }).length); }, 0);
                el('summary').innerHTML = r.shows.length + ' shows · tracking ' + esc(r.trackedUsers.join(', ')) + ' · <b>' + readyShows + '</b> whole shows and <b>' + readySeasons + '</b> seasons ready to clear';
                html = r.shows.filter(function (s) {
                    var flagged = s.users.some(function (u) { return u.flags.length; }) || s.seasons.some(function (x) { return x.users.some(function (u) { return u.flags.length; }); });
                    return matches(s, s.users, s.eligible || s.seasons.some(function (x) { return x.eligible; }), flagged, filter, q);
                }).map(showCard).join('');
            } else {
                var ready = r.movies.filter(function (m) { return m.eligible; }).length;
                el('summary').innerHTML = r.movies.length + ' movies · tracking ' + esc(r.trackedUsers.join(', ')) + ' · <b>' + ready + '</b> ready to clear';
                html = r.movies.filter(function (m) {
                    return matches(m, m.users, m.eligible, m.users.some(function (u) { return u.flags.length; }), filter, q);
                }).map(movieCard).join('');
            }

            el('list').innerHTML = html || '<p class="cl-muted">Nothing matches.</p>';
            bindList();
        }

        function viaPill(item, arr) {
            if (item.via === 'Jellyfin') return '<span class="cl-pill cl-pill-unmanaged" title="Not managed by ' + arr + '; would be removed through Jellyfin">via Jellyfin</span>';
            if (!item.managed) return '<span class="cl-pill cl-pill-unmanaged" title="Clearr only removes what ' + arr + ' manages">not in ' + arr + '</span>';
            return '';
        }

        function showCard(s) {
            var readySeasons = s.seasons.filter(function (x) { return x.eligible; }).length;
            var pill = s.eligible ? '<span class="cl-pill cl-pill-ready">show ready</span>'
                : readySeasons ? '<span class="cl-pill cl-pill-partial">' + readySeasons + ' season' + (readySeasons > 1 ? 's' : '') + ' ready</span>' : '';
            var rows = s.seasons.map(function (x) {
                return '<tr><td>' + esc(x.name) + '<div class="cl-muted cl-small">' + x.episodeCount + ' episodes</div></td>' +
                    '<td><div class="cl-chips">' + chips(x.users, true) + '</div></td>' +
                    '<td class="cl-reason' + (x.eligible ? ' cl-ok' : '') + '">' + (x.eligible ? '✓ ' : '') + esc(x.reason) + '</td>' +
                    '<td>' + (s.kept ? '<span class="cl-muted cl-small">show kept</span>' : keepBtn(x.seasonId, 'season', x.kept, x.keptBy)) + '</td></tr>';
            }).join('');
            return '<div class="cl-card' + (state.open[s.showId] ? ' cl-open' : '') + '" data-id="' + esc(s.showId) + '">' +
                '<div class="cl-card-head"><div class="cl-name">' + esc(s.name) + '<span class="cl-sub">' + esc(s.status || 'unknown status') + ' · ' + s.episodeCount + ' episodes</span></div>' +
                '<div class="cl-chips">' + chips(s.users, true) + '</div>' + viaPill(s, 'Sonarr') + pill + keepBtn(s.showId, 'show', s.kept, s.keptBy) + '</div>' +
                '<div class="cl-card-body"><p class="cl-reason' + (s.eligible ? ' cl-ok' : '') + '"><b>Whole show:</b> ' + esc(s.reason) + '</p>' +
                '<table class="cl-table"><thead><tr><th>Season</th><th>Viewers</th><th>Cleanup</th><th></th></tr></thead><tbody>' + rows + '</tbody></table></div></div>';
        }

        function movieCard(m) {
            var pill = m.eligible ? '<span class="cl-pill cl-pill-ready">ready</span>' : '';
            return '<div class="cl-card' + (state.open[m.movieId] ? ' cl-open' : '') + '" data-id="' + esc(m.movieId) + '">' +
                '<div class="cl-card-head"><div class="cl-name">' + esc(m.name) + (m.year ? '<span class="cl-sub">' + m.year + '</span>' : '') + '</div>' +
                '<div class="cl-chips">' + chips(m.users, false) + '</div>' + viaPill(m, 'Radarr') + pill + keepBtn(m.movieId, 'movie', m.kept, m.keptBy) + '</div>' +
                '<div class="cl-card-body"><p class="cl-reason' + (m.eligible ? ' cl-ok' : '') + '">' + (m.eligible ? '✓ ' : '') + esc(m.reason) + '</p></div></div>';
        }

        function bindList() {
            all('[data-el="list"] .cl-keep').forEach(function (b) {
                b.addEventListener('click', function (e) {
                    e.stopPropagation();
                    b.disabled = true;
                    api('keep', 'POST', { id: b.getAttribute('data-id'), kind: b.getAttribute('data-kind'), keep: b.getAttribute('data-keep') === 'true' })
                        .then(function () { return loadReport(); })
                        .catch(function (err) { b.disabled = false; alert(err.message); });
                });
            });
            all('[data-el="list"] .cl-card-head').forEach(function (h) {
                h.addEventListener('click', function () {
                    var c = h.parentNode; c.classList.toggle('cl-open'); state.open[c.getAttribute('data-id')] = c.classList.contains('cl-open');
                });
            });
        }

        function line(a) {
            return esc(a.title) + (a.kind === 'Season' ? ' – ' + esc(a.seasonName) : a.kind === 'Show' ? ' (whole show)' : '') + ' · ' + fmtSize(a.bytes);
        }

        function showResult(res) {
            var box = el('result');
            box.classList.remove('cl-hidden');
            if (res.error) { box.innerHTML = '<div class="cl-error">' + esc(res.error) + '</div>'; return; }
            var main = res.actions.filter(function (a) { return a.action === 'WouldDelete' || a.action === 'Deleted' || a.action === 'Failed'; });
            var skipped = res.actions.filter(function (a) { return a.action === 'Skipped'; });
            if (!main.length && !skipped.length) { box.innerHTML = 'Nothing is ready to clear right now.'; return; }
            box.innerHTML = '<b>' + (res.dryRun ? 'Would remove' : 'Result') + ': ' + main.length + ' item(s), ' + fmtSize(res.totalBytes) + '</b><ul>' +
                main.map(function (a) { return '<li>' + (res.dryRun ? '' : '<b class="cl-a-' + a.action + '">' + esc(a.action) + '</b> ') + line(a) + ' <span class="cl-muted cl-small">' + esc(a.detail) + '</span></li>'; }).join('') + '</ul>' +
                (skipped.length ? '<div class="cl-muted cl-small">Held back (' + skipped.length + '):</div><ul class="cl-muted cl-small">' + skipped.map(function (a) { return '<li>' + line(a) + ' — ' + esc(a.detail) + '</li>'; }).join('') + '</ul>' : '');
        }

        el('filter').addEventListener('change', renderLibrary);
        el('search').addEventListener('input', renderLibrary);
        el('refresh').addEventListener('click', function () { loadStatus().then(loadReport); });
        el('preview').addEventListener('click', function () {
            var b = this; b.disabled = true;
            api('cleanup?dryRun=true', 'POST').then(showResult).catch(function (e) { showResult(e.body || { error: e.message }); }).then(function () { b.disabled = false; });
        });
        el('deleteNow').addEventListener('click', function () {
            if (state.status.mode !== 'Delete') return;
            var b = this; b.disabled = true;
            api('cleanup?dryRun=true', 'POST').then(function (p) {
                showResult(p);
                var items = p.actions.filter(function (a) { return a.action === 'WouldDelete'; });
                if (!items.length) return null;
                var list = items.slice(0, 25).map(function (a) { return '• ' + a.title + (a.kind === 'Season' ? ' – ' + a.seasonName : a.kind === 'Show' ? ' (whole show)' : '') + ' · ' + fmtSize(a.bytes); }).join('\n');
                if (items.length > 25) list += '\n… and ' + (items.length - 25) + ' more';
                if (!confirm('PERMANENTLY remove ' + items.length + ' item(s), ' + fmtSize(p.totalBytes) + '?\n\n' + list + '\n\nThis cannot be undone.')) return null;
                return api('cleanup?dryRun=false', 'POST').then(function (res) { showResult(res); return loadReport(); });
            }).catch(function (e) { showResult(e.body || { error: e.message }); }).then(function () { b.disabled = false; });
        });

        // ------------------------------------------------------------ history
        function loadHistory() {
            api('history').then(function (list) {
                if (!list.length) { el('history').innerHTML = '<p class="cl-muted">Nothing has run yet.</p>'; return; }
                el('history').innerHTML = '<table class="cl-table"><thead><tr><th>When</th><th>Action</th><th>What</th><th>Size</th><th>Detail</th></tr></thead><tbody>' +
                    list.map(function (h) {
                        var what = esc(h.title) + (h.kind === 'Season' ? ' – ' + esc(h.seasonName) : h.kind === 'Show' ? ' (whole show)' : '');
                        var label = h.action === 'WouldDelete' ? 'Would remove (dry run)' : h.action;
                        return '<tr><td>' + esc(fmtDate(h.date)) + '</td><td class="cl-a-' + h.action + '">' + esc(label) + '</td><td>' + what + '</td><td>' + (h.bytes ? fmtSize(h.bytes) : '') + '</td><td class="cl-reason">' + esc(h.detail) + '</td></tr>';
                    }).join('') + '</tbody></table>';
            }).catch(function (e) { el('history').textContent = e.message; });
        }

        // ------------------------------------------------------------ settings
        function getPath(o, p) { return p.split('.').reduce(function (a, k) { return a == null ? a : a[k]; }, o); }
        function setPath(o, p, v) { var ks = p.split('.'); var last = ks.pop(); ks.forEach(function (k) { o = o[k] = o[k] || {}; }); o[last] = v; }

        function loadSettings() {
            api('settings').then(function (r) {
                var s = r.settings;
                state.settings = s;
                all('[name]').forEach(function (f) {
                    var v = getPath(s, f.name);
                    if (f.type === 'checkbox') f.checked = !!v; else f.value = v == null ? '' : v;
                });
                if (host === 'app') {
                    el('apiKey').value = r.apiKey || '';
                    el('pwState').textContent = r.hasPassword ? 'A password is set. Enter a new one to change it.' : 'No password is set. Anyone who can reach Clearr can use it; set one if it\'s reachable by others.';
                    el('clearPwRow').classList.toggle('cl-hidden', !r.hasPassword);
                    el('newPassword').value = ''; el('clearPassword').checked = false;
                    renderHttps();
                }
                loadUsers(s.excludedUserIds || []);
            });
        }

        function loadUsers(excluded) {
            el('users').innerHTML = '<span class="cl-muted cl-small">Loading users…</span>';
            api('users').then(function (users) {
                el('users').innerHTML = users.length ? users.map(function (u) {
                    return '<label class="cl-check"><input type="checkbox" class="cl-exclude" value="' + esc(u.id) + '"' + (excluded.indexOf(u.id) >= 0 ? ' checked' : '') + '> Ignore ' + esc(u.name) + '</label>';
                }).join('') : '<span class="cl-muted cl-small">Connect Jellyfin to see users.</span>';
            }).catch(function (e) { el('users').innerHTML = '<span class="cl-error">' + esc(e.message) + '</span>'; });
        }

        function renderHttps() {
            var h = state.status.https || {};
            el('httpsState').innerHTML = h.active
                ? '<span class="cl-ok">HTTPS is on, port ' + h.port + ' (' + esc(h.certificate) + ').</span>'
                : h.error ? '<span class="cl-error">' + esc(h.error) + '</span>' : 'HTTPS is off.';
            el('restartRow').classList.toggle('cl-hidden', !h.restartNeeded);
        }

        all('.cl-test').forEach(function (b) {
            b.addEventListener('click', function () {
                var svc = b.getAttribute('data-test'), out = b.nextElementSibling;
                out.textContent = 'Testing…'; out.className = 'cl-test-result';
                api('test/' + svc, 'POST', { url: form.elements[svc + '.url'].value.trim(), apiKey: form.elements[svc + '.apiKey'].value.trim() })
                    .then(function (r) { out.textContent = r.message; out.className = 'cl-test-result ' + (r.ok ? 'cl-ok' : 'cl-error'); })
                    .catch(function (e) { out.textContent = e.message; out.className = 'cl-test-result cl-error'; });
            });
        });

        if (host === 'app') {
            el('checkCert').addEventListener('click', function () {
                var out = el('certResult'); out.textContent = 'Checking…'; out.className = 'cl-test-result';
                api('https/check', 'POST', {
                    enabled: true, port: parseInt(form.elements['https.port'].value, 10) || 8586,
                    certificatePath: form.elements['https.certificatePath'].value.trim(), keyPath: form.elements['https.keyPath'].value.trim(),
                    certificatePassword: form.elements['https.certificatePassword'].value
                }).then(function (r) { out.textContent = r.message; out.className = 'cl-test-result ' + (r.ok ? 'cl-ok' : 'cl-error'); })
                  .catch(function (e) { out.textContent = e.message; out.className = 'cl-test-result cl-error'; });
            });
            el('restart').addEventListener('click', function () {
                var b = this; b.disabled = true; b.textContent = 'Restarting…';
                api('restart', 'POST').catch(function () {}).then(function () {
                    var tries = 0;
                    (function wait() {
                        setTimeout(function () {
                            fetch('ping', { cache: 'no-store' }).then(function (r) { if (r.ok) location.reload(); else throw 0; })
                                .catch(function () { if (++tries < 60) wait(); else b.textContent = 'Clearr didn\'t come back. Is the container set to restart: unless-stopped?'; });
                        }, 1500);
                    })();
                });
            });
        }

        form.addEventListener('submit', function (e) {
            e.preventDefault();
            var s = JSON.parse(JSON.stringify(state.settings || {}));
            all('[name]').forEach(function (f) {
                var v = f.type === 'checkbox' ? f.checked : f.type === 'number' ? (parseInt(f.value, 10) || 0) : f.value.trim();
                setPath(s, f.name, v);
            });
            s.excludedUserIds = all('.cl-exclude:checked').map(function (x) { return x.value; });
            var body = { settings: s };
            if (host === 'app') { body.newPassword = el('newPassword').value; body.clearPassword = el('clearPassword').checked; }
            var msg = el('saveMsg');
            api('settings', 'PUT', body).then(function () {
                msg.textContent = 'Saved'; msg.className = 'cl-ok';
                setTimeout(function () { msg.textContent = ''; }, 3000);
                state.report = null;
                return loadStatus().then(loadSettings);
            }).catch(function (err) { msg.textContent = err.message; msg.className = 'cl-error'; });
        });

        loadStatus().catch(function () {});
        return { show: show, refreshStatus: loadStatus };
    }

    window.ClearrUI = { mount: mount, fmtDate: fmtDate };
})();
