# -*- coding: utf-8 -*-
"""Build a PC on PCPartPicker using ONLY background mouse actions (UIA invoke /
posted window messages). No focus steal, no real cursor movement, no typing."""
import json, subprocess, time, base64

EXE = r"C:\SyncedProjects\Scripting\Windows-Computer-Use\src\ShadowUse\bin\Debug\net10.0-windows10.0.22621.0\shadow-use.exe"

class Mcp:
    def __init__(self):
        self.p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.next_id = 1
    def _send(self, o): self.p.stdin.write(json.dumps(o) + "\n"); self.p.stdin.flush()
    def call(self, method, params=None):
        rid = self.next_id; self.next_id += 1
        self._send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}})
        while True:
            line = self.p.stdout.readline()
            if not line: raise RuntimeError("server closed stdout")
            m = json.loads(line)
            if m.get("id") == rid: return m
    def notify(self, method): self._send({"jsonrpc": "2.0", "method": method})
    def tool(self, name, args=None):
        r = self.call("tools/call", {"name": name, "arguments": args or {}})
        c = r.get("result", {}).get("content", [])
        t = c[0]["text"] if c else "{}"
        try: return json.loads(t)
        except Exception: return {"raw": t}

m = Mcp()
m.call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                      "clientInfo": {"name": "driver", "version": "0.1"}})
m.notify("notifications/initialized")

APP = "chrome"

def snap(shot=False, tag=None, max_elements=900):
    r = m.tool("get_app_state", {"app": APP, "include_screenshot": shot, "max_elements": max_elements})
    if shot and r.get("screenshot_png_base64") and tag:
        open(f"C:/tmp/{tag}.png", "wb").write(base64.b64decode(r["screenshot_png_base64"]))
    return r

def click(eid):
    r = m.tool("click", {"app": APP, "element_id": eid})
    return r

DISMISS = ["accept", "got it", "agree", "continue", "no thanks", "not now", "dismiss"]
def dismiss(pg):
    for e in pg.get("elements", []):
        n = (e.get("name") or "").lower()
        if e.get("type") in ("Button", "Hyperlink") and e["w"] < 700 and any(w in n for w in DISMISS):
            if any(bad in n for bad in ["accept all cookies preferences"]): continue
            print("  dismiss:", e["name"][:60])
            click(e["id"]); time.sleep(1.5)
            return True
    return False

def wait_title_contains(word, tries=12):
    for _ in range(tries):
        t = (snap().get("title") or "").lower()
        if word.lower() in t: return True
        time.sleep(1.2)
    return False

# parts wishlist: (category link text, [preferred name substrings in priority order])
BUILD = [
    ("CPU",            ["7800x3d", "7600x3d", "7600x", "ryzen 5 7600", "ryzen 7 7700x"]),
    ("CPU Cooler",     ["peerless assassin", "ak400", "hyper 212", "assassin x", "freezer 36"]),
    ("Motherboard",    ["b650 tomahawk", "b650 gaming x", "b650 pro", "b650"]),
    ("Memory",         ["ddr5-6000", "6000", "ddr5-5600", "32 gb"]),
    ("Storage",        ["980 pro", "sn850x", "sn770", "2 tb", "1 tb"]),
    ("Video Card",     ["4070 ti", "4070 super", "4070", "7800 xt", "7900 gre"]),
    ("Case",           ["4000d", "pop air", "h5 flow", "lancool", "meshify"]),
    ("Power Supply",   ["rm750", "750 w", "850 w", "focus gx"]),
]

print("== pc build starting ==")
time.sleep(4)
pg = snap(shot=True, tag="pc_00_landing")
print("on:", pg.get("title", "?")[:80])
dismiss(pg)

# make sure we're on the system builder
pg = snap()
if "system builder" not in (pg.get("title") or "").lower():
    link = next((e for e in pg.get("elements", []) if e.get("type") == "Hyperlink"
                 and "build" in (e.get("name") or "").lower() and e["w"] < 500), None)
    if link:
        print("navigating to builder via:", link["name"][:50])
        click(link["id"]); time.sleep(3)
        dismiss(snap())

chosen = {}
for category, prefs in BUILD:
    print(f"\n== {category} ==")
    pg = snap()
    # the "Choose a X" / "Add" link for the category row
    cand = [e for e in pg.get("elements", [])
            if e.get("type") in ("Hyperlink", "Button")
            and category.lower() in (e.get("name") or "").lower()
            and any(k in (e.get("name") or "").lower() for k in ["choose", "add"])]
    scrolls = 0
    while not cand and scrolls < 5:
        m.tool("scroll", {"app": APP, "direction": "down", "pages": 1})
        time.sleep(1.2)
        pg = snap()
        cand = [e for e in pg.get("elements", [])
                if e.get("type") in ("Hyperlink", "Button")
                and category.lower() in (e.get("name") or "").lower()
                and any(k in (e.get("name") or "").lower() for k in ["choose", "add"])]
        scrolls += 1
    if not cand:
        print("  no category link found after scrolling, skipping"); continue
    pick = max(cand, key=lambda e: e["w"] * e["h"])
    print("  category link:", pick["name"][:60])
    r = click(pick["id"])
    if "error" in r: print("  click failed:", r["error"]); continue
    time.sleep(4)
    pg = snap(shot=True, tag=f"pc_{category.replace(' ', '_')}")
    if dismiss(pg): pg = snap()

    # pair product-name links with the Add button that follows them in tree order
    pairs, last_name = [], None
    for e in pg.get("elements", []):
        n = (e.get("name") or "").strip()
        if e.get("type") == "Hyperlink" and len(n) > 10 and not n.startswith("http"):
            last_name = e
        elif e.get("type") == "Button" and n.lower() == "add" and last_name is not None:
            pairs.append((last_name, e)); last_name = None
    print(f"  {len(pairs)} addable products")
    if not pairs:
        print("  no products found; going back"); continue
    product = None
    for pref in prefs:
        product = next(((nm, btn) for nm, btn in pairs if pref in nm["name"].lower()), None)
        if product: break
    if not product: product = pairs[0]
    nm, btn = product
    print("  adding:", nm["name"][:80])
    r = click(btn["id"])
    if "error" in r: print("  add failed:", r["error"]); continue
    time.sleep(3.5)
    dismiss(snap())
    chosen[category] = nm["name"]
    # sometimes adding redirects to the product page instead of back to the list
    pg = snap()
    if "system builder" not in (pg.get("title") or "").lower():
        back = next((e for e in pg.get("elements", []) if e.get("type") == "Hyperlink"
                     and "system builder" in (e.get("name") or "").lower()), None)
        if back: click(back["id"]); time.sleep(3)

print("\n===== BUILD COMPLETE =====")
for cat, name in chosen.items():
    print(f"  {cat}: {name[:90]}")
final = snap(shot=True, tag="pc_final")
m.tool("hide_cursor")
print("\nfinal screenshot: C:/tmp/pc_final.png")
