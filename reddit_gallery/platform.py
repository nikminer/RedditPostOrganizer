import os
import subprocess
import sys
from pathlib import Path


def set_autostart(enabled):
    if os.name != "nt":
        if enabled:
            raise ValueError("Автозапуск поддерживается в Windows")
        return
    import winreg
    if getattr(sys, "frozen", False):
        args = [sys.executable, "--tray"]
    else:
        pythonw = Path(sys.executable).with_name("pythonw.exe")
        args = [str(pythonw), "-m", "reddit_gallery", "--tray"]
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run") as key:
        if enabled:
            winreg.SetValueEx(key, "RedditGallery", 0, winreg.REG_SZ, subprocess.list2cmdline(args))
        else:
            try:
                winreg.DeleteValue(key, "RedditGallery")
            except FileNotFoundError:
                pass
