namespace CraftConsole.Web.Services;

/// <summary>
/// Self-contained login/first-run-setup page. Served directly by the auth
/// middleware for any unauthenticated GET, independent of wwwroot's static
/// files (which are themselves behind the auth gate).
/// </summary>
public static class LoginPage
{
    public static string Render(bool configured)
    {
        var title = configured ? "Sign in" : "Set up";
        var subtitle = configured ? "Sign in to continue" : "Set a password to secure this panel";
        var label = configured ? "Password" : "New password";
        var autocomplete = configured ? "current-password" : "new-password";
        var buttonLabel = configured ? "Sign in" : "Create password";
        var endpoint = configured ? "/api/auth/login" : "/api/auth/setup";
        var configuredJs = configured ? "true" : "false";
        var passwordAutofocus = configured ? "" : " autofocus";

        var usernameField = configured ? """
            <label for="un">Username</label>
            <input id="un" type="text" autocomplete="username" autofocus value="admin" required>
            """ : "";

        var confirmField = configured ? "" : """
            <label for="pw2">Confirm password</label>
            <input id="pw2" type="password" autocomplete="new-password" minlength="8" required>
            """;

        var hint = configured ? "" : """
            <div class="hint">This protects command execution on this panel. The password is stored only on this machine — there is no recovery; delete <code>auth.json</code> in the app data folder to reset it.</div>
            """;

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>CraftConsole — {{title}}</title>
        <style>
          @font-face { font-family:'JBMono'; font-style:normal; font-weight:400; font-display:swap; src:url('/fonts/jbmono-400.woff2') format('woff2'); }
          @font-face { font-family:'JBMono'; font-style:normal; font-weight:700; font-display:swap; src:url('/fonts/jbmono-700.woff2') format('woff2'); }
          @font-face { font-family:'Plex'; font-style:normal; font-weight:400; font-display:swap; src:url('/fonts/plex-400.woff2') format('woff2'); }
          :root { --sheet:#0a1e33; --sheet-2:#0d2740; --sheet-3:#12304c;
                  --n-200:#1d3f5e; --n-300:#2c5478; --n-500:#7396b4; --n-600:#9db9d1; --n-800:#d8e6f2; --n-900:#ecf4fb;
                  --blue:#ffb000; --blue-wash:#3a2a06; --grid:#16395c; --bad:#ff6f61; }
          * { box-sizing:border-box; }
          html,body { margin:0; height:100%; background:var(--sheet); color:var(--n-900);
                      font-family:'JBMono',ui-monospace,Consolas,monospace;
                      -webkit-font-smoothing:antialiased; }
          /* The sheet itself: the same ruled grid the panel draws on. */
          body { display:flex; align-items:center; justify-content:center; padding:20px;
                 background-image:linear-gradient(to right, var(--grid) 1px, transparent 1px),
                                  linear-gradient(to bottom, var(--grid) 1px, transparent 1px);
                 background-size:16px 16px; }
          ::selection { background: var(--blue-wash); color: var(--n-900); }
          :focus-visible { outline: 2px solid var(--blue); outline-offset: 2px; }
          /* Corner ticks mark the card as a drawn part, matching the panel. */
          .card { position:relative; width:100%; max-width:344px; background:var(--sheet);
                  border:1px solid var(--n-200); border-radius:2px; padding:28px 26px; }
          .card::before, .card::after { content:''; position:absolute; width:7px; height:7px;
                                        border-color:var(--n-300); border-style:solid; }
          .card::before { top:-1px; left:-1px; border-width:1px 0 0 1px; }
          .card::after  { bottom:-1px; right:-1px; border-width:0 1px 1px 0; }
          .brand { display:flex; align-items:center; gap:10px; margin-bottom:24px; }
          /* Isometric cube, like a part in an exploded view. */
          .mark { width:26px; height:26px; flex-shrink:0;
                  background:no-repeat center/contain url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23ffb000' stroke-width='1.5' stroke-linejoin='round'%3E%3Cpath d='M12 2.5 20.5 7.25v9.5L12 21.5 3.5 16.75v-9.5Z'/%3E%3Cpath d='M12 12 20.5 7.25'/%3E%3Cpath d='M12 12 3.5 7.25'/%3E%3Cpath d='M12 12v9.5'/%3E%3C/svg%3E"); }
          h1 { font-size:14px; margin:0; font-weight:600; letter-spacing:-.02em; }
          /* Prose sets in Plex; everything structural stays mono. */
          p.sub { margin:2px 0 0; font-size:12px; color:var(--n-500); font-family:'Plex',system-ui,sans-serif; }
          label { display:block; font-family:'JBMono',ui-monospace,monospace; font-size:10.5px;
                  letter-spacing:.1em; text-transform:uppercase; color:var(--n-500); margin:16px 0 4px; }
          input { width:100%; padding:6px 9px; background:var(--sheet); border:1px solid var(--n-200);
                  border-radius:2px; color:var(--n-900); font-family:'JBMono',ui-monospace,monospace;
                  font-size:12.5px; transition:border-color .12s, box-shadow .12s; }
          input:focus { outline:none; border-color:var(--blue); box-shadow:0 0 0 3px var(--blue-wash); }
          button { width:100%; margin-top:22px; padding:8px; border:1px solid var(--n-900); border-radius:2px;
                   background:var(--n-900); color:var(--sheet); font-family:inherit; font-weight:500;
                   font-size:12.5px; cursor:pointer; transition: background .12s, border-color .12s; }
          button:hover:not(:disabled) { background:var(--n-800); border-color:var(--n-800); }
          button:disabled { opacity:.45; cursor:not-allowed; }
          .err { display:none; margin-top:14px; padding:7px 9px; background:var(--sheet);
                 border:1px solid var(--bad); border-radius:2px; color:var(--bad); font-size:12px; }
          .hint { margin-top:14px; font-size:11px; color:var(--n-500); line-height:1.55; font-family:'Plex',system-ui,sans-serif; }
          .hint code { color:var(--n-600); font-family:'JBMono',ui-monospace,monospace; }
        </style>
        </head>
        <body>
          <form class="card" id="f">
            <div class="brand">
              <div class="mark"></div>
              <div><h1>CraftConsole</h1><p class="sub">{{subtitle}}</p></div>
            </div>
            {{usernameField}}
            <label for="pw">{{label}}</label>
            <input id="pw" type="password" autocomplete="{{autocomplete}}"{{passwordAutofocus}} minlength="8" required>
            {{confirmField}}
            <button type="submit">{{buttonLabel}}</button>
            <div class="err" id="err"></div>
            {{hint}}
          </form>
        <script>
          var configured = {{configuredJs}};
          document.getElementById('f').addEventListener('submit', function (e) {
            e.preventDefault();
            var pw = document.getElementById('pw').value;
            var err = document.getElementById('err');
            err.style.display = 'none';
            var body = { password: pw };
            if (configured) {
              body.username = document.getElementById('un').value;
            } else {
              var pw2 = document.getElementById('pw2').value;
              if (pw !== pw2) { err.textContent = 'Passwords do not match.'; err.style.display = 'block'; return; }
            }
            var btn = e.target.querySelector('button');
            btn.disabled = true;
            fetch('{{endpoint}}', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(body),
            }).then(function (res) {
              if (res.ok) { location.reload(); return; }
              return res.json().catch(function () { return {}; }).then(function (data) {
                err.textContent = data.message || 'Something went wrong.';
                err.style.display = 'block';
                btn.disabled = false;
              });
            }).catch(function () {
              err.textContent = 'Network error.';
              err.style.display = 'block';
              btn.disabled = false;
            });
          });
        </script>
        </body>
        </html>
        """;
    }
}
