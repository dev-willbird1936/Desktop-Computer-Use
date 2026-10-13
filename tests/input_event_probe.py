"""Observe tagged DCU mouse injection without blocking or altering any input."""
import ctypes
from ctypes import wintypes
import threading

USER = ctypes.WinDLL("user32", use_last_error=True)
KERNEL = ctypes.WinDLL("kernel32", use_last_error=True)
CALLBACK = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, ctypes.c_int, ctypes.c_size_t, ctypes.c_ssize_t)


class MouseEvent(ctypes.Structure):
    _fields_ = [("point", wintypes.POINT), ("data", wintypes.DWORD),
                ("flags", wintypes.DWORD), ("time", wintypes.DWORD), ("extra", ctypes.c_size_t)]


class InputEventProbe:
    def __init__(self):
        self.pids = set()
        self.phase = "startup"
        self.events = []
        self.ready = threading.Event()
        self.thread_id = 0
        self.error = None
        self.callback = CALLBACK(self.on_mouse)
        self.thread = threading.Thread(target=self.run, daemon=True)
        self.thread.start()
        if not self.ready.wait(5):
            raise TimeoutError("Input probe did not start")
        if self.error:
            raise self.error

    def on_mouse(self, code, message, pointer):
        if code >= 0:
            event = ctypes.cast(pointer, ctypes.POINTER(MouseEvent)).contents
            if event.flags & 1 and event.extra in self.pids:
                self.events.append((self.phase, event.extra, int(message)))
        return USER.CallNextHookEx(None, code, message, pointer)

    def run(self):
        USER.SetWindowsHookExW.argtypes = [ctypes.c_int, CALLBACK, ctypes.c_void_p, wintypes.DWORD]
        USER.SetWindowsHookExW.restype = ctypes.c_void_p
        USER.CallNextHookEx.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_size_t, ctypes.c_ssize_t]
        USER.CallNextHookEx.restype = ctypes.c_ssize_t
        USER.UnhookWindowsHookEx.argtypes = [ctypes.c_void_p]
        KERNEL.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
        KERNEL.GetModuleHandleW.restype = ctypes.c_void_p
        self.thread_id = KERNEL.GetCurrentThreadId()
        hook = USER.SetWindowsHookExW(14, self.callback, KERNEL.GetModuleHandleW(None), 0)
        if not hook:
            self.error = ctypes.WinError(ctypes.get_last_error())
            self.ready.set()
            return
        self.ready.set()
        try:
            message = wintypes.MSG()
            while USER.GetMessageW(ctypes.byref(message), None, 0, 0) > 0:
                USER.TranslateMessage(ctypes.byref(message))
                USER.DispatchMessageW(ctypes.byref(message))
        finally:
            USER.UnhookWindowsHookEx(hook)

    def count(self, phase):
        return sum(event[0] == phase for event in self.events)

    def close(self):
        USER.PostThreadMessageW(self.thread_id, 0x12, 0, 0)
        self.thread.join(timeout=5)
        if self.thread.is_alive():
            raise TimeoutError("Input probe did not stop")
