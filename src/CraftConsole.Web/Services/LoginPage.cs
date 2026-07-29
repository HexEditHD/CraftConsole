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
          :root { --bg:#0B0E14; --surface:#11151D; --border:#232A38;
                  --text:#E8ECF4; --text-2:#9AA5B8; --text-3:#667084;
                  --accent:#34D399; --accent-strong:#10B981; --danger:#F87171; --danger-dim:rgba(248,113,113,.12); }
          * { box-sizing:border-box; }
          html,body { margin:0; height:100%; background:var(--bg); color:var(--text);
                      font-family:'Segoe UI Variable Text','Segoe UI',system-ui,-apple-system,sans-serif; }
          body { display:flex; align-items:center; justify-content:center; padding:20px; }
          .card { width:100%; max-width:340px; background:var(--surface); border:1px solid var(--border);
                  border-radius:14px; padding:28px; box-shadow:0 8px 30px rgba(0,0,0,.35); }
          .brand { display:flex; align-items:center; gap:11px; margin-bottom:22px; }
          .mark { width:34px; height:34px; border-radius:9px; flex-shrink:0;
                  background: radial-gradient(circle at 30% 28%, rgba(255,255,255,.28), transparent 42%),
                              linear-gradient(180deg, #10B981 0%, #10B981 52%, #065F46 52%); }
          h1 { font-size:15px; margin:0; font-weight:650; }
          p.sub { margin:2px 0 0; font-size:12px; color:var(--text-3); }
          label { display:block; font-size:12px; font-weight:600; color:var(--text-2); margin:14px 0 5px; }
          input { width:100%; padding:9px 11px; background:var(--bg); border:1px solid var(--border);
                  border-radius:8px; color:var(--text); font-size:13.5px; }
          input:focus { outline:none; border-color:var(--accent-strong); }
          button { width:100%; margin-top:18px; padding:10px; border:none; border-radius:8px;
                   background:var(--accent-strong); color:#06281C; font-weight:600; font-size:13.5px; cursor:pointer; }
          button:hover { background:var(--accent); }
          button:disabled { opacity:.6; cursor:not-allowed; }
          .err { display:none; margin-top:12px; padding:9px 11px; background:var(--danger-dim);
                 border:1px solid rgba(248,113,113,.35); border-radius:8px; color:var(--danger); font-size:12.5px; }
          .hint { margin-top:12px; font-size:11.5px; color:var(--text-3); line-height:1.5; }
          .hint code { color:var(--text-2); }
        </style>
        </head>
        <body>
          <form class="card" id="f">
            <div class="brand">
              <div class="mark"></div>
              <div><h1>CraftConsole</h1><p class="sub">{{subtitle}}</p></div>
            </div>
            <label for="pw">{{label}}</label>
            <input id="pw" type="password" autocomplete="{{autocomplete}}" autofocus minlength="8" required>
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
            if (!configured) {
              var pw2 = document.getElementById('pw2').value;
              if (pw !== pw2) { err.textContent = 'Passwords do not match.'; err.style.display = ''; return; }
            }
            var btn = e.target.querySelector('button');
            btn.disabled = true;
            fetch('{{endpoint}}', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ password: pw }),
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
