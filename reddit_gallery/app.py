from __future__ import annotations

import argparse
import json
import os
import sys
import time
from datetime import date, datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from PySide6.QtCore import QLibraryInfo, QLocale, QLockFile, QTimer, QTranslator, Qt
from PySide6.QtGui import QAction, QColor, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QComboBox, QDialog, QDialogButtonBox, QFileDialog,
    QFormLayout, QHBoxLayout, QHeaderView, QInputDialog, QLabel, QLineEdit, QMainWindow,
    QMenu, QMessageBox, QPushButton, QSpinBox, QStackedWidget, QSystemTrayIcon,
    QTableWidget, QTableWidgetItem, QTextEdit, QVBoxLayout, QWidget,
)

from .browser import valid_post_url
from .calendar import Calendar
from .core import EDITABLE, STATES, Store, data_directory, display_time, local_candidates
from .dialogs import Copies, Editor, Settings, error, time_widget, utc_from_widget
from .worker import Worker

STYLE = """
QWidget {font-family: 'Segoe UI'; font-size: 13px; color: #23304a;}
QMainWindow, QDialog {background: #f5f7fc;}
QPushButton {background: #fff; border: 1px solid #d9e0ee; border-radius: 6px; padding: 8px 12px;}
QPushButton:hover {background: #eaf0ff; border-color: #8ba5ff;}
QPushButton:disabled {color: #a4aec1;}
QPushButton#primary {background: #355de9; color: white; border: 0; font-weight: 600;}
QLineEdit, QComboBox, QDateTimeEdit, QSpinBox, QTextEdit {background: white; border: 1px solid #d9e0ee; border-radius: 5px; padding: 6px;}
QTableWidget, QListWidget {background: white; border: 1px solid #dfe5f0; border-radius: 6px; gridline-color: #edf0f6;}
QHeaderView::section {background: #edf1fa; border: 0; padding: 8px; font-weight: 600;}
QTableWidget::item:selected, QListWidget::item:selected {background: #e0e8ff; color: #203d8f;}
QLabel#heading {font-size: 25px; font-weight: 700;}
QLabel#muted {color: #6a7890;}
"""


def app_icon():
    pixmap = QPixmap(64, 64)
    pixmap.fill(Qt.GlobalColor.transparent)
    painter = QPainter(pixmap)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.setBrush(QColor("#355de9"))
    painter.setPen(Qt.PenStyle.NoPen)
    painter.drawRoundedRect(2, 2, 60, 60, 16, 16)
    painter.setPen(QColor("white"))
    font = painter.font()
    font.setPixelSize(33)
    font.setBold(True)
    painter.setFont(font)
    painter.drawText(pixmap.rect(), Qt.AlignmentFlag.AlignCenter, "G")
    painter.end()
    return QIcon(pixmap)


class Window(QMainWindow):
    def __init__(self, store, worker, start_worker=True):
        super().__init__()
        self.store, self.worker = store, worker
        self.locked = bool(store.setting("password"))
        self.exiting = False
        self.login_dialog = None
        self.last_signature = None
        self.anchor = datetime.now(ZoneInfo(store.setting("timezone", "Europe/Moscow"))).date()
        self.setWindowTitle("Gallery · Reddit")
        self.setWindowIcon(app_icon())
        self.resize(1280, 860)
        self.stack = QStackedWidget()
        self.setCentralWidget(self.stack)
        main = QWidget()
        self.stack.addWidget(main)
        layout = QVBoxLayout(main)
        layout.setContentsMargins(24, 20, 24, 18)
        header = QHBoxLayout()
        title = QLabel("Gallery")
        title.setObjectName("heading")
        header.addWidget(title)
        sub = QLabel("REDDIT · ВАШ ПЛАН ПУБЛИКАЦИЙ")
        sub.setObjectName("muted")
        header.addWidget(sub)
        header.addStretch()
        for text, callback in [("Подключить Reddit", self.login), ("Настройки", self.settings), ("Заблокировать", self.lock)]:
            button = QPushButton(text)
            button.clicked.connect(callback)
            header.addWidget(button)
        layout.addLayout(header)
        self.summary = QLabel()
        self.summary.setWordWrap(True)
        layout.addWidget(self.summary)
        controls = QHBoxLayout()
        create = QPushButton("+ Новая галерея")
        create.setObjectName("primary")
        create.clicked.connect(lambda: self.edit())
        controls.addWidget(create)
        self.pause = QPushButton()
        self.pause.clicked.connect(self.toggle_pause)
        controls.addWidget(self.pause)
        self.view = QComboBox()
        self.view.addItems(["Месяц", "Неделя", "День", "Список"])
        self.view.currentTextChanged.connect(self.refresh)
        controls.addWidget(self.view)
        controls.addStretch()
        for text, callback in [("‹", lambda: self.navigate(-1)), ("Сегодня", self.today), ("›", lambda: self.navigate(1))]:
            button = QPushButton(text)
            button.clicked.connect(callback)
            controls.addWidget(button)
        self.period = QLabel()
        controls.addWidget(self.period)
        layout.addLayout(controls)
        filters = QHBoxLayout()
        self.search = QLineEdit()
        self.search.setPlaceholderText("Поиск по заголовку и заметкам")
        self.community = QLineEdit()
        self.community.setPlaceholderText("Сообщество")
        self.tag = QLineEdit()
        self.tag.setPlaceholderText("Локальный тег")
        self.status_filter = QComboBox()
        self.status_filter.addItem("Все статусы", "")
        for key, name in STATES.items():
            self.status_filter.addItem(name, key)
        for widget in (self.search, self.community, self.tag):
            widget.textChanged.connect(self.refresh)
            filters.addWidget(widget)
        self.status_filter.currentIndexChanged.connect(self.refresh)
        filters.addWidget(self.status_filter)
        layout.addLayout(filters)
        self.views = QStackedWidget()
        self.calendar = Calendar(store)
        self.calendar.moved.connect(self.move_post)
        self.calendar.opened.connect(self.edit)
        self.views.addWidget(self.calendar)
        self.table = QTableWidget(0, 6)
        self.table.setHorizontalHeaderLabels(["Галерея", "Сообщество", "Время", "Статус", "Метки", "Причина / ссылка"])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.ResizeToContents)
        self.table.horizontalHeader().setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        self.table.horizontalHeader().setSectionResizeMode(5, QHeaderView.ResizeMode.Stretch)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.cellDoubleClicked.connect(lambda row, _: self.edit(self.table.item(row, 0).data(Qt.ItemDataRole.UserRole)))
        self.views.addWidget(self.table)
        layout.addWidget(self.views, 1)
        footer = QHBoxLayout()
        for text, callback in [("Редактировать", self.edit_selected), ("Копия", self.copy_one), ("Несколько копий", self.copy_many),
                               ("Массовые действия", self.batch), ("История", self.history), ("Проверить результат", self.resolve)]:
            button = QPushButton(text)
            button.clicked.connect(callback)
            footer.addWidget(button)
        footer.addStretch()
        layout.addLayout(footer)
        self.notice = QLabel("Выберите пост в календаре или списке. Двойной щелчок открывает карточку.")
        self.notice.setWordWrap(True)
        self.notice.setObjectName("muted")
        layout.addWidget(self.notice)
        locked = QWidget()
        lock_layout = QVBoxLayout(locked)
        lock_layout.addStretch()
        lock_title = QLabel("Gallery · интерфейс заблокирован")
        lock_title.setAlignment(Qt.AlignmentFlag.AlignCenter)
        lock_layout.addWidget(lock_title)
        self.unlock_password = QLineEdit()
        self.unlock_password.setMaximumWidth(350)
        self.unlock_password.setEchoMode(QLineEdit.EchoMode.Password)
        self.unlock_password.setPlaceholderText("Пароль приложения")
        self.unlock_password.returnPressed.connect(self.unlock)
        lock_layout.addWidget(self.unlock_password, alignment=Qt.AlignmentFlag.AlignHCenter)
        unlock = QPushButton("Разблокировать")
        unlock.clicked.connect(self.unlock)
        lock_layout.addWidget(unlock, alignment=Qt.AlignmentFlag.AlignHCenter)
        lock_layout.addStretch()
        self.stack.addWidget(locked)
        self.stack.setCurrentIndex(1 if self.locked else 0)
        self.tray = QSystemTrayIcon(self.windowIcon(), self)
        self.tray.setToolTip("Gallery · Reddit")
        menu = QMenu()
        for text, callback in [("Открыть", self.show_window), ("Заблокировать", self.lock), ("Выйти", self.quit)]:
            action = menu.addAction(text)
            action.triggered.connect(callback)
        self.tray.setContextMenu(menu)
        self.tray.activated.connect(lambda reason: self.show_window() if reason == QSystemTrayIcon.ActivationReason.DoubleClick else None)
        self.tray.show()
        file_menu = self.menuBar().addMenu("Данные")
        for text, callback in [("Резервная копия…", self.backup), ("Восстановить…", self.restore), ("Открыть папку данных", self.open_data), ("Выйти", self.quit)]:
            file_menu.addAction(text).triggered.connect(callback)
        account_menu = self.menuBar().addMenu("Аккаунт")
        account_menu.addAction("Проверить фоновое подключение").triggered.connect(self.check_session)
        account_menu.addAction("Отключить аккаунт").triggered.connect(self.disconnect)
        self.menuBar().setVisible(not self.locked)
        worker.events.changed.connect(self.refresh)
        worker.events.message.connect(self.message)
        worker.events.completed.connect(self.completed)
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.poll)
        self.timer.start(5000)
        self.refresh()
        if start_worker:
            worker.start()

    def selected(self):
        if self.view.currentText() == "Список":
            return [self.table.item(row.row(), 0).data(Qt.ItemDataRole.UserRole) for row in self.table.selectionModel().selectedRows()]
        return [self.calendar.selected_id] if self.calendar.selected_id else []

    def one(self):
        ids = self.selected()
        if len(ids) != 1:
            error(self, "Выберите один пост")
            return None
        return ids[0]

    def poll(self):
        signature = [(p["id"], p["updated"]) for p in self.store.posts()]
        if signature != self.last_signature:
            self.refresh()

    def refresh(self, *_):
        posts = self.store.posts()
        self.last_signature = [(p["id"], p["updated"]) for p in posts]
        zone = self.store.setting("timezone", "Europe/Moscow")
        count = sum(p["status"] == "scheduled" for p in posts)
        account = self.store.setting("account")
        reason = self.store.setting("pause_reason", "")
        self.summary.setText(f"{count} в очереди   ·   {zone}   ·   " + (f"u/{account}" if account else "Reddit не подключён") + ("   ·   " + reason if reason else ""))
        self.pause.setText("▶ Запустить очередь" if self.store.setting("paused", True) else "Ⅱ Приостановить")
        filtered = []
        for post in posts:
            c = post["content"]
            if self.search.text().lower() not in (c["title"] + " " + c.get("notes", "")).lower():
                continue
            if self.community.text().lower().removeprefix("r/") not in c.get("community", "").lower():
                continue
            if self.tag.text().lower() not in ", ".join(c.get("tags", [])).lower():
                continue
            if self.status_filter.currentData() and self.status_filter.currentData() != post["status"]:
                continue
            filtered.append(post)
        self.period.setText(self.anchor.strftime("%d.%m.%Y"))
        if self.view.currentText() == "Список":
            selected = set(self.selected())
            self.views.setCurrentIndex(1)
            self.table.setRowCount(len(filtered))
            for row, post in enumerate(filtered):
                c = post["content"]
                values = [c["title"], "r/" + c.get("community", ""), display_time(post["scheduled"], zone), STATES[post["status"]],
                          ("18+ · " if c.get("nsfw") else "") + ", ".join(c.get("tags", [])), post["error"] or post["url"]]
                for col, value in enumerate(values):
                    item = QTableWidgetItem(value)
                    item.setData(Qt.ItemDataRole.UserRole, post["id"])
                    item.setToolTip(value)
                    if col == 0 and c.get("images"):
                        item.setIcon(QIcon(str(self.store.media_path(c["images"][0]["file"]))))
                    self.table.setItem(row, col, item)
                if post["id"] in selected:
                    self.table.selectRow(row)
        else:
            self.views.setCurrentIndex(0)
            self.calendar.render(self.anchor, self.view.currentText(), filtered, zone)

    def navigate(self, direction):
        if self.view.currentText() == "Месяц":
            month = self.anchor.year * 12 + self.anchor.month - 1 + direction
            self.anchor = date(month // 12, month % 12 + 1, 1)
        else:
            self.anchor += timedelta(days=(7 if self.view.currentText() == "Неделя" else 1) * direction)
        self.refresh()

    def today(self):
        self.anchor = datetime.now(ZoneInfo(self.store.setting("timezone", "Europe/Moscow"))).date()
        self.refresh()

    def edit(self, key=None):
        if self.locked:
            return
        post = self.store.get(key) if key else None
        if post and post["status"] not in EDITABLE:
            error(self, "Этот пост доступен в истории. Для нового поста создайте копию; для неизвестного результата используйте «Проверить результат».")
            return
        Editor(self.store, self.worker, post, self).exec()
        self.refresh()

    def edit_selected(self):
        key = self.one()
        if key:
            self.edit(key)

    def copy_one(self):
        key = self.one()
        if key:
            try:
                new = self.store.copies(key, 1)[0]
                self.edit(new)
            except Exception as exc:
                error(self, exc)

    def copy_many(self):
        key = self.one()
        if key:
            Copies(self.store, key, self).exec()
            self.refresh()

    def move_post(self, key, day, hour):
        try:
            post = self.store.get(key)
            if post["status"] not in EDITABLE:
                raise ValueError("Этот пост нельзя перемещать")
            zone = self.store.setting("timezone", "Europe/Moscow")
            old = datetime.fromtimestamp(post["scheduled"], ZoneInfo(zone))
            local = datetime(day.year, day.month, day.day, old.hour if hour is None else hour, old.minute)
            choices = local_candidates(local, zone)
            if len(choices) != 1:
                raise ValueError("Дата попадает в перевод часов. Задайте время через редактор.")
            if choices[0] <= time.time():
                raise ValueError("Выберите время в будущем")
            self.store.batch([key], times=[choices[0]])
            QTimer.singleShot(0, self.refresh)
        except Exception as exc:
            error(self, exc)

    def batch(self):
        ids = self.selected()
        if not ids:
            error(self, "Выберите посты в списке (Ctrl или Shift для нескольких)")
            return
        action, ok = QInputDialog.getItem(self, "Массовые действия", f"Выбрано: {len(ids)}", ["Назначить время", "Изменить сообщество", "Заменить локальные теги", "Включить NSFW", "Выключить NSFW", "Снять с расписания"], editable=False)
        if not ok:
            return
        try:
            changes, times, unschedule = {}, None, False
            if action == "Назначить время":
                dialog = QDialog(self)
                dialog.setWindowTitle("Расписание выбранных постов")
                form = QFormLayout(dialog)
                zone = self.store.setting("timezone", "Europe/Moscow")
                when = time_widget(time.time() + 3600, zone)
                interval = QSpinBox()
                interval.setRange(1, 525600)
                interval.setValue(120)
                form.addRow("Первый · " + zone, when)
                form.addRow("Интервал, минуты", interval)
                buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
                buttons.accepted.connect(dialog.accept)
                buttons.rejected.connect(dialog.reject)
                form.addRow(buttons)
                if not dialog.exec():
                    return
                start = utc_from_widget(self, when, zone)
                if start <= time.time():
                    raise ValueError("Выберите время в будущем")
                times = [start + i * interval.value() * 60 for i in range(len(ids))]
                preview = "\n".join(display_time(t, zone) for t in times[:20])
                if QMessageBox.question(self, "Применить расписание?", preview + ("\n…" if len(times) > 20 else "")) != QMessageBox.StandardButton.Yes:
                    return
            elif action in ("Изменить сообщество", "Заменить локальные теги"):
                text, ok = QInputDialog.getText(self, action, "Новое значение")
                if not ok:
                    return
                changes = {"community": text.strip().removeprefix("r/"), "flair": ""} if action == "Изменить сообщество" else {"tags": list(dict.fromkeys(t.strip() for t in text.split(",") if t.strip()))}
            elif action == "Снять с расписания":
                unschedule = True
            else:
                changes = {"nsfw": action == "Включить NSFW"}
            self.store.batch(ids, changes, times, unschedule)
            self.refresh()
        except Exception as exc:
            error(self, exc)

    def history(self):
        key = self.one()
        if not key:
            return
        dialog = QDialog(self)
        dialog.setWindowTitle("История поста")
        dialog.resize(680, 420)
        layout = QVBoxLayout(dialog)
        text = QTextEdit()
        text.setReadOnly(True)
        post = self.store.get(key)
        zone = self.store.setting("timezone", "Europe/Moscow")
        lines = [post["url"]] if post["url"] else []
        for event in self.store.events(key):
            detail = event["detail"]
            if event["kind"] == "shift":
                change = json.loads(detail)
                detail = f"{display_time(change['from'], zone)} → {display_time(change['to'], zone)}\n{change['reason']}"
            kind = "Перенос расписания" if event["kind"] == "shift" else STATES.get(event["kind"], event["kind"])
            lines.append(f"{display_time(event['at'], zone)} · {kind}\n{detail}")
        text.setPlainText("\n\n".join(lines) or "Для этого поста пока нет событий.")
        layout.addWidget(text)
        dialog.exec()

    def resolve(self):
        key = self.one()
        if not key:
            return
        post = self.store.get(key)
        if post["status"] != "review":
            error(self, "Это действие предназначено для постов со статусом «Требуется проверка»")
            return
        action, ok = QInputDialog.getItem(self, "Результат отправки", "Сначала проверьте свой профиль на Reddit", ["Пост опубликован — указать ссылку", "Проверено: пост отсутствует — вернуть в черновики"], editable=False)
        if not ok:
            return
        if action.startswith("Пост опубликован"):
            url, ok = QInputDialog.getText(self, "Ссылка на пост", "Полная https-ссылка Reddit")
            if not ok:
                return
            if not valid_post_url(url):
                error(self, "Нужна ссылка вида https://www.reddit.com/r/.../comments/...")
                return
            self.store.status(key, "published", "Подтверждено пользователем", url)
        else:
            self.store.status(key, "draft", "Пользователь подтвердил отсутствие публикации")
            self.store.batch([key], unschedule=True)
        self.refresh()

    def toggle_pause(self):
        paused = self.store.setting("paused", True)
        if paused:
            if not self.store.setting("account"):
                error(self, "Сначала подключите аккаунт Reddit")
                return
            if self.store.setting("pause_reason", ""):
                error(self, "Сначала восстановите подключение через меню «Аккаунт»")
                return
            if self.worker.busy.is_set():
                error(self, "Дождитесь окончания операции браузера")
                return
            self.store.recover()
        self.store.set_setting("paused", not paused)
        self.refresh()

    def login(self):
        if self.locked:
            return
        if self.worker.busy.is_set() or not self.worker.commands.empty():
            error(self, "Дождитесь окончания текущей операции браузера")
            return
        self.worker.login_finished.clear()
        self.worker.login_cancelled.clear()
        dialog = QDialog(self)
        dialog.setWindowTitle("Подключение Reddit")
        layout = QVBoxLayout(dialog)
        label = QLabel("Войдите в Reddit в отдельном окне Chromium.\nЗатем нажмите «Вход завершён». Пароль вводится только на сайте Reddit.")
        layout.addWidget(label)
        finish = QPushButton("Вход завершён")
        finish.clicked.connect(lambda: (self.worker.login_finished.set(), finish.setEnabled(False), label.setText("Проверяю подключённый аккаунт…")))
        layout.addWidget(finish)
        cancel = QPushButton("Отмена")
        cancel.clicked.connect(dialog.reject)
        layout.addWidget(cancel)
        self.login_dialog = dialog
        self.worker.submit("login")
        dialog.exec()
        self.worker.login_cancelled.set()
        self.login_dialog = None

    def completed(self, operation, result):
        if operation == "login" and self.login_dialog:
            self.login_dialog.accept()
        if isinstance(result, dict):
            self.message(result["error"])
        elif operation in {"login", "check"}:
            self.message(f"Аккаунт u/{result} подключён" if operation == "login" else f"Фоновое подключение u/{result} проверено")
        elif operation == "disconnect":
            self.message("Аккаунт отключён")
        self.refresh()

    def check_session(self):
        if not self.locked:
            self.worker.submit("check")

    def disconnect(self):
        if self.locked:
            return
        if QMessageBox.question(self, "Отключить Reddit", "Удалить сохранённую сессию браузера? Посты и изображения останутся.") == QMessageBox.StandardButton.Yes:
            self.store.set_setting("paused", True)
            self.worker.submit("disconnect")

    def message(self, text):
        self.notice.setText(text)
        self.tray.showMessage("Gallery", "Есть обновление очереди. Откройте приложение." if self.locked else text, QSystemTrayIcon.MessageIcon.Information, 5000)

    def settings(self):
        if not self.locked:
            Settings(self.store, self).exec()
            self.refresh()

    def lock(self):
        if not self.store.setting("password"):
            self.settings()
            if not self.store.setting("password"):
                return
        for dialog in self.findChildren(QDialog):
            if dialog.isVisible():
                dialog.reject()
        self.locked = True
        self.stack.setCurrentIndex(1)
        self.menuBar().hide()
        self.unlock_password.clear()

    def unlock(self):
        if self.store.check_password(self.unlock_password.text()):
            self.locked = False
            self.stack.setCurrentIndex(0)
            self.menuBar().show()
            self.unlock_password.clear()
        else:
            self.unlock_password.clear()
            error(self, "Неверный пароль")

    def backup(self):
        if self.locked:
            return
        path, _ = QFileDialog.getSaveFileName(self, "Резервная копия", f"Gallery-{date.today()}.zip", "ZIP (*.zip)")
        if path:
            try:
                self.store.backup(path)
                self.message("Резервная копия сохранена")
            except Exception as exc:
                error(self, exc)

    def restore(self):
        if self.locked:
            return
        if not self.worker.suspend():
            self.worker.resume()
            error(self, "Дождитесь окончания операции браузера")
            return
        try:
            path, _ = QFileDialog.getOpenFileName(self, "Восстановить резервную копию", "", "ZIP (*.zip)")
            if path and QMessageBox.question(self, "Восстановить данные?", "Текущая база будет заменена. Её резервная копия будет сохранена в папке данных. Очередь останется на паузе.") == QMessageBox.StandardButton.Yes:
                self.store.backup(self.store.root / f"before-restore-{int(time.time())}.zip")
                self.store.restore(path)
                self.refresh()
                self.message("Данные восстановлены. Подключите аккаунт и проверьте расписание.")
        except Exception as exc:
            error(self, exc)
        finally:
            self.worker.resume()

    def open_data(self):
        if not self.locked:
            from PySide6.QtCore import QUrl
            from PySide6.QtGui import QDesktopServices
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(self.store.root)))

    def show_window(self):
        self.showNormal()
        self.raise_()
        self.activateWindow()

    def closeEvent(self, event):
        if self.exiting:
            event.accept()
        elif QSystemTrayIcon.isSystemTrayAvailable():
            event.ignore()
            if self.store.setting("password"):
                self.lock()
            self.hide()
        else:
            event.ignore()
            self.quit()

    def quit(self):
        if self.locked:
            self.show_window()
            return
        self.worker.stop()
        if self.worker.is_alive():
            self.notice.setText("Завершаю текущую операцию браузера перед выходом…")
            self.exit_timer = QTimer(self)
            self.exit_timer.timeout.connect(self.finish_exit)
            self.exit_timer.start(200)
        else:
            self.finish_exit()

    def finish_exit(self):
        if self.worker.is_alive():
            return
        self.exiting = True
        self.tray.hide()
        QApplication.quit()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tray", action="store_true")
    parser.add_argument("--data-dir", type=Path)
    parser.add_argument("--self-test", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args()
    if getattr(sys, "frozen", False):
        bundled = Path(sys.executable).parent / "browsers"
        if bundled.is_dir():
            os.environ["PLAYWRIGHT_BROWSERS_PATH"] = str(bundled)
    app = QApplication(sys.argv)
    QLocale.setDefault(QLocale("ru_RU"))
    translator = QTranslator(app)
    translator.load("qtbase_ru", QLibraryInfo.path(QLibraryInfo.LibraryPath.TranslationsPath))
    app.installTranslator(translator)
    app.setApplicationName("RedditGallery")
    app.setStyle("Fusion")
    app.setStyleSheet(STYLE)
    app.setQuitOnLastWindowClosed(False)
    if args.self_test:
        from .diagnostics import smoke_test
        return smoke_test(app, args.self_test)
    root = args.data_dir or data_directory()
    root.mkdir(parents=True, exist_ok=True)
    lock = QLockFile(str(root / "application.lock"))
    lock.setStaleLockTime(0)
    if not lock.tryLock(100):
        QMessageBox.information(None, "Gallery", "Приложение уже запущено. Откройте его через значок в трее.")
        return 0
    store = Store(root)
    worker = Worker(store)
    window = Window(store, worker)
    if not args.tray or not QSystemTrayIcon.isSystemTrayAvailable():
        window.show()
    result = app.exec()
    worker.stop()
    worker.join(timeout=5)
    lock.unlock()
    return result
