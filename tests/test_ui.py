import os
import time

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtWidgets import QApplication
from reddit_gallery.app import Window
from reddit_gallery.core import Store
from reddit_gallery.dialogs import Editor, Copies, Settings
from reddit_gallery.worker import Worker
from PySide6.QtCore import QLockFile


def test_one_instance_per_data_directory(tmp_path):
    first = QLockFile(str(tmp_path / "app.lock"))
    second = QLockFile(str(tmp_path / "app.lock"))
    first.setStaleLockTime(0)
    second.setStaleLockTime(0)
    assert first.tryLock(0)
    assert not second.tryLock(0)
    first.unlock()
    assert second.tryLock(0)
    second.unlock()


def test_views_and_dialogs(tmp_path):
    app = QApplication.instance() or QApplication([])
    store = Store(tmp_path)
    worker = Worker(store)
    window = Window(store, worker, start_worker=False)
    for view in ["Месяц", "Неделя", "День", "Список"]:
        window.view.setCurrentText(view)
        window.refresh()
    editor = Editor(store, worker, parent=window)
    editor.title.setText("Черновик")
    editor.save()
    key = store.posts()[0]["id"]
    copies = Copies(store, key, window)
    copies.save()
    assert len(store.posts()) == 4
    settings = Settings(store, window)
    assert settings.zone.currentText() == "Europe/Moscow"
    window.exiting = True
    window.tray.hide()
    window.close()
