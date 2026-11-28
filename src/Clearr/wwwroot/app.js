// Clearr app shell: sign-in, sidebar and status. Everything else is the shared ClearrUI (also used by the plugin).
(function () {
    'use strict';
    var $ = function (s) { return document.querySelector(s); };
    var ui = null;

    function api(path, method, body) {
        return fetch('api/' + path, {
            method: method || 'GET',
            credentials: 'same-origin',
            headers: body !== undefined ? { 'Content-Type': 'application/json', 'X-Clearr-Request': '1' } : { 'X-Clearr-Request': '1' },
            body: body !== undefined ? JSON.stringify(body) : undefined
        }).then(function (r) {
            if (r.status === 401 && path !== 'login') { showLogin(); }
            return r.json().catch(function () { return {}; }).then(function (j) {
                if (!r.ok) { var e = new Error(j.error || ('HTTP ' + r.status)); e.status = r.status; e.body = j; throw e; }
                return j;
            });
        });
    }

    function showLogin() { $('#app').classList.add('hidden'); $('#login').classList.remove('hidden'); $('#loginPassword').focus(); }

    $('#loginForm').addEventListener('submit', function (e) {
        e.preventDefault();
        api('login', 'POST', { password: $('#loginPassword').value }).then(function () {
            $('#loginPassword').value = ''; $('#loginError').textContent = ''; location.reload();
        }).catch(function (err) { $('#loginError').textContent = ClearrUI.t(err.status === 429 ? 'login.locked' : 'login.wrong'); });
    });

    $('#logout').addEventListener('click', function (e) { e.preventDefault(); api('logout', 'POST').then(function () { location.reload(); }); });

    function onStatus(s) {
        var t = ClearrUI.t;
        $('#version').textContent = s.version;
        // One badge per kind (shows, movies), each with its own mode and next run.
        $('#statusModes').innerHTML = '';
        [['shows', s.showsMode, s.nextShowsRun], ['movies', s.moviesMode, s.nextMoviesRun]].forEach(function (k) {
            var badge = document.createElement('div');
            badge.className = 'mode-badge mode-' + k[1];
            badge.textContent = t('modeFor.' + k[0], { mode: t('modeName.' + k[1]) });
            var next = document.createElement('div');
            next.className = 'muted small';
            next.textContent = k[2] ? t('shell.nextRun', { kind: t('nav.' + k[0]), when: ClearrUI.fmtDate(k[2]) }) : t('shell.runOff', { kind: t('nav.' + k[0]) });
            $('#statusModes').appendChild(badge);
            $('#statusModes').appendChild(next);
        });
        $('#statusNext').textContent = '';
        $('#statusLast').textContent = s.lastRunSummary ? t('shell.last', { summary: ClearrUI.msg(s.lastRunSummary) }) : '';
        $('#logout').classList.toggle('hidden', !s.passwordRequired);
    }

    function route() {
        var page = (location.hash || '#shows').slice(1);
        Array.prototype.forEach.call(document.querySelectorAll('.nav-link'), function (a) { a.classList.toggle('active', a.getAttribute('data-page') === page); });
        ui.show(page);
    }

    api('status').then(function (s) {
        return ClearrUI.loadLanguage(s.language).then(function () { ClearrUI.translate(document); return s; });
    }).then(function (s) {
        if (s.passwordRequired && !s.authenticated) { showLogin(); return; }
        $('#app').classList.remove('hidden');
        ui = ClearrUI.mount($('#root'), { host: 'app', api: api, onStatus: onStatus, onNavigate: function (p) { location.hash = p; } });
        window.addEventListener('hashchange', route);
        route();
    });

    // Optional support link: set "githubSponsors" (a GitHub username) in support.json to show it.
    fetch('support.json', { cache: 'no-store' }).then(function (r) { return r.ok ? r.json() : {}; }).then(function (j) {
        if (j && j.githubSponsors) { var a = $('#sponsor'); a.href = 'https://github.com/sponsors/' + encodeURIComponent(j.githubSponsors); a.classList.remove('hidden'); }
    }).catch(function () {});
})();
