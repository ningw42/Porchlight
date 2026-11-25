"""Disposable Windows console program. No shell, clipboard, files or keystroke log."""
import ctypes
from ctypes import wintypes
import msvcrt
import os
import sys
import time

# A visible per-process challenge, live PTY dimensions and count of accepted test keys.
nonce = sys.argv[1]
output = wintypes.HANDLE(msvcrt.get_osfhandle(sys.stdout.fileno()))
mode = wintypes.DWORD()
k = ctypes.windll.kernel32
if not k.GetConsoleMode(output, ctypes.byref(mode)) or not k.SetConsoleMode(output, mode.value | 5):
    raise ctypes.WinError()
sys.stdout.reconfigure(newline='')
count = 0
last = None
# Paint this banner only once, before the viewer exists. Reopening must repaint it from the core.
print(f'\x1b[?1049h\x1b[Hsmoke {nonce}', end='', flush=True)
start = time.monotonic()
while time.monotonic() - start < 180:
    cols, rows = os.get_terminal_size(sys.stdout.fileno())
    state = cols, rows, count
    if state != last:
        print(f'\x1b[2;1H\x1b[2Kfixture {os.getpid()} grid {cols}x{rows} accepted {count}', end='', flush=True)
        last = state
    if msvcrt.kbhit():
        key = msvcrt.getwch()
        if key == 'q':
            break
        if key == 'k':
            count += 1
    time.sleep(.03)
print('\x1b[?1049l', end='', flush=True)
