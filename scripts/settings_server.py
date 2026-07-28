# Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
# Licensed under MIT. See LICENSE. Keep this notice when redistributing.
# -*- coding: utf-8 -*-
"""DCU settings server: serves a tiny settings page, saves settings.json."""
import json, os, webbrowser
from http.server import BaseHTTPRequestHandler, HTTPServer

PORT = 8737
MAX_BODY_BYTES = 8_192
APPDATA_DIR = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "shadow-use")
APPDATA_SETTINGS = os.path.join(APPDATA_DIR, "settings.json")
EXE_SETTINGS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "publish", "settings.json")

DEFAULTS = {
    "AllowUiaTextFallback": True,
    "EnableBoundsGuard": False,
    "EnableDesktopCheck": False,
    "ShowVirtualCursor": True,
    "EnableFocusGuard": True,
    "PostActionDelayMs": 150,
}

TOGGLES = [
    ("AllowUiaTextFallback", "UIA text fallback", "Use UIA SetValue-append when EM_REPLACESEL isn't available. More compatible; can briefly foreground some apps."),
    ("EnableFocusGuard", "Focus guard", "Restore YOUR foreground window + caret after any action that grabs them. Keep this on."),
    ("ShowVirtualCursor", "Virtual cursor", "Show the green ghost cursor during actions."),
    ("EnableBoundsGuard", "Bounds guard (trust gate)", "Refuse coordinate clicks if the window moved since the last snapshot."),
    ("EnableDesktopCheck", "Desktop check (trust gate)", "Refuse to act on lock screen / secure desktop."),
]

PAGE = """<!doctype html>
<html><head><meta charset="utf-8"><title>DCU settings</title>
<style>
  :root { color-scheme: dark; }
  body { background:#0d1117; color:#e6edf3; font:14px/1.5 'Segoe UI',system-ui,sans-serif; max-width:640px; margin:40px auto; padding:0 20px; }
  h1 { font-size:20px; } h1 span { color:#1eff5a; }
  .row { display:flex; align-items:flex-start; gap:12px; padding:14px 0; border-bottom:1px solid #21262d; }
  .row input[type=checkbox] { width:20px; height:20px; margin-top:3px; accent-color:#1eff5a; }
  .row label { flex:1; cursor:pointer; }
  .row b { display:block; } .row small { color:#8b949e; }
  .num { width:90px; background:#161b22; border:1px solid #30363d; color:#e6edf3; border-radius:6px; padding:6px 8px; }
  button { margin-top:24px; background:#1eff5a; color:#06130a; border:0; border-radius:8px; padding:12px 28px; font-weight:700; font-size:15px; cursor:pointer; }
  button:hover { filter:brightness(1.15); }
  #status { margin-left:14px; color:#1eff5a; font-weight:600; }
  .note { color:#8b949e; font-size:12px; margin-top:8px; }
  .warn { color:#ffa657; margin-top:16px; font-size:13px; display:none; }
</style></head><body>
<h1><span>◆</span> DCU settings</h1>
<div id="rows"></div>
<div class="row"><label><b>Settle delay (ms)</b><small>Pause after each action before the follow-up snapshot.</small></label>
<input class="num" type="number" id="PostActionDelayMs" min="0" max="5000" step="50"></div>
<button onclick="save()">Save</button><span id="status"></span>
<div class="warn" id="warn">⚠ settings.json also exists next to dcu.exe — that file takes precedence over this one.</div>
<div class="note">Saved to %APPDATA%\\shadow-use\\settings.json — dcu picks it up on next start.</div>
<script>
const TOGGLES = %TOGGLES%;
let current = {};
async function load() {
  const r = await fetch('/settings'); const d = await r.json();
  current = d.settings;
  const rows = document.getElementById('rows');
  for (const [key, label, desc] of TOGGLES) {
    rows.innerHTML += `<div class="row"><input type="checkbox" id="${key}" ${current[key] ? 'checked' : ''}>
      <label for="${key}"><b>${label}</b><small>${desc}</small></label></div>`;
  }
  document.getElementById('PostActionDelayMs').value = current.PostActionDelayMs;
  if (d.exeSettingsExists) document.getElementById('warn').style.display = 'block';
}
async function save() {
  for (const [key] of TOGGLES) current[key] = document.getElementById(key).checked;
  const parsedDelay = parseInt(document.getElementById('PostActionDelayMs').value);
  current.PostActionDelayMs = Number.isNaN(parsedDelay) ? 150 : parsedDelay;
  const r = await fetch('/settings', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(current)});
  document.getElementById('status').textContent = r.ok ? ' ✓ saved' : ' save failed';
  setTimeout(() => document.getElementById('status').textContent = '', 2500);
}
load();
</script></body></html>"""


def load_settings():
    for path in (EXE_SETTINGS, APPDATA_SETTINGS):
        try:
            if os.path.exists(path):
                with open(path, encoding="utf-8") as f:
                    return {**DEFAULTS, **json.load(f)}
        except Exception:
            pass
    return dict(DEFAULTS)


def clean_settings(data):
    if not isinstance(data, dict):
        raise ValueError("settings payload must be an object")
    clean = {}
    for key, default in DEFAULTS.items():
        value = data.get(key, default)
        if key == "PostActionDelayMs":
            if isinstance(value, bool) or not isinstance(value, int):
                raise ValueError("PostActionDelayMs must be an integer")
            clean[key] = max(0, min(5_000, value))
        else:
            if not isinstance(value, bool):
                raise ValueError(f"{key} must be a boolean")
            clean[key] = value
    return clean


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a): pass

    def do_GET(self):
        if self.path in ("/", "/index.html"):
            body = PAGE.replace("%TOGGLES%", json.dumps(TOGGLES)).encode()
            self._send(200, body, "text/html; charset=utf-8")
        elif self.path == "/settings":
            payload = {"settings": load_settings(), "exeSettingsExists": os.path.exists(EXE_SETTINGS)}
            self._send(200, json.dumps(payload).encode(), "application/json")
        else:
            self._send(404, b"not found", "text/plain")

    def do_POST(self):
        if self.path != "/settings":
            return self._send(404, b"not found", "text/plain")
        # A plain form/fetch POST from some other page open in the same browser can still
        # be sent cross-origin (same-origin policy blocks reading the response, not sending
        # the request) — reject anything not claiming to come from this server's own origin,
        # so an unrelated page can't silently flip these safety toggles while this is running.
        origin = self.headers.get("Origin", "")
        if origin not in (f"http://127.0.0.1:{PORT}", f"http://localhost:{PORT}"):
            return self._send(403, b'{"ok":false,"error":"bad origin"}', "application/json")
        if self.headers.get_content_type() != "application/json":
            return self._send(415, b'{"ok":false,"error":"application/json required"}', "application/json")
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > MAX_BODY_BYTES:
                return self._send(413, b'{"ok":false,"error":"invalid body size"}', "application/json")
            data = json.loads(self.rfile.read(length))
            clean = clean_settings(data)
            os.makedirs(APPDATA_DIR, exist_ok=True)
            with open(APPDATA_SETTINGS, "w", encoding="utf-8") as f:
                json.dump(clean, f, indent=2)
            self._send(200, b'{"ok":true}', "application/json")
        except (ValueError, json.JSONDecodeError) as ex:
            self._send(400, json.dumps({"ok": False, "error": str(ex)}).encode(), "application/json")
        except Exception as ex:
            self._send(500, json.dumps({"ok": False, "error": str(ex)}).encode(), "application/json")

    def _send(self, code, body, ctype):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    webbrowser.open(f"http://127.0.0.1:{PORT}/")
    print(f"DCU settings page: http://127.0.0.1:{PORT}/  (Ctrl+C to stop)")
    HTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
