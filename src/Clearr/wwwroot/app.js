// Clearr app shell: sign-in, sidebar and status. Everything else is the shared ClearrUI (also used by the plugin).
(function () {
    'use strict';
    var $ = function (s) { return document.querySelector(s); };
    var ui = null;

    function api(path, method, body) {
        return fetch('api/' + path, {
            method: method || 'GET',
            credentials: 'same-origin',
            headers: body !== undefined ? { 'Content-Type': 'application/json' } : {},
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
        }).catch(function (err) { $('#loginError').textContent = err.message; });
    });

    $('#logout').addEventListener('click', function (e) { e.preventDefault(); api('logout', 'POST').then(function () { location.reload(); }); });

    function onStatus(s) {
        $('#version').textContent = s.version;
        $('#statusMode').textContent = { Off: 'Off', DryRun: 'Dry run', Delete: 'Delete mode' }[s.mode] || s.mode;
        $('#statusMode').className = 'mode-badge mode-' + s.mode;
        $('#statusNext').textContent = s.mode === 'Off' ? 'Daily run off' : 'Next run ' + ClearrUI.fmtDate(s.nextRun);
        $('#statusLast').textContent = s.lastRunSummary ? 'Last: ' + s.lastRunSummary : '';
        $('#logout').classList.toggle('hidden', !s.passwordRequired);
    }

    function route() {
        var page = (location.hash || '#shows').slice(1);
        Array.prototype.forEach.call(document.querySelectorAll('.nav-link'), function (a) { a.classList.toggle('active', a.getAttribute('data-page') === page); });
        ui.show(page);
    }

    api('status').then(function (s) {
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
