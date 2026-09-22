"""Offline release smoke test; uses only temporary data and local HTML."""
import json
import tempfile
from pathlib import Path

from playwright.sync_api import sync_playwright

from .app import Window
from .core import Store
from .worker import Worker


def smoke_test(app, report):
    result = {}
    try:
        with tempfile.TemporaryDirectory() as temp:
            store = Store(Path(temp))
            window = Window(store, Worker(store), start_worker=False)
            window.show()
            app.processEvents()
            result["database"] = store.path.exists()
            result["qt"] = window.isVisible()
            for view in ["Месяц", "Неделя", "День", "Список"]:
                window.view.setCurrentText(view)
                app.processEvents()
            window.tray.hide()
            window.exiting = True
            window.close()
            with sync_playwright() as pw:
                browser = pw.chromium.launch(headless=True, channel="chromium")
                page = browser.new_page()
                page.set_content('<h1>Offline test</h1><input aria-label="Title">')
                page.get_by_role("textbox", name="Title").fill("ok")
                result["chromium"] = page.get_by_role("textbox", name="Title").input_value() == "ok"
                browser.close()
        result["ok"] = all(result.values())
    except Exception as exc:
        result["ok"] = False
        result["error"] = f"{type(exc).__name__}: {exc}"
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    return 0 if result["ok"] else 1
