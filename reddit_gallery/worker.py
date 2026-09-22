from __future__ import annotations

import queue
import threading
import time

from PySide6.QtCore import QObject, Signal

from .browser import FormChanged, NeedsLogin, NetworkProblem, RateLimited, RedditBrowser, Uncertain


class Events(QObject):
    changed = Signal()
    message = Signal(str)
    completed = Signal(str, object)


def run_post(store, browser, post):
    try:
        url = browser.send(post)
        store.status(post["id"], "published", url=url)
        return "Галерея опубликована"
    except (NetworkProblem, RateLimited) as exc:
        seconds = store.setting("retry_minutes", 15) * 60
        if isinstance(exc, RateLimited):
            seconds = max(seconds, exc.seconds)
        store.defer(post["id"], seconds, str(exc))
        return str(exc)
    except NeedsLogin as exc:
        store.status(post["id"], "scheduled", str(exc))
        store.set_setting("pause_reason", str(exc))
        return str(exc)
    except Uncertain as exc:
        store.status(post["id"], "review", str(exc))
        return str(exc)
    except (FormChanged, ValueError) as exc:
        store.status(post["id"], "error", str(exc))
        return str(exc)
    except Exception:
        # Unexpected adapter failures are never permission to retry a possible submit.
        store.status(post["id"], "review", "Неожиданная ошибка. Проверьте результат на Reddit.")
        return "Отправка требует проверки"


class Worker(threading.Thread):
    def __init__(self, store):
        super().__init__(name="reddit-browser-worker", daemon=True)
        self.store = store
        self.browser = RedditBrowser(store)
        self.events = Events()
        self.commands = queue.Queue()
        self.stopping = threading.Event()
        self.login_finished = threading.Event()
        self.login_cancelled = threading.Event()
        self.busy = threading.Event()
        self.maintenance = threading.Event()
        self.state_lock = threading.Lock()

    def submit(self, name, argument=None):
        self.commands.put((name, argument))

    def suspend(self):
        with self.state_lock:
            self.maintenance.set()
            return not self.busy.is_set() and self.commands.empty()

    def resume(self):
        self.maintenance.clear()

    def stop(self):
        self.stopping.set()
        self.login_cancelled.set()

    def run(self):
        self.store.recover()
        last = time.time()
        while not self.stopping.is_set():
            now = time.time()
            try:
                with self.state_lock:
                    if self.maintenance.is_set():
                        work = None
                    else:
                        work = self.next_work(now, last)
                        if work:
                            self.busy.set()
                last = now
                if work:
                    name, arg = work
                    try:
                        self.execute(name, arg)
                    finally:
                        self.busy.clear()
                        self.events.changed.emit()
            except Exception:
                self.store.set_setting("pause_reason", "Внутренняя ошибка очереди. Перезапустите приложение и проверьте историю.")
                self.events.message.emit("Очередь приостановлена из-за внутренней ошибки")
                self.busy.clear()
            self.stopping.wait(1)

    def next_work(self, now, last):
        try:
            return self.commands.get_nowait()
        except queue.Empty:
            pass
        if now - last > 60 or now < last - 5:
            self.store.recover(now)
            self.events.changed.emit()
        if self.store.setting("paused", True) or self.store.setting("pause_reason", ""):
            return None
        if not self.store.setting("account"):
            return None
        post = self.store.claim(now)
        return ("send", post) if post else None

    def execute(self, name, arg):
        try:
            if name == "send":
                self.events.message.emit(run_post(self.store, self.browser, arg))
                return
            if name == "login":
                result = self.browser.login(self.login_finished, self.login_cancelled, self.events.message.emit)
                self.store.set_setting("pause_reason", "")
                self.store.recover()
            elif name == "check":
                result = self.browser.check_session()
                self.store.set_setting("pause_reason", "")
                self.store.recover()
            elif name == "flairs":
                result = self.browser.fetch_flairs(arg)
            elif name == "disconnect":
                self.browser.disconnect()
                self.store.set_setting("paused", True)
                result = "Аккаунт отключён"
            else:
                raise ValueError("Неизвестная операция")
            self.events.completed.emit(name, result)
        except Exception as exc:
            if isinstance(exc, NeedsLogin):
                self.store.set_setting("pause_reason", str(exc))
            message = str(exc) if isinstance(exc, (FormChanged, NeedsLogin, NetworkProblem, RateLimited, ValueError)) else "Не удалось завершить операцию браузера. Проверьте соединение и повторите."
            self.events.completed.emit(name, {"error": message})
