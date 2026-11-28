/*
 * Clearr UI — shared by the Clearr app and the Clearr Jellyfin plugin.
 *
 *   var ui = ClearrUI.mount(rootElement, {
 *       host: 'app' | 'plugin',
 *       api: function (path, method, body) { return Promise<json> },   // rejects with Error (.status, .body)
 *       onStatus: function (status) {},                                 // optional
 *       onReady: function () {},                                        // optional: language loaded, UI drawn
 *       onNavigate: function (page) {},                                 // optional
 *       language: 'sv-SE'                                               // optional: follow the host's language (the plugin
 *                                                                       // passes Jellyfin's); otherwise the Language setting
 *   });
 *   ui.show('shows' | 'movies' | 'history' | 'settings' | 'logs');   // logs: app only
 *
 * Text comes from lang-<code>.json next to this file (English is the fallback for anything missing).
 * ClearrUI.t(key, args) translates; ClearrUI.translate(element) fills [data-i18n] / [data-i18n-placeholder].
 */
(function () {
    'use strict';

    // ---------------------------------------------------------------- translations
    var LANGUAGES = { en: 'English', sv: 'Svenska' };
    var base = (function () {
        var s = document.currentScript && document.currentScript.src;
        return s ? s.replace(/[^/?#]*([?#].*)?$/, '') : 'ui/';
    })();
    var dict = {}, fallback = {}, current = 'en';

    function fetchLang(code) {
        return fetch(base + 'lang-' + code + '.json', { cache: 'no-cache' }).then(function (r) { return r.ok ? r.json() : {}; }).catch(function () { return {}; });
    }

    function baseCode(code) { return String(code || '').toLowerCase().split(/[-_]/)[0]; }

    /**
     * Picks the language. A host language (Jellyfin's) is followed exactly, falling back to English;
     * otherwise the setting, else the browser's first supported language, else English.
     */
    function pick(setting, hostLanguage) {
        if (hostLanguage) return LANGUAGES[baseCode(hostLanguage)] ? baseCode(hostLanguage) : 'en';
        if (setting && LANGUAGES[baseCode(setting)]) return baseCode(setting);
        var wanted = (navigator.languages || [navigator.language || 'en']).map(baseCode);
        for (var i = 0; i < wanted.length; i++) if (LANGUAGES[wanted[i]]) return wanted[i];
        return 'en';
    }

    function loadLanguage(setting, hostLanguage) {
        var code = pick(setting, hostLanguage);
        var en = Object.keys(fallback).length ? Promise.resolve(fallback) : fetchLang('en');
        return en.then(function (e) {
            fallback = e;
            return code === 'en' ? e : fetchLang(code);
        }).then(function (d) { dict = d; current = code; document.documentElement.setAttribute('data-clearr-lang', code); return code; });
    }

    function people(list) {
        return list.map(function (p) {
            var flags = (p.flags || []).map(function (f) { return t('flagInline.' + f); });
            return t('reason.person', { name: p.name, watched: p.watched, total: p.total, flags: flags.length ? ', ' + flags.join(', ') : '' });
        }).join(', ');
    }

    /** Translates a key; {name} placeholders are filled from args (messages and people lists are rendered too). */
    function t(key, args) {
        var s = dict[key] != null ? dict[key] : fallback[key] != null ? fallback[key] : key;
        if (args) {
            Object.keys(args).forEach(function (k) {
                var v = args[k];
                var text = v == null ? '' : Array.isArray(v) ? people(v) : typeof v === 'object' && v.code ? msg(v) : String(v);
                s = s.split('{' + k + '}').join(text);
            });
        }
        return s;
    }

    function has(key) { return dict[key] != null || fallback[key] != null; }

    /** Renders a server message ({ code, args }). */
    function msg(m) { return m ? t(m.code, m.args) : ''; }

    function translate(rootEl) {
        Array.prototype.forEach.call(rootEl.querySelectorAll('[data-i18n]'), function (e) { e.textContent = t(e.getAttribute('data-i18n')); });
        Array.prototype.forEach.call(rootEl.querySelectorAll('[data-i18n-placeholder]'), function (e) { e.placeholder = t(e.getAttribute('data-i18n-placeholder')); });
    }

    // ---------------------------------------------------------------- helpers
    function esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function fmtSize(b) { if (!b) return '0 GB'; var gb = b / 1073741824; return gb >= 1 ? gb.toFixed(gb >= 100 ? 0 : 1) + ' GB' : Math.max(1, Math.round(b / 1048576)) + ' MB'; }
    function fmtDate(d) { if (!d) return ''; var x = new Date(d); return isNaN(x) ? '' : x.toLocaleString(current); }
    function e(key, args) { return esc(t(key, args)); }

    function template(host) {
        var app = host === 'app';
        var conn = function (svc, title, note, ph, keyHint) {
            return '<div class="cl-conn">' +
                '<h3>' + title + ' <span class="cl-opt">' + e(note) + '</span></h3>' +
                '<label>' + e('settings.url') + '<input name="' + svc + '.url" placeholder="' + ph + '"></label>' +
                '<label>' + e('settings.apiKey') + '<input name="' + svc + '.apiKey" type="password" placeholder="' + e(keyHint) + '"></label>' +
                '<button type="button" class="cl-btn cl-small cl-test" data-test="' + svc + '">' + e('settings.test') + '</button><span class="cl-test-result"></span></div>';
        };
        var check = function (name, key) { return '<label class="cl-check"><input type="checkbox" name="' + name + '"> ' + e(key) + '</label>'; };
        var modeSelect = function (name, labelKey) {
            return '<label>' + e(labelKey) + '<select name="' + name + '">' +
                ['Off', 'DryRun', 'Delete'].map(function (m) { return '<option value="' + m + '">' + e('modeOption.' + m) + '</option>'; }).join('') + '</select></label>';
        };
        var time = function (name) { return app ? '<label>' + e('settings.dailyRunAt') + '<input name="' + name + '" type="time"></label>' : ''; };
        return '' +
            '<div class="cl-banner cl-hidden" data-el="banner"></div>' +

            '<section class="cl-page" data-page="library">' +
            '  <h1 data-el="libTitle"></h1>' +
            '  <div class="cl-toolbar">' +
            '    <select data-el="filter">' +
            ['all', 'ready', 'flagged', 'watching', 'untouched', 'kept', 'unmanaged'].map(function (f) { return '<option value="' + f + '">' + e('filter.' + f) + '</option>'; }).join('') +
            '    </select>' +
            '    <input type="search" data-el="search" placeholder="' + e('library.search') + '">' +
            '    <button class="cl-btn" data-el="refresh">' + e('library.refresh') + '</button>' +
            '    <button class="cl-btn" data-el="preview">' + e('library.preview') + '</button>' +
            '    <button class="cl-btn cl-danger cl-hidden" data-el="deleteNow">' + e('library.deleteNow') + '</button>' +
            '  </div>' +
            '  <div class="cl-note" data-el="modeNote"></div>' +
            '  <div class="cl-result cl-hidden" data-el="result"></div>' +
            '  <div data-el="warnings"></div>' +
            '  <div class="cl-muted cl-summary" data-el="summary"></div>' +
            '  <div data-el="list"></div>' +
            '</section>' +

            '<section class="cl-page" data-page="history"><h1>' + e('nav.history') + '</h1><div data-el="history"></div></section>' +

            (app ? '<section class="cl-page" data-page="logs"><h1>' + e('nav.logs') + '</h1>' +
                '<div class="cl-toolbar"><button class="cl-btn" data-el="refreshLogs">' + e('library.refresh') + '</button><span class="cl-muted cl-small" data-el="logFiles"></span></div>' +
                '<p class="cl-hint">' + e('logs.hint') + '</p>' +
                '<div class="cl-log" data-el="log"></div></section>' : '') +

            '<section class="cl-page" data-page="settings"><h1>' + e('nav.settings') + '</h1>' +
            '<form class="cl-settings" data-el="form" autocomplete="off">' +
            '<h2>' + e('settings.connections') + '</h2>' +
            (app ? conn('jellyfin', 'Jellyfin', 'settings.required', 'http://192.168.1.6:8096', 'settings.jellyfinKeyHint') : '') +
            conn('sonarr', 'Sonarr', 'settings.forShows', 'http://192.168.1.6:8989', 'settings.arrKeyHint') +
            conn('radarr', 'Radarr', 'settings.forMovies', 'http://192.168.1.6:7878', 'settings.arrKeyHint') +
            '<p class="cl-hint">' + e('settings.arrHint') + '</p>' +
            (app ? '' : check('allowDirectDelete', 'settings.allowDirectDelete') + '<p class="cl-hint">' + e('settings.allowDirectDeleteHint') + '</p>') +

            '<h2>' + e('settings.cleanup') + '</h2>' +
            '<p class="cl-hint">' + e(app ? 'settings.modesHint' : 'settings.modesHintPlugin') + '</p>' +
            '<h3>' + e('nav.shows') + '</h3>' +
            modeSelect('showsMode', 'settings.mode') + time('showsScheduleTime') +
            check('deleteSeasons', 'settings.deleteSeasons') +
            check('deleteShows', 'settings.deleteShows') +
            check('requireEndedForShowDelete', 'settings.requireEnded') +
            check('protectLatestSeasonOfContinuing', 'settings.protectLatest') +
            check('includeSpecials', 'settings.includeSpecials') +
            '<h3>' + e('nav.movies') + '</h3>' +
            modeSelect('moviesMode', 'settings.mode') + time('moviesScheduleTime') +
            (app ? '<p class="cl-hint">' + e('settings.timeZoneHint') + '</p>' : '') +
            '<h3>' + e('settings.both') + '</h3>' +
            '<label>' + e('settings.maxGb') + '<input name="maxDeleteGbPerRun" type="number" min="0"><span class="cl-hint">' + e('settings.maxGbHint') + '</span></label>' +
            '<label>' + e('settings.grace') + '<input name="graceDays" type="number" min="0"></label>' +
            check('refreshJellyfinAfterCleanup', 'settings.refreshJellyfin') +
            '<label>' + e('settings.keepTag') + '<input name="keepTag"><span class="cl-hint">' + e('settings.keepTagHint') + '</span></label>' +
            check('keepFavorites', 'settings.keepFavorites') +

            '<h2>' + e('settings.droppedOff') + '</h2>' +
            '<label>' + e('settings.inactiveDays') + '<input name="inactiveDays" type="number" min="1"></label>' +
            '<label>' + e('settings.sampledMax') + '<input name="sampledMaxEpisodes" type="number" min="0"><span class="cl-hint">' + e('settings.sampledHint') + '</span></label>' +
            check('abandonedUsersBlockCleanup', 'settings.abandonedBlock') +

            '<h2>' + e('settings.users') + '</h2><p class="cl-hint">' + e('settings.usersHint') + '</p><div data-el="users" class="cl-users"></div>' +

            // The plugin follows the Jellyfin user's display language, so only the app has a Language setting.
            (app ? '<h2>' + e('settings.general') + '</h2>' +
                '<label>' + e('settings.language') + '<select name="language"><option value="">' + e('settings.languageAuto') + '</option>' +
                Object.keys(LANGUAGES).map(function (c) { return '<option value="' + c + '">' + esc(LANGUAGES[c]) + '</option>'; }).join('') + '</select></label>' +
                check('debugLogging', 'settings.debugLogging') + '<span class="cl-hint">' + e('settings.debugLoggingHint') + '</span>' : '') +

            (app ? '<h2>' + e('settings.security') + '</h2><p class="cl-hint" data-el="pwState"></p>' +
                '<label>' + e('settings.newPassword') + '<input data-el="newPassword" type="password" autocomplete="new-password" placeholder="' + e('settings.newPasswordPlaceholder') + '"></label>' +
                '<label class="cl-check cl-hidden" data-el="clearPwRow"><input type="checkbox" data-el="clearPassword"> ' + e('settings.removePassword') + '</label>' +
                '<label>' + e('settings.apiKey') + ' <span class="cl-hint">' + e('settings.apiKeyHint') + '</span><input data-el="apiKey" readonly></label>' +

                '<h2>HTTPS</h2>' +
                '<p class="cl-hint">' + e('https.hint') + '</p>' +
                '<p class="cl-hint" data-el="httpsState"></p>' +
                check('https.enabled', 'https.enable') +
                '<label>' + e('https.port') + '<input name="https.port" type="number" min="1" max="65535"><span class="cl-hint">' + e('https.portHint') + '</span></label>' +
                '<label>' + e('https.certificate') + '<input name="https.certificatePath" placeholder="' + e('https.certificatePlaceholder') + '"><span class="cl-hint">' + e('https.certificateHint') + '</span></label>' +
                '<label>' + e('https.key') + '<input name="https.keyPath" placeholder="/certs/privkey.pem"><span class="cl-hint">' + e('https.keyHint') + '</span></label>' +
                '<label>' + e('https.password') + '<input name="https.certificatePassword" type="password" autocomplete="off"><span class="cl-hint">' + e('https.passwordHint') + '</span></label>' +
                '<button type="button" class="cl-btn cl-small" data-el="checkCert">' + e('https.check') + '</button><span data-el="certResult" class="cl-test-result"></span>' +
                '<div data-el="restartRow" class="cl-banner cl-hidden cl-restart-row">' + e('https.restartNeeded') + ' <button type="button" class="cl-btn cl-small cl-primary" data-el="restart">' + e('https.restart') + '</button></div>'
                : '') +

            '<div class="cl-save-row"><button type="submit" class="cl-btn cl-primary">' + e('settings.save') + '</button><span data-el="saveMsg"></span></div>' +
            '<h3>' + e('backup.title') + '</h3>' +
            '<p class="cl-hint">' + e('backup.hint') + '</p>' +
            '<button type="button" class="cl-btn cl-small" data-el="exportBackup">' + e('backup.export') + '</button> ' +
            '<button type="button" class="cl-btn cl-small" data-el="importBackup">' + e('backup.import') + '</button>' +
            '<input type="file" accept=".json,application/json" data-el="backupFile" class="cl-hidden">' +
            '<span data-el="backupMsg" class="cl-test-result"></span>' +
            '</form></section>';
    }

    function mount(root, opts) {
        var host = opts.host || 'app';
        var api = opts.api;
        var state = { status: null, report: null, page: 'shows', open: {}, settings: null };
        root.classList.add('clearr');
        root.setAttribute('data-host', host);
        var el = function (name) { return root.querySelector('[data-el="' + name + '"]'); };
        var all = function (sel) { return Array.prototype.slice.call(root.querySelectorAll(sel)); };
        var form;

        // The language comes from the settings (via status), so status loads first, then the UI is drawn.
        var ready = api('status').then(function (s) {
            state.status = s;
            return loadLanguage(s.language, opts.language);
        }).then(function () {
            root.innerHTML = template(host);
            form = el('form');
            wire();
            renderStatus();
            if (opts.onStatus) opts.onStatus(state.status);
            if (opts.onReady) opts.onReady();
        });

        // ------------------------------------------------------------ status
        function loadStatus() {
            return api('status').then(function (s) { state.status = s; renderStatus(); if (opts.onStatus) opts.onStatus(s); return s; });
        }

        function kind() { return state.page === 'movies' ? 'movies' : 'shows'; }
        function pageMode() { return state.status[kind() + 'Mode']; }

        function renderStatus() {
            var s = state.status;
            var banner = el('banner');
            if (!s.configured) {
                banner.innerHTML = e(host === 'app' ? 'banner.welcomeApp' : 'banner.welcomePlugin') + ' <a href="#" data-goto="settings">' + e('banner.openSettings') + '</a>';
                banner.classList.remove('cl-hidden');
            } else if (s.notice) {
                banner.textContent = msg(s.notice);
                banner.classList.remove('cl-hidden');
            } else banner.classList.add('cl-hidden');
            var mode = pageMode();
            el('deleteNow').classList.toggle('cl-hidden', mode !== 'Delete');
            el('modeNote').innerHTML = '<span class="cl-mode cl-mode-' + esc(mode) + '">' + e('modeFor.' + kind(), { mode: t('modeName.' + mode) }) + '</span> ' +
                e(mode === 'Delete' ? 'modeNote.delete' : 'modeNote.safe', { deleteNow: t('library.deleteNow'), preview: t('library.preview'), keep: t('keep.keep') });
        }

        root.addEventListener('click', function (ev) {
            var g = ev.target.closest && ev.target.closest('[data-goto]');
            if (g) { ev.preventDefault(); show(g.getAttribute('data-goto')); if (opts.onNavigate) opts.onNavigate(g.getAttribute('data-goto')); }
        });

        // ------------------------------------------------------------ pages
        function show(page) {
            var pages = host === 'app' ? ['shows', 'movies', 'history', 'settings', 'logs'] : ['shows', 'movies', 'history', 'settings'];
            if (pages.indexOf(page) < 0) page = 'shows';
            state.page = page;
            return ready.then(function () {
                all('.cl-page').forEach(function (p) {
                    var name = p.getAttribute('data-page');
                    p.classList.toggle('cl-hidden', !(name === page || (name === 'library' && (page === 'shows' || page === 'movies'))));
                });
                if (page === 'shows' || page === 'movies') {
                    el('libTitle').textContent = t('nav.' + page);
                    el('result').classList.add('cl-hidden');
                    renderStatus();
                    if (state.report) renderLibrary(); else loadReport();
                }
                if (page === 'history') loadHistory();
                if (page === 'logs') loadLogs();
                if (page === 'settings') loadSettings();
            });
        }

        // ------------------------------------------------------------ library
        function loadReport() {
            if (!state.status.configured) { el('summary').textContent = ''; el('list').innerHTML = ''; return Promise.resolve(); }
            el('summary').textContent = t('common.loading');
            return api('report').then(function (r) { state.report = r; renderLibrary(); })
                .catch(function (err) { el('summary').textContent = ''; el('list').innerHTML = '<div class="cl-warn">' + esc(err.message) + '</div>'; });
        }

        function flagLabel(f) { return t(f === 'MidEpisode' && state.page === 'movies' ? 'flag.MidMovie' : 'flag.' + f); }
        function reason(x) { return x.reasonMsg ? msg(x.reasonMsg) : x.reason; }

        function chip(u, counts) {
            var cls = 'cl-chip cl-st-' + u.status + (u.flags.length ? ' cl-flagged' : '');
            var text = esc(u.userName);
            if (u.status === 'Finished') text += ' ✓';
            else if (u.status === 'InProgress' && counts) text += ' ' + u.watched + '/' + u.total;
            if (u.flags.length) text += ' · ' + esc(u.flags.map(flagLabel).join(', '));
            var title = t('status.' + u.status) + (u.lastEpisode ? ' — ' + t('chip.last', { episode: u.lastEpisode }) : '') + (u.lastActivity ? ' ' + t('chip.on', { date: fmtDate(u.lastActivity) }) : '');
            return '<span class="' + cls + '" title="' + esc(title) + '">' + text + '</span>';
        }

        function chips(users, counts) {
            var c = users.filter(function (u) { return u.status !== 'NotStarted'; }).map(function (u) { return chip(u, counts); }).join('');
            return c || '<span class="cl-muted cl-small">' + e('chip.nobodyStarted') + '</span>';
        }

        function keepBtn(id, k, kept, keptBy) {
            var external = kept && keptBy && keptBy !== 'Clearr' && keptBy !== 'Sonarr tag' && keptBy !== 'Radarr tag';
            var by = !keptBy ? '' : has('keptBy.' + keptBy) ? t('keptBy.' + keptBy) : keptBy;
            return '<button type="button" class="cl-keep' + (kept ? ' cl-on' : '') + '" data-id="' + esc(id) + '" data-kind="' + k + '" data-keep="' + (!kept) + '"' +
                (keptBy === 'Favourite' ? ' disabled title="' + e('keep.favourite') + '"' : external ? ' disabled title="' + e('keep.external', { by: by }) + '"' : ' title="' + (kept ? e('keep.keptTitle', { by: by }) : e('keep.keepTitle')) + '"') +
                '>' + (kept ? e('keep.kept') + ' ✓' : e('keep.keep')) + '</button>';
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
            el('filter').options[6].textContent = t(shows ? 'filter.unmanagedShows' : 'filter.unmanagedMovies');

            el('warnings').innerHTML = r.warnings.map(function (w) { return '<div class="cl-warn">' + esc(w) + '</div>'; }).join('') +
                (shows && !r.sonarrConnected && !r.directDelete ? '<div class="cl-warn">' + e('library.noSonarr') + '</div>' : '') +
                (!shows && !r.radarrConnected && !r.directDelete ? '<div class="cl-warn">' + e('library.noRadarr') + '</div>' : '');

            var html, users = r.trackedUsers.join(', ');
            if (shows) {
                var readyShows = r.shows.filter(function (s) { return s.eligible; }).length;
                var readySeasons = r.shows.reduce(function (n, s) { return n + (s.eligible ? 0 : s.seasons.filter(function (x) { return x.eligible; }).length); }, 0);
                el('summary').textContent = t('library.summaryShows', { count: r.shows.length, users: users, shows: readyShows, seasons: readySeasons });
                html = r.shows.filter(function (s) {
                    var flagged = s.users.some(function (u) { return u.flags.length; }) || s.seasons.some(function (x) { return x.users.some(function (u) { return u.flags.length; }); });
                    return matches(s, s.users, s.eligible || s.seasons.some(function (x) { return x.eligible; }), flagged, filter, q);
                }).map(showCard).join('');
            } else {
                var ready = r.movies.filter(function (m) { return m.eligible; }).length;
                el('summary').textContent = t('library.summaryMovies', { count: r.movies.length, users: users, ready: ready });
                html = r.movies.filter(function (m) {
                    return matches(m, m.users, m.eligible, m.users.some(function (u) { return u.flags.length; }), filter, q);
                }).map(movieCard).join('');
            }

            el('list').innerHTML = html || '<p class="cl-muted">' + e('library.nothingMatches') + '</p>';
            bindList();
        }

        function viaPill(item, arr) {
            if (item.via === 'Jellyfin') return '<span class="cl-pill cl-pill-unmanaged" title="' + e('pill.viaJellyfinTitle', { arr: arr }) + '">' + e('pill.viaJellyfin') + '</span>';
            if (!item.managed) return '<span class="cl-pill cl-pill-unmanaged" title="' + e('pill.notInArrTitle', { arr: arr }) + '">' + e('pill.notInArr', { arr: arr }) + '</span>';
            return '';
        }

        function showCard(s) {
            var readySeasons = s.seasons.filter(function (x) { return x.eligible; }).length;
            var pill = s.eligible ? '<span class="cl-pill cl-pill-ready">' + e('pill.showReady') + '</span>'
                : readySeasons ? '<span class="cl-pill cl-pill-partial">' + e(readySeasons > 1 ? 'pill.seasonsReady' : 'pill.seasonReady', { count: readySeasons }) + '</span>' : '';
            var rows = s.seasons.map(function (x) {
                return '<tr><td>' + esc(x.name) + '<div class="cl-muted cl-small">' + e('library.episodes', { count: x.episodeCount }) + '</div></td>' +
                    '<td><div class="cl-chips">' + chips(x.users, true) + '</div></td>' +
                    '<td class="cl-reason' + (x.eligible ? ' cl-ok' : '') + '">' + (x.eligible ? '✓ ' : '') + esc(reason(x)) + '</td>' +
                    '<td>' + (s.kept ? '<span class="cl-muted cl-small">' + e('keep.showKept') + '</span>' : keepBtn(x.seasonId, 'season', x.kept, x.keptBy)) + '</td></tr>';
            }).join('');
            return '<div class="cl-card' + (state.open[s.showId] ? ' cl-open' : '') + '" data-id="' + esc(s.showId) + '">' +
                '<div class="cl-card-head"><div class="cl-name">' + esc(s.name) + '<span class="cl-sub">' + esc(!s.status ? t('showStatus.unknown') : has('showStatus.' + s.status) ? t('showStatus.' + s.status) : s.status) + ' · ' + e('library.episodes', { count: s.episodeCount }) + '</span></div>' +
                '<div class="cl-chips">' + chips(s.users, true) + '</div>' + viaPill(s, 'Sonarr') + pill + keepBtn(s.showId, 'show', s.kept, s.keptBy) + '</div>' +
                '<div class="cl-card-body"><p class="cl-reason' + (s.eligible ? ' cl-ok' : '') + '"><b>' + e('library.wholeShow') + '</b> ' + esc(reason(s)) + '</p>' +
                '<table class="cl-table"><thead><tr><th>' + e('library.season') + '</th><th>' + e('library.viewers') + '</th><th>' + e('library.cleanup') + '</th><th></th></tr></thead><tbody>' + rows + '</tbody></table></div></div>';
        }

        function movieCard(m) {
            var pill = m.eligible ? '<span class="cl-pill cl-pill-ready">' + e('pill.ready') + '</span>' : '';
            return '<div class="cl-card' + (state.open[m.movieId] ? ' cl-open' : '') + '" data-id="' + esc(m.movieId) + '">' +
                '<div class="cl-card-head"><div class="cl-name">' + esc(m.name) + (m.year ? '<span class="cl-sub">' + esc(m.year) + '</span>' : '') + '</div>' +
                '<div class="cl-chips">' + chips(m.users, false) + '</div>' + viaPill(m, 'Radarr') + pill + keepBtn(m.movieId, 'movie', m.kept, m.keptBy) + '</div>' +
                '<div class="cl-card-body"><p class="cl-reason' + (m.eligible ? ' cl-ok' : '') + '">' + (m.eligible ? '✓ ' : '') + esc(reason(m)) + '</p></div></div>';
        }

        function bindList() {
            all('[data-el="list"] .cl-keep').forEach(function (b) {
                b.addEventListener('click', function (ev) {
                    ev.stopPropagation();
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

        function what(a) { return a.title + (a.kind === 'Season' ? ' – ' + a.seasonName : a.kind === 'Show' ? ' ' + t('line.wholeShow') : ''); }
        function line(a) { return esc(what(a)) + ' · ' + fmtSize(a.bytes); }
        function detail(a) { return a.parts && a.parts.length ? a.parts.map(msg).join(' · ') : a.detail; }
        function actionLabel(a) { return t('action.' + a.action); }

        function showResult(res) {
            var box = el('result');
            box.classList.remove('cl-hidden');
            if (res.error) { box.innerHTML = '<div class="cl-error">' + esc(res.errorMsg ? msg(res.errorMsg) : res.error) + '</div>'; return; }
            var main = res.actions.filter(function (a) { return a.action === 'WouldDelete' || a.action === 'Deleted' || a.action === 'Failed'; });
            var skipped = res.actions.filter(function (a) { return a.action === 'Skipped'; });
            if (!main.length && !skipped.length) { box.textContent = t('result.nothing'); return; }
            box.innerHTML = '<b>' + e(res.dryRun ? 'result.wouldRemove' : 'result.result', { count: main.length, size: fmtSize(res.totalBytes) }) + '</b><ul>' +
                main.map(function (a) { return '<li>' + (res.dryRun ? '' : '<b class="cl-a-' + esc(a.action) + '">' + esc(actionLabel(a)) + '</b> ') + line(a) + ' <span class="cl-muted cl-small">' + esc(detail(a)) + '</span></li>'; }).join('') + '</ul>' +
                (skipped.length ? '<div class="cl-muted cl-small">' + e('result.heldBack', { count: skipped.length }) + '</div><ul class="cl-muted cl-small">' + skipped.map(function (a) { return '<li>' + line(a) + ' — ' + esc(detail(a)) + '</li>'; }).join('') + '</ul>' : '');
        }

        function wire() {
            el('filter').addEventListener('change', renderLibrary);
            el('search').addEventListener('input', renderLibrary);
            el('refresh').addEventListener('click', function () { loadStatus().then(loadReport); });
            el('preview').addEventListener('click', function () {
                var b = this; b.disabled = true;
                api('cleanup?dryRun=true&scope=' + kind(), 'POST').then(showResult).catch(function (err) { showResult(err.body || { error: err.message }); }).then(function () { b.disabled = false; });
            });
            el('deleteNow').addEventListener('click', function () {
                if (pageMode() !== 'Delete') return;
                var b = this, scope = kind(); b.disabled = true;
                api('cleanup?dryRun=true&scope=' + scope, 'POST').then(function (p) {
                    showResult(p);
                    var items = p.actions.filter(function (a) { return a.action === 'WouldDelete'; });
                    if (!items.length) return null;
                    var list = items.slice(0, 25).map(function (a) { return '• ' + what(a) + ' · ' + fmtSize(a.bytes); }).join('\n');
                    if (items.length > 25) list += '\n' + t('confirm.more', { count: items.length - 25 });
                    if (!confirm(t('confirm.title', { count: items.length, size: fmtSize(p.totalBytes) }) + '\n\n' + list + '\n\n' + t('confirm.cannotUndo'))) return null;
                    return api('cleanup?dryRun=false&scope=' + scope, 'POST').then(function (res) { showResult(res); return loadReport(); });
                }).catch(function (err) { showResult(err.body || { error: err.message }); }).then(function () { b.disabled = false; });
            });

            if (host === 'app') el('refreshLogs').addEventListener('click', loadLogs);

            all('.cl-test').forEach(function (b) {
                b.addEventListener('click', function () {
                    var svc = b.getAttribute('data-test'), out = b.nextElementSibling;
                    out.textContent = t('settings.testing'); out.className = 'cl-test-result';
                    api('test/' + svc, 'POST', { url: form.elements[svc + '.url'].value.trim(), apiKey: form.elements[svc + '.apiKey'].value.trim() })
                        .then(function (r) { out.textContent = r.message; out.className = 'cl-test-result ' + (r.ok ? 'cl-ok' : 'cl-error'); })
                        .catch(function (err) { out.textContent = err.message; out.className = 'cl-test-result cl-error'; });
                });
            });

            if (host === 'app') {
                el('checkCert').addEventListener('click', function () {
                    var out = el('certResult'); out.textContent = t('https.checking'); out.className = 'cl-test-result';
                    api('https/check', 'POST', {
                        enabled: true, port: parseInt(form.elements['https.port'].value, 10) || 8586,
                        certificatePath: form.elements['https.certificatePath'].value.trim(), keyPath: form.elements['https.keyPath'].value.trim(),
                        certificatePassword: form.elements['https.certificatePassword'].value
                    }).then(function (r) { out.textContent = r.message; out.className = 'cl-test-result ' + (r.ok ? 'cl-ok' : 'cl-error'); })
                      .catch(function (err) { out.textContent = err.message; out.className = 'cl-test-result cl-error'; });
                });
                el('restart').addEventListener('click', function () {
                    var b = this; b.disabled = true; b.textContent = t('https.restarting');
                    api('restart', 'POST').catch(function () {}).then(function () {
                        var tries = 0;
                        (function wait() {
                            setTimeout(function () {
                                fetch('ping', { cache: 'no-store' }).then(function (r) { if (r.ok) location.reload(); else throw 0; })
                                    .catch(function () { if (++tries < 60) wait(); else b.textContent = t('https.didntComeBack'); });
                            }, 1500);
                        })();
                    });
                });
            }

            el('exportBackup').addEventListener('click', function () {
                api('backup').then(function (data) {
                    var blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
                    var a = document.createElement('a');
                    a.href = URL.createObjectURL(blob);
                    a.download = 'clearr-backup-' + new Date().toISOString().slice(0, 10) + '.json';
                    document.body.appendChild(a); a.click(); a.remove();
                    setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
                }).catch(function (err) { var m = el('backupMsg'); m.textContent = err.message; m.className = 'cl-test-result cl-error'; });
            });
            el('importBackup').addEventListener('click', function () { el('backupFile').click(); });
            el('backupFile').addEventListener('change', function () {
                var file = this.files && this.files[0];
                var m = el('backupMsg');
                this.value = '';
                if (!file) return;
                if (!confirm(t('backup.confirm'))) return;
                file.text().then(function (text) {
                    var data;
                    try { data = JSON.parse(text); } catch (x) { throw new Error(t('backup.notBackup'), { cause: x }); }
                    return api('backup/restore', 'POST', data);
                }).then(function () {
                    m.textContent = t('backup.restored'); m.className = 'cl-test-result cl-ok';
                    state.report = null;
                    return loadStatus().then(loadSettings);
                }).catch(function (err) {
                    var b = err.body && err.body.error;
                    m.textContent = t('backup.failed') + ' ' + (b === "This isn't a Clearr backup file." ? t('backup.notBackup') : (b || err.message));
                    m.className = 'cl-test-result cl-error';
                });
            });

            form.addEventListener('submit', function (ev) {
                ev.preventDefault();
                var s = JSON.parse(JSON.stringify(state.settings || {}));
                all('[name]').forEach(function (f) {
                    var v = f.type === 'checkbox' ? f.checked : f.type === 'number' ? (parseInt(f.value, 10) || 0) : f.value.trim();
                    setPath(s, f.name, v);
                });
                s.excludedUserIds = all('.cl-exclude:checked').map(function (x) { return x.value; });
                var body = { settings: s };
                if (host === 'app') { body.newPassword = el('newPassword').value; body.clearPassword = el('clearPassword').checked; }
                var out = el('saveMsg');
                var languageChanged = (state.settings || {}).language !== s.language;
                api('settings', 'PUT', body).then(function () {
                    if (languageChanged) { location.reload(); return null; }
                    out.textContent = t('settings.saved'); out.className = 'cl-ok';
                    setTimeout(function () { out.textContent = ''; }, 3000);
                    state.report = null;
                    return loadStatus().then(loadSettings);
                }).catch(function (err) { out.textContent = err.message; out.className = 'cl-error'; });
            });
        }

        // ------------------------------------------------------------ history
        function loadHistory() {
            api('history').then(function (list) {
                if (!list.length) { el('history').innerHTML = '<p class="cl-muted">' + e('history.empty') + '</p>'; return; }
                el('history').innerHTML = '<table class="cl-table"><thead><tr><th>' + e('history.when') + '</th><th>' + e('history.action') + '</th><th>' + e('history.what') + '</th><th>' + e('history.size') + '</th><th>' + e('history.detail') + '</th></tr></thead><tbody>' +
                    list.map(function (h) {
                        var label = h.action === 'WouldDelete' ? t('action.WouldDeleteHistory') : actionLabel(h);
                        return '<tr><td>' + esc(fmtDate(h.date)) + '</td><td class="cl-a-' + esc(h.action) + '">' + esc(label) + '</td><td>' + esc(what(h)) + '</td><td>' + (h.bytes ? fmtSize(h.bytes) : '') + '</td><td class="cl-reason">' + esc(detail(h)) + '</td></tr>';
                    }).join('') + '</tbody></table>';
            }).catch(function (err) { el('history').textContent = err.message; });
        }

        // ------------------------------------------------------------ logs (app only)
        function loadLogs() {
            api('logs?lines=1000').then(function (r) {
                el('logFiles').innerHTML = r.files.length ? e('logs.download') + ' ' + r.files.map(function (f) {
                    return '<a href="api/logs/' + encodeURIComponent(f.name) + '" download>' + esc(f.name) + '</a> (' + fmtSize(f.size) + ')';
                }).join(' · ') : '';
                el('log').innerHTML = r.lines.length ? r.lines.map(function (l) {
                    var p = l.split('|');
                    if (p.length < 4) return '<div class="cl-log-more">' + esc(l) + '</div>';
                    return '<div class="cl-log-' + esc(p[1]) + '"><span class="cl-log-time">' + esc(p[0]) + '</span> <b>' + esc(p[1]) + '</b> <span class="cl-muted">' + esc(p[2]) + '</span> ' + esc(p.slice(3).join('|')) + '</div>';
                }).join('') : '<p class="cl-muted">' + e('logs.empty') + '</p>';
            }).catch(function (err) { el('log').textContent = err.message; });
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
                    el('pwState').textContent = t(r.hasPassword ? 'settings.passwordSet' : 'settings.noPassword');
                    el('clearPwRow').classList.toggle('cl-hidden', !r.hasPassword);
                    el('newPassword').value = ''; el('clearPassword').checked = false;
                    renderHttps();
                }
                loadUsers(s.excludedUserIds || []);
            });
        }

        function loadUsers(excluded) {
            el('users').innerHTML = '<span class="cl-muted cl-small">' + e('settings.loadingUsers') + '</span>';
            api('users').then(function (users) {
                el('users').innerHTML = users.length ? users.map(function (u) {
                    return '<label class="cl-check"><input type="checkbox" class="cl-exclude" value="' + esc(u.id) + '"' + (excluded.indexOf(u.id) >= 0 ? ' checked' : '') + '> ' + e('settings.ignoreUser', { name: u.name }) + '</label>';
                }).join('') : '<span class="cl-muted cl-small">' + e('settings.noUsers') + '</span>';
            }).catch(function (err) { el('users').innerHTML = '<span class="cl-error">' + esc(err.message) + '</span>'; });
        }

        function renderHttps() {
            var h = state.status.https || {};
            el('httpsState').innerHTML = h.active
                ? '<span class="cl-ok">' + e('https.on', { port: h.port, certificate: h.certificate }) + '</span>'
                : h.error ? '<span class="cl-error">' + esc(h.error) + '</span>' : e('https.off');
            el('restartRow').classList.toggle('cl-hidden', !h.restartNeeded);
        }

        return { show: show, refreshStatus: loadStatus, ready: ready };
    }

    window.ClearrUI = { mount: mount, fmtDate: fmtDate, t: t, msg: msg, translate: translate, loadLanguage: loadLanguage, languages: LANGUAGES };
})();
