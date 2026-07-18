# Copyright (c) 2026 dev-willbird1936 — https://github.com/dev-willbird1936/Desktop-Computer-Use
# Licensed under MIT. See LICENSE. Keep this notice when redistributing.
# -*- coding: utf-8 -*-
"""Chain-surf news articles through shadow-use — background only, no focus/cursor theft."""
import json, os, subprocess, sys, time, base64

EXE = os.environ.get("DCU_EXE", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "publish", "dcu.exe"))

class Mcp:
    def __init__(self):
        self.p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.next_id = 1
    def _send(self, obj):
        self.p.stdin.write(json.dumps(obj) + "\n"); self.p.stdin.flush()
    def call(self, method, params=None):
        rid = self.next_id; self.next_id += 1
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}})
        while True:
            line = self.p.stdout.readline()
            if not line: raise RuntimeError("server closed stdout")
            msg = json.loads(line)
            if msg.get("id") == rid: return msg
    def notify(self, method):
        self._send({"jsonrpc": "2.0", "method": method})
    def tool(self, name, args=None):
        r = self.call("tools/call", {"name": name, "arguments": args or {}})
        content = r.get("result", {}).get("content", [])
        text = content[0]["text"] if content else "{}"
        try: return json.loads(text)
        except Exception: return {"raw": text}

m = Mcp()
m.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                      "clientInfo": {"name": "driver", "version": "0.1"}})
m.notify("notifications/initialized")

def snapshot(shot=False, tag=None):
    r = m.tool("get_app_state", {"app": "chrome", "include_screenshot": shot})
    if shot and r.get("screenshot_png_base64") and tag:
        open(f"C:/tmp/{tag}.png", "wb").write(base64.b64decode(r["screenshot_png_base64"]))
    return r

DISMISS = ["reject", "decline", "without accepting", "no thanks", "maybe later", "not now"]
CONSENT = DISMISS + ["accept all", "accept &", "i agree", "allow all", "got it"]

def dismiss_popups(pg):
    """Click the best dismiss/consent button if a popup is present. Never clicks bare 'Close'."""
    cands = [e for e in pg.get("elements", [])
             if e.get("type") in ("Button", "Hyperlink")
             and any(w in (e.get("name") or "").lower() for w in CONSENT)
             and e["w"] < 600]
    if not cands: return False
    pick = next((c for c in cands if any(w in c["name"].lower() for w in DISMISS)), cands[0])
    print("  dismissing popup via:", pick["name"][:60])
    m.tool("click", {"app": "chrome", "element_id": pick["id"]})
    time.sleep(1.5)
    return True

def collect_text(pg, seen_text):
    for e in pg.get("elements", []):
        t = (e.get("value") or e.get("name") or "").strip()
        if e.get("type") in ("Text", "Document") and len(t) > 40 and t not in seen_text \
           and not t.startswith("http"):
            seen_text.append(t)

def article_links(pg):
    links, seen = [], set()
    for e in pg.get("elements", []):
        name = (e.get("name") or "")
        if e.get("type") != "Hyperlink" or len(name) < 25 or name.startswith("http"): continue
        key = name[:40]
        if key in seen: continue
        seen.add(key)
        links.append(e)
    return links

print("== chain-surf starting ==")
time.sleep(3)
visited, digest = set(), []

for hop in range(4):
    pg = snapshot(shot=True, tag=f"hop{hop}")
    title = pg.get("title", "?")
    print(f"\n[hop {hop}] on: {title[:80]}")
    dismiss_popups(pg)
    if hop > 0:
        seen_text = []
        collect_text(pg, seen_text)
        for _ in range(3):
            m.tool("scroll", {"app": "chrome", "direction": "down", "pages": 1})
            time.sleep(1.2)
            collect_text(snapshot(), seen_text)
        body = " ".join(seen_text)[:700]
        digest.append((title.replace(" - BBC News - Google Chrome", ""), body))
        print("  read", len(body), "chars")
    links = article_links(snapshot())
    nxt = None
    for l in links:
        if l["name"][:40] not in visited:
            visited.add(l["name"][:40]); nxt = l; break
    if nxt is None:
        print("  no fresh links; stopping chain"); break
    print("  hopping to:", nxt["name"][:70])
    m.tool("click", {"app": "chrome", "element_id": nxt["id"]})
    time.sleep(4)

print("\n===== NEWS DIGEST =====")
for i, (headline, body) in enumerate(digest, 1):
    print(f"\n{i}. {headline}")
    print("   " + body[:450].replace("\n", " "))
m.tool("hide_cursor")
print("\ndone.")
