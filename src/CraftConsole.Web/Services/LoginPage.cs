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
          @font-face { font-family:'Inter'; font-style:normal; font-weight:400; font-display:swap; src:url('/fonts/inter-400.woff2') format('woff2'); }
          @font-face { font-family:'Inter'; font-style:normal; font-weight:500; font-display:swap; src:url('/fonts/inter-500.woff2') format('woff2'); }
          :root { --bg:#161826; --surface:#232532; --hair:color-mix(in srgb, #e9e9ed 16%, transparent);
                  --text:#e9e9ed; --muted:color-mix(in srgb, #e9e9ed 55%, transparent); --muted-2:color-mix(in srgb, #e9e9ed 38%, transparent);
                  --accent:#9184d9; --accent-400:#b5abfc; --lvl-err:#d98f8f; --danger-dim:color-mix(in srgb, #d98f8f 12%, transparent); }
          * { box-sizing:border-box; }
          html,body { margin:0; height:100%; background:var(--bg); color:var(--text);
                      font-family:'Inter',system-ui,-apple-system,sans-serif; }
          body { display:flex; align-items:center; justify-content:center; padding:20px; }
          ::selection { background: color-mix(in srgb, var(--accent) 25%, transparent); }
          :focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
          .card { width:100%; max-width:340px; background:var(--surface); border-radius:14px; padding:28px;
                  box-shadow:0 0 0 1px #3f424d, 0 8px 30px rgba(0,0,0,.35); }
          .brand { display:flex; align-items:center; gap:11px; margin-bottom:22px; }
          .mark { width:34px; height:34px; border-radius:9px; flex-shrink:0;
                  background: radial-gradient(circle at 30% 28%, rgba(255,255,255,.22), transparent 42%),
                              linear-gradient(180deg, var(--accent) 0%, var(--accent) 52%, #5d5294 52%); }
          h1 { font-size:15px; margin:0; font-weight:500; }
          p.sub { margin:2px 0 0; font-size:12px; color:var(--muted-2); }
          label { display:block; font-size:12px; font-weight:600; color:var(--muted); margin:14px 0 5px; }
          input { width:100%; padding:9px 11px; background:var(--bg); border:1px solid var(--hair);
                  border-radius:8px; color:var(--text); font-size:13.5px; }
          input:focus { outline:none; border-color:var(--accent); }
          button { width:100%; margin-top:18px; padding:10px; border:1px solid var(--accent); border-radius:8px;
                   background:transparent; color:var(--accent); font-weight:600; font-size:13.5px; cursor:pointer;
                   transition: background .17s, border-color .17s, transform .17s; }
          button:hover:not(:disabled) { background: color-mix(in srgb, var(--accent) 10%, transparent); transform: translateY(-1px); }
          button:active:not(:disabled) { border-color: var(--accent-400); color: var(--accent-400); }
          button:disabled { opacity:.45; cursor:not-allowed; }
          .err { display:none; margin-top:12px; padding:9px 11px; background:var(--danger-dim);
                 border:1px solid color-mix(in srgb, var(--lvl-err) 35%, transparent); border-radius:8px; color:var(--lvl-err); font-size:12.5px; }
          .hint { margin-top:12px; font-size:11.5px; color:var(--muted-2); line-height:1.5; }
          .hint code { color:var(--muted); }
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
              if (pw !== pw2) { err.textContent = 'Passwords do not match.'; err.style.display = ''; return; }
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
                err.style.display = '';
                btn.disabled = false;
              });
            }).catch(function () {
              err.textContent = 'Network error.';
              err.style.display = '';
              btn.disabled = false;
            });
          });
        </script>
        </body>
        </html>
        """;
    }
}
