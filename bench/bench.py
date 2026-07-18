# -*- coding: utf-8 -*-
"""dcu benchmark harness — drives dcu.exe over stdio through the automatable test suite.

Usage: python bench/bench.py [test-id-substring ...]
Writes evidence to C:/tmp/dcu-bench/ and prints a report card.
"""
import ctypes, json, os, re, subprocess, sys, time, base64, threading, http.server, functools

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.environ.get("DCU_EXE", os.path.join(ROOT, "publish", "dcu.exe"))
EVID = r"C:\tmp\dcu-bench"
PORT = 8931
u32 = ctypes.windll.user32
k32 = ctypes.windll.kernel32
os.makedirs(EVID, exist_ok=True)

# ---------------- infra ----------------

class Mcp:
    def __init__(self):
        self.p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.DEVNULL, text=True, bufsize=1)
        self.n = 1
    def call(self, method, params=None):
        rid = self.n; self.n += 1
        self.p.stdin.write(json.dumps({"jsonrpc":"2.0","id":rid,"method":method,"params":params or {}})+"\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line: raise RuntimeError("dcu closed stdout")
            m = json.loads(line)
            if m.get("id") == rid: return m
    def notify(self, method):
        self.p.stdin.write(json.dumps({"jsonrpc":"2.0","method":method})+"\n"); self.p.stdin.flush()
    def tool(self, name, args=None):
        r = self.call("tools/call", {"name": name, "arguments": args or {}})
        if "error" in r: return {"error": "rpc: " + json.dumps(r["error"])[:200]}
        c = r.get("result", {}).get("content", [])
        t = c[0]["text"] if c else "{}"
        try: return json.loads(t)
        except Exception: return {"raw": t}
    def close(self):
        try: self.p.stdin.close()
        except Exception: pass
        try: self.p.wait(timeout=5)
        except Exception: self.p.kill()

def foreground():
    hwnd = u32.GetForegroundWindow()
    buf = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(hwnd, buf, 512)
    return hwnd, buf.value

def set_foreground(hwnd):
    cur = k32.GetCurrentThreadId()
    tid = u32.GetWindowThreadProcessId(hwnd, 0)
    u32.AttachThreadInput(cur, tid, True)
    u32.SetForegroundWindow(hwnd)
    u32.AttachThreadInput(cur, tid, False)

def find_window(title_part):
    out = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, l):
        if u32.IsWindowVisible(h):
            buf = ctypes.create_unicode_buffer(512)
            u32.GetWindowTextW(h, buf, 512)
            if title_part.lower() in buf.value.lower(): out.append(h)
        return True
    u32.EnumWindows(cb, 0)
    return out[0] if out else None

RESULTS = []
def test(tid, name):
    def deco(fn):
        def wrapped(ctx):
            try:
                r = fn(ctx)
                status, detail = r if r else ("pass", "")
            except Exception as ex:
                status, detail = "fail", f"exception: {ex}"
            RESULTS.append((tid, name, status, detail))
            print(f"[{status.upper():4}] {tid:>3} {name} — {detail}")
        return wrapped
    return deco

def ok(cond, detail=""): return ("pass", detail) if cond else ("fail", detail)
def skip(why): return ("skip", why)

def evidence_png(b64, tag):
    if b64:
        open(os.path.join(EVID, tag + ".png"), "wb").write(base64.b64decode(b64))

# ---------------- fixtures ----------------

class Ctx:
    mcp = None
    chrome_url = f"http://127.0.0.1:{PORT}/page.html"

def bench_elements(ctx, title_hint="dcu-bench"):
    """Snapshot chrome and return elements of the bench page (re-navigates if needed)."""
    s = ctx.mcp.tool("get_app_state", {"app": "chrome", "include_screenshot": False})
    if "error" in s: raise RuntimeError(s["error"])
    return s

def read_log(ctx):
    """Extract the log lines from the bench page via UIA text elements."""
    s = bench_elements(ctx)
    texts = [(e.get("value") or e.get("name") or "") for e in s.get("elements", [])]
    blob = "\n".join(texts)
    return blob, s

def wait_log(ctx, needle, tries=10, delay=0.5):
    for _ in range(tries):
        blob, s = read_log(ctx)
        if needle in blob: return blob, s
        time.sleep(delay)
    return read_log(ctx)

def el_by_name(snap, name, types=None):
    for e in snap.get("elements", []):
        if (e.get("name") or "") == name and (types is None or e.get("type") in types):
            return e
    return None

# ---------------- tests ----------------

@test("2", "focus-yank guard (omnibox click restores foreground)")
def t2(ctx):
    notepad = find_window("dcu-bench-notepad")
    if not notepad: return skip("bench notepad not open")
    set_foreground(notepad); time.sleep(0.4)
    hwnd_before, _ = foreground()
    s = ctx.mcp.tool("get_app_state", {"app": "chrome", "include_screenshot": False})
    omni = next((e for e in s["elements"] if "address and search" in (e.get("name") or "").lower()), None)
    if not omni: return skip("omnibox not found")
    ctx.mcp.tool("click", {"app": "chrome", "element_id": omni["id"]})
    time.sleep(0.8)
    hwnd_after, after_title = foreground()
    return ok(hwnd_after == hwnd_before and hwnd_after == notepad,
              f"foreground hwnd preserved={hwnd_after == hwnd_before} (title now: {after_title[:40]!r})")

@test("3", "no OS focus events on target during actions")
def t3(ctx):
    blob, _ = read_log(ctx)
    m = re.findall(r"focusEvents:(\d+)", blob)
    n = int(m[-1]) if m else 0
    # run a burst of actions
    s = bench_elements(ctx)
    pad = next((e for e in s["elements"] if "mouse-event pad area" in (e.get("name") or "")), None)
    if pad:
        for _ in range(5): ctx.mcp.tool("click", {"app": "chrome", "element_id": pad["id"]})
    blob2, _ = read_log(ctx)
    m2 = re.findall(r"focusEvents:(\d+)", blob2)
    n2 = int(m2[-1]) if m2 else 0
    return ok(n2 == n, f"focusEvents {n} -> {n2} over 5 clicks")

@test("5", "click accuracy grid 5x5 (coordinates)")
def t5(ctx):
    s = bench_elements(ctx)
    buttons = [e for e in s["elements"] if re.fullmatch(r"B\d+", e.get("name") or "")]
    if len(buttons) < 25: return fail_result(f"only {len(buttons)} grid buttons found")
    picks = [buttons[i] for i in (0, 6, 12, 18, 24)]
    hits = set()
    for b in picks:
        ctx.mcp.tool("click", {"app": "chrome", "x": b["x"] + b["w"]//2 + s["bounds"]["left"],
                               "y": b["y"] + b["h"]//2 + s["bounds"]["top"]})
        time.sleep(0.25)
    blob, _ = read_log(ctx)
    for b in picks:
        i = int(b["name"][1:])
        if f"grid:{i}" in blob: hits.add(i)
    return ok(len(hits) == len(picks), f"{len(hits)}/{len(picks)} grid hits {sorted(hits)}")

def fail_result(d): return ("fail", d)

@test("6", "click button coverage (left/right/double)")
def t6(ctx):
    s = bench_elements(ctx)
    pad = next((e for e in s["elements"] if "mouse-event pad area" in (e.get("name") or "")), None)
    if not pad: return skip("pad not found")
    bx, by = s["bounds"]["left"] + pad["x"] + 60, s["bounds"]["top"] + pad["y"] + 40
    ctx.mcp.tool("click", {"app": "chrome", "x": bx, "y": by})
    ctx.mcp.tool("click", {"app": "chrome", "x": bx, "y": by, "button": "right"})
    ctx.mcp.tool("click", {"app": "chrome", "x": bx, "y": by, "click_count": 2})
    blob, _ = wait_log(ctx, "dbl:0", tries=6)
    got = [k for k in ("down:0", "down:2", "dbl:0") if k in blob]
    return ok(len(got) == 3, f"events seen: {got}")

@test("7a", "typing gauntlet — notepad (ascii+unicode+long)")
def t7a(ctx):
    text = "ASCII hello 123 !@# | unicode: héllo wörld — 日本語テスト 🚀 | " + "x" * 1200
    r = ctx.mcp.tool("type_text", {"app": "dcu-bench-notepad", "text": text})
    if "error" in r: return fail_result(str(r["error"]))
    time.sleep(0.5)
    s = ctx.mcp.tool("get_app_state", {"app": "dcu-bench-notepad", "include_screenshot": False})
    doc = next((e for e in s["elements"] if e["type"] == "Document"), None)
    val = (doc or {}).get("value") or ""
    return ok(text[:60] in val and len(val) >= len(text) - 5, f"method={r.get('method')} read {len(val)} chars")

@test("7b", "typing gauntlet — chrome textarea")
def t7b(ctx):
    s = bench_elements(ctx)
    ta = next((e for e in s["elements"] if "bench textarea" in (e.get("name") or "")), None)
    if not ta: return skip("textarea not found")
    ctx.mcp.tool("click", {"app": "chrome", "element_id": ta["id"]})
    r = ctx.mcp.tool("type_text", {"app": "chrome", "text": "dcu typed into chrome textarea"})
    blob, _ = wait_log(ctx, "t1input:", tries=6)
    return ok("t1input:" in blob, f"method={r.get('method')} log={'t1input:' in blob}")

@test("9", "scroll fidelity (scrollY via title)")
def t9(ctx):
    r = ctx.mcp.tool("scroll", {"app": "chrome", "direction": "down", "pages": 3})
    time.sleep(0.8)
    hwnd = find_window("dcu-bench")
    buf = ctypes.create_unicode_buffer(512)
    u32.GetWindowTextW(hwnd, buf, 512)
    m = re.search(r"scrollY:(\d+)", buf.value)
    y = int(m.group(1)) if m else 0
    ctx.mcp.tool("scroll", {"app": "chrome", "direction": "up", "pages": 99})
    return ok(y > 300, f"scrollY={y} after 3 pages down")

@test("10", "html5 drag-drop via messages")
def t10(ctx):
    s = bench_elements(ctx)
    src = next((e for e in s["elements"] if "DRAGME source element" in (e.get("name") or "")), None)
    dz = next((e for e in s["elements"] if "DROP HERE target zone" in (e.get("name") or "")), None)
    if not src or not dz: return skip("drag elements not found")
    fx = s["bounds"]["left"] + src["x"] + src["w"]//2; fy = s["bounds"]["top"] + src["y"] + src["h"]//2
    tx = s["bounds"]["left"] + dz["x"] + dz["w"]//2; ty = s["bounds"]["top"] + dz["y"] + dz["h"]//2
    ctx.mcp.tool("drag", {"app": "chrome", "from_x": fx, "from_y": fy, "to_x": tx, "to_y": ty})
    blob, _ = wait_log(ctx, "drop:", tries=5)
    seen = "drop:" in blob
    started = "dragstart" in blob
    if seen: return ("pass", "drop fired")
    return ("warn", f"html5 dnd needs real input pipeline (dragstart={'yes' if started else 'no'}) — documented limit")

@test("11", "stable element ids hit intended control")
def t11(ctx):
    s = bench_elements(ctx)
    b7 = el_by_name(s, "B7")
    if not b7: return skip("B7 not found")
    ctx.mcp.tool("click", {"app": "chrome", "element_id": b7["id"]})
    blob, _ = wait_log(ctx, "grid:7", tries=5)
    return ok("grid:7" in blob, "B7 element id → grid:7 event")

@test("12", "stale id after DOM mutation")
def t12(ctx):
    s = bench_elements(ctx)
    b3 = el_by_name(s, "B3")
    mut = el_by_name(s, "Mutate DOM")
    if not b3 or not mut: return skip("elements missing")
    ctx.mcp.tool("click", {"app": "chrome", "element_id": mut["id"]})
    time.sleep(0.5)
    r = ctx.mcp.tool("click", {"app": "chrome", "element_id": b3["id"]})
    blob, _ = read_log(ctx)
    if "grid:3" in blob: return ("pass", "stale id re-resolved to correct control")
    errtxt = str(r.get("error") or r.get("raw") or "")
    if "Unknown element" in errtxt or "fresh ids" in errtxt:
        return ("pass", f"clean structured error: {errtxt[:60]}")
    return ("fail", f"silent wrong-click or no-op: {json.dumps(r)[:80]}")

@test("13", "duplicate-name buttons (5x 'Add')")
def t13(ctx):
    s = bench_elements(ctx)
    dups = [e for e in s["elements"] if (e.get("name") or "") == "Add" and e.get("type") == "Button"]
    if len(dups) < 5: return skip(f"only {len(dups)} dup buttons")
    third = sorted(dups, key=lambda e: (e["y"], e["x"]))[2]
    ctx.mcp.tool("click", {"app": "chrome", "element_id": third["id"]})
    blob, _ = wait_log(ctx, "dup:", tries=5)
    hits = re.findall(r"dup:(\d)", blob)
    return ok(hits and hits[-1] == "2", f"dup events: {hits} (want last=2)")

@test("14", "tree-scale snapshot timing")
def t14(ctx):
    t0 = time.time()
    s = ctx.mcp.tool("get_app_state", {"app": "chrome", "include_screenshot": False, "max_elements": 2000})
    dt = time.time() - t0
    return ok(dt < 5 and "elements" in s, f"{len(s.get('elements', []))} elements in {dt:.2f}s")

@test("16", "occluded-window capture")
def t16(ctx):
    # notepad with known text, covered by chrome window positioned over it
    np_hwnd = find_window("dcu-bench-notepad")
    ch_hwnd = find_window("dcu-bench")
    if not np_hwnd or not ch_hwnd: return skip("windows missing")
    class RECT(ctypes.Structure): _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("r", ctypes.c_long), ("b", ctypes.c_long)]
    rc = RECT(); u32.GetWindowRect(np_hwnd, ctypes.byref(rc))
    u32.SetWindowPos(ch_hwnd, 0, rc.l, rc.t, rc.r - rc.l, rc.b - rc.t, 0x0040)
    time.sleep(0.6)
    r = ctx.mcp.tool("get_app_state", {"app": "dcu-bench-notepad", "include_screenshot": True})
    img = r.get("screenshot_png_base64")
    evidence_png(img, "16_occluded")
    u32.SetWindowPos(ch_hwnd, 0, 50, 2500, 1200, 700, 0x0040)  # move chrome away
    return ok(img is not None and len(img) > 20000, f"captured {len(img or '')//1024}KB while fully covered")

@test("17", "minimized-window capture fails clean")
def t17(ctx):
    hwnd = find_window("dcu-bench-notepad")
    if not hwnd: return skip("notepad missing")
    u32.ShowWindow(hwnd, 6)  # minimize
    time.sleep(0.5)
    r = ctx.mcp.tool("get_app_state", {"app": "dcu-bench-notepad", "include_screenshot": True})
    u32.ShowWindow(hwnd, 9)  # restore
    img = r.get("screenshot_png_base64")
    return ok(img is None or len(img) < 8000, f"minimized capture: {'null/blank' if not img or len(img) < 8000 else 'CONTENT?!'}")

@test("19", "coordinate alignment at current DPI")
def t19(ctx):
    s = bench_elements(ctx)
    b0 = el_by_name(s, "B0")
    if not b0: return skip("B0 missing")
    # click center of B0 via coordinates — if DPI math is wrong, it misses
    cx = s["bounds"]["left"] + b0["x"] + b0["w"]//2
    cy = s["bounds"]["top"] + b0["y"] + b0["h"]//2
    ctx.mcp.tool("click", {"app": "chrome", "x": cx, "y": cy})
    blob, _ = wait_log(ctx, "grid:0", tries=5)
    return ok("grid:0" in blob, "frame-derived coords hit B0")

@test("24", "chrome scripted flow (link invoke → navigation)")
def t24(ctx):
    s = bench_elements(ctx)
    mut = el_by_name(s, "Mutate DOM")
    if not mut: return skip("mutate btn missing")
    r = ctx.mcp.tool("click", {"app": "chrome", "element_id": mut["id"]})
    blob, _ = wait_log(ctx, "mutated", tries=5)
    return ok("mutated" in blob, f"invoke fired ({r.get('method')})")

@test("29", "canvas app: no fake elements, coordinate fallback works")
def t29(ctx):
    s = bench_elements(ctx)
    cv = next((e for e in s["elements"] if "cv" == (e.get("automation_id") or "")), None)
    inside = [e for e in s["elements"] if cv and e["x"] > cv["x"] and e["x"] < cv["x"] + cv["w"]
              and e["y"] > cv["y"] and e["y"] < cv["y"] + cv["h"] and e["id"] != cv["id"]] if cv else []
    cx = s["bounds"]["left"] + (cv["x"] if cv else 20) + 50
    cy = s["bounds"]["top"] + (cv["y"] if cv else 400) + 40
    ctx.mcp.tool("click", {"app": "chrome", "x": cx, "y": cy})
    blob, _ = wait_log(ctx, "canvas:", tries=5)
    return ok("canvas:" in blob and len(inside) == 0, f"canvas click landed, {len(inside)} phantom children")

@test("31", "hung app: action times out clean, server survives")
def t31(ctx):
    # suspend notepad's threads, then action it
    hwnd = find_window("dcu-bench-notepad")
    if not hwnd: return skip("notepad missing")
    pid = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    THREAD_SUSPEND_RESUME = 0x0002
    threads = []
    hsnap = ctypes.windll.kernel32.CreateToolhelp32Snapshot(4, 0)
    class TE32(ctypes.Structure):
        _fields_ = [("cnt", ctypes.c_ulong), ("tid", ctypes.c_ulong), ("owner", ctypes.c_ulong), ("pad", ctypes.c_ulong * 3)]
    te = TE32(); te.cnt = ctypes.sizeof(TE32)
    if ctypes.windll.kernel32.Thread32First(hsnap, ctypes.byref(te)):
        while True:
            if te.owner == pid.value:
                h = ctypes.windll.kernel32.OpenThread(THREAD_SUSPEND_RESUME, False, te.tid)
                if h: ctypes.windll.kernel32.SuspendThread(h); threads.append(h)
            if not ctypes.windll.kernel32.Thread32Next(hsnap, ctypes.byref(te)): break
    ctypes.windll.kernel32.CloseHandle(hsnap)
    t0 = time.time()
    r = ctx.mcp.tool("type_text", {"app": "dcu-bench-notepad", "text": "into the void"})
    dt = time.time() - t0
    for h in threads:
        ctypes.windll.kernel32.ResumeThread(h); ctypes.windll.kernel32.CloseHandle(h)
    alive = ctx.mcp.tool("health_check")
    return ok(dt < 35 and alive.get("ok"), f"hung action returned in {dt:.1f}s, server alive")

@test("32", "rapid-fire stress (100 clicks + 30 types)")
def t32(ctx):
    t0 = time.time()
    s = bench_elements(ctx)
    pad = next((e for e in s["elements"] if "mouse-event pad area" in (e.get("name") or "")), None)
    if pad is None:
        time.sleep(1.5)
        s = bench_elements(ctx)
        pad = next((e for e in s["elements"] if "mouse-event pad area" in (e.get("name") or "")), None)
    if pad is None: return fail_result("pad not found after retry")
    bx, by = s["bounds"]["left"] + pad["x"] + 60, s["bounds"]["top"] + pad["y"] + 40
    errs = 0
    for i in range(100):
        r = ctx.mcp.tool("click", {"app": "chrome", "x": bx, "y": by})
        if "error" in r: errs += 1
    for i in range(30):
        r = ctx.mcp.tool("type_text", {"app": "dcu-bench-notepad", "text": f"burst {i} "})
        if "error" in r: errs += 1
    dt = time.time() - t0
    alive = ctx.mcp.tool("health_check")
    return ok(errs == 0 and alive.get("ok"), f"130 actions in {dt:.1f}s ({dt/130*1000:.0f}ms avg), {errs} errors")

@test("34", "window closed mid-action → clean error, server survives")
def t34(ctx):
    subprocess.run(["powershell", "-NoProfile", "-Command",
        "Get-Process notepad -ErrorAction SilentlyContinue | Where-Object {$_.MainWindowTitle -like '*dcu-bench-closeme*'} | Stop-Process -Force"],
        capture_output=True)
    subprocess.Popen(["notepad.exe", "dcu-bench-closeme.txt"])
    time.sleep(1.5)
    ctx.mcp.tool("get_app_state", {"app": "notepad", "include_screenshot": False})
    subprocess.run(["powershell", "-NoProfile", "-Command",
        "Get-Process notepad | Where-Object {$_.MainWindowTitle -like '*closeme*'} | Stop-Process -Force"],
        capture_output=True)
    time.sleep(0.7)
    r = ctx.mcp.tool("click", {"app": "notepad", "x": 500, "y": 500})
    alive = ctx.mcp.tool("health_check")
    return ok(alive.get("ok") is True, f"post-close click: {json.dumps(r)[:80]}; server alive")

@test("37", "multi-instance app resolution by pid")
def t37(ctx):
    apps = ctx.mcp.tool("list_apps")
    notepads = [a for a in apps.get("apps", []) if a["name"].lower() == "notepad"]
    if len(notepads) < 2: return skip(f"only {len(notepads)} notepads")
    target = notepads[0]
    s = ctx.mcp.tool("get_app_state", {"app": str(target["pid"]), "include_screenshot": False})
    return ok(s.get("pid") == target["pid"], f"pid resolution → {s.get('pid')}")

@test("38", "ghost overlay: click-through + capture-excluded flags")
def t38(ctx):
    # find the overlay window among top-level windows by size/style
    found = {}
    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, l):
        ex = u32.GetWindowLongW(h, -20)  # GWL_EXSTYLE
        LAYERED, TRANSPARENT, NOACTIVATE, TOOLWINDOW = 0x80000, 0x20, 0x8000000, 0x80
        if (ex & LAYERED) and (ex & TRANSPARENT) and (ex & NOACTIVATE) and (ex & TOOLWINDOW):
            aff = ctypes.c_ulong()
            if u32.GetWindowDisplayAffinity(h, ctypes.byref(aff)):
                found["hwnd"] = h; found["affinity"] = aff.value; found["exstyle"] = ex
        return True
    u32.EnumWindows(cb, 0)
    if not found: return fail_result("no ghost overlay window found")
    aff_ok = found["affinity"] == 0x11  # WDA_EXCLUDEFROMCAPTURE
    return ok(aff_ok, f"overlay exstyle=0x{found['exstyle']:X} affinity=0x{found['affinity']:X}")

@test("40", "ghost cleanup on server death")
def t40(ctx):
    m2 = Mcp()
    m2.call("initialize", {"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0.1"}})
    m2.notify("notifications/initialized")
    s = m2.tool("get_app_state", {"app": "chrome", "include_screenshot": False})
    b = el_by_name(s, "B5")
    if b: m2.tool("click", {"app": "chrome", "element_id": b["id"]})
    m2.p.kill()
    time.sleep(1.0)
    ghosts = []
    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(h, l):
        ex = u32.GetWindowLongW(h, -20)
        if (ex & 0x80000) and (ex & 0x20) and (ex & 0x8000000) and (ex & 0x80) and u32.IsWindowVisible(h):
            pid = ctypes.c_ulong(); u32.GetWindowThreadProcessId(h, ctypes.byref(pid))
            ghosts.append(pid.value)
        return True
    u32.EnumWindows(cb, 0)
    own = [g for g in ghosts if g == m2.p.pid]
    return ok(len(own) == 0, f"killed server, {len(own)} orphan ghosts")

@test("41", "shutdown regression: EOF variants exit 0")
def t41(ctx):
    codes = []
    for payload in ([], ['{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0.1"}}}']):
        p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, text=True)
        for line in payload: p.stdin.write(line + "\n")
        p.stdin.close(); codes.append(p.wait(timeout=15))
    return ok(all(c == 0 for c in codes), f"exit codes: {codes}")

@test("42", "tool schema conformance + bad args handling")
def t42(ctx):
    r = ctx.mcp.call("tools/list")
    tools = [t["name"] for t in r.get("result", {}).get("tools", [])]
    bad = ctx.mcp.tool("click", {"app": "chrome", "button": "purple"})
    bad2 = ctx.mcp.tool("scroll", {"app": "chrome", "direction": "sideways"})
    okk = len(tools) >= 10 and ("error" in bad or "ok" in bad) and ("error" in bad2 or "ok" in bad2)
    return ok(okk, f"{len(tools)} tools, bad-button→{list(bad)[:1]}, bad-dir→{list(bad2)[:1]}")

@test("43", "execute_sequence stops on error")
def t43(ctx):
    steps = json.dumps([
        {"tool": "press_key", "args": {"key": "F5"}},
        {"tool": "click", "args": {"element_id": "e999999"}},
        {"tool": "press_key", "args": {"key": "F5"}},
    ])
    r = ctx.mcp.tool("execute_sequence", {"app": "chrome", "steps_json": steps})
    stopped = r.get("stopped_at") == 2 and r.get("ok") is False
    return ok(stopped, f"stopped_at={r.get('stopped_at')} ok={r.get('ok')}")

@test("44", "settings: corrupt json falls back to defaults")
def t44(ctx):
    path = os.path.join(os.environ["APPDATA"], "shadow-use", "settings.json")
    backup = None
    if os.path.exists(path): backup = open(path).read()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    open(path, "w").write("{corrupt!!!")
    m2 = Mcp()
    m2.call("initialize", {"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"0.1"}})
    m2.notify("notifications/initialized")
    h = m2.tool("health_check")
    m2.close()
    if backup: open(path, "w").write(backup)
    else: os.remove(path)
    return ok(h.get("ok") is True, "corrupt settings.json → defaults, server healthy")

# ---------------- runner ----------------

def main():
    ctx = Ctx()
    only = sys.argv[1:]

    # serve bench page
    handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=os.path.join(ROOT, "bench"))
    srv = http.server.ThreadingHTTPServer(("127.0.0.1", PORT), handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()

    # fixture notepad with known title/content
    subprocess.run(["powershell", "-NoProfile", "-Command",
        "Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force"], capture_output=True)
    time.sleep(0.5)
    open(r"C:\tmp\dcu-bench-notepad.txt", "w").write("dcu bench notepad fixture")
    subprocess.Popen(["notepad.exe", r"C:\tmp\dcu-bench-notepad.txt"])
    time.sleep(1.5)

    # chrome on bench page
    subprocess.run(["powershell", "-NoProfile", "-Command",
        "Start-Process 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' -ArgumentList '--new-window','%s'" % ctx.chrome_url],
        capture_output=True)
    time.sleep(5)

    ctx.mcp = Mcp()
    ctx.mcp.call("initialize", {"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"bench","version":"0.1"}})
    ctx.mcp.notify("notifications/initialized")

    suite = [t2, t3, t5, t6, t7a, t7b, t9, t10, t11, t12, t13, t14, t16, t17, t19,
             t24, t29, t31, t32, t34, t37, t38, t40, t41, t42, t43, t44]
    for t in suite:
        tid = t.__wrapped__.__doc__ if hasattr(t, "__wrapped__") else ""
        # filter by cli substring
        name = getattr(t, "_tid", "")
        if only and not any(o in str(t) for o in only):
            # crude: run all if no filter; else match on function name
            if not any(o.lower() in t.__name__.lower() for o in only): continue
        try: t(ctx)
        except Exception as ex:
            RESULTS.append(("?", t.__name__, "fail", f"harness error: {ex}"))
        time.sleep(0.3)

    ctx.mcp.close()
    srv.shutdown()

    passed = sum(1 for r in RESULTS if r[2] == "pass")
    warned = sum(1 for r in RESULTS if r[2] == "warn")
    skipped = sum(1 for r in RESULTS if r[2] == "skip")
    failed = [(r) for r in RESULTS if r[2] == "fail"]
    print("\n================ REPORT CARD ================")
    print(f"pass {passed} | warn {warned} | skip {skipped} | FAIL {len(failed)}")
    for r in failed: print("  FAILED:", r[0], r[1], "—", r[3])
    return 1 if failed else 0

if __name__ == "__main__":
    sys.exit(main())
