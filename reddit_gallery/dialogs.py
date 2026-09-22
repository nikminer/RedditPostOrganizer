from __future__ import annotations

import copy
from datetime import datetime
from zoneinfo import ZoneInfo, available_timezones

from PySide6.QtCore import QDateTime, QSize, Qt
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import (
    QAbstractItemView, QCheckBox, QComboBox, QDateTimeEdit, QDialog, QDialogButtonBox,
    QFileDialog, QFormLayout, QHBoxLayout, QInputDialog, QLabel, QLineEdit, QListWidget,
    QListWidgetItem, QMessageBox, QPushButton, QSpinBox, QTextEdit, QVBoxLayout,
)

from .core import display_time, local_candidates


def error(parent, exc):
    QMessageBox.warning(parent, "Не удалось выполнить действие", str(exc))


def utc_from_widget(parent, widget, zone):
    value = datetime.strptime(widget.dateTime().toString("yyyy-MM-dd HH:mm"), "%Y-%m-%d %H:%M")
    choices = local_candidates(value, zone)
    if not choices:
        raise ValueError("Это местное время не существует из-за перевода часов. Выберите другое.")
    if len(choices) == 2:
        labels = [datetime.fromtimestamp(ts, ZoneInfo(zone)).strftime("%H:%M %Z (%z)") for ts in choices]
        label, ok = QInputDialog.getItem(parent, "Перевод часов", "Это время встречается дважды. Выберите вариант:", labels, 0, False)
        if not ok:
            raise ValueError("Выбор времени отменён")
        return choices[labels.index(label)]
    return choices[0]


def time_widget(timestamp, zone):
    text = datetime.fromtimestamp(timestamp, ZoneInfo(zone)).strftime("%Y-%m-%d %H:%M")
    widget = QDateTimeEdit(QDateTime.fromString(text, "yyyy-MM-dd HH:mm"))
    widget.setDisplayFormat("dd.MM.yyyy HH:mm")
    widget.setCalendarPopup(True)
    return widget


class Editor(QDialog):
    def __init__(self, store, worker, post=None, parent=None):
        super().__init__(parent)
        import time
        self.store, self.worker, self.post = store, worker, post
        self.zone = store.setting("timezone", "Europe/Moscow")
        self.setWindowTitle("Редактор галереи")
        self.resize(810, 800)
        layout = QVBoxLayout(self)
        form = QFormLayout()
        content = copy.deepcopy(post["content"]) if post else {}
        self.title = QLineEdit(content.get("title", ""))
        self.title.setMaxLength(300)
        self.community = QLineEdit(content.get("community", ""))
        self.community.setPlaceholderText("Имя сообщества без r/")
        self.flair = QComboBox()
        self.flair.setEditable(True)
        self.flair.addItem("")
        self.flair.addItems(store.flairs(self.community.text()))
        self.flair.setCurrentText(content.get("flair", ""))
        flair_row = QHBoxLayout()
        flair_row.addWidget(self.flair)
        refresh = QPushButton("Получить из Reddit")
        refresh.clicked.connect(self.refresh_flairs)
        flair_row.addWidget(refresh)
        self.nsfw = QCheckBox("NSFW / 18+ — применить к посту Reddit")
        self.nsfw.setChecked(content.get("nsfw", False))
        self.tags = QLineEdit(", ".join(content.get("tags", [])))
        self.notes = QTextEdit(content.get("notes", ""))
        self.notes.setMaximumHeight(75)
        form.addRow("Заголовок", self.title)
        form.addRow("Сообщество r/", self.community)
        form.addRow("Reddit flair", flair_row)
        form.addRow("", self.nsfw)
        form.addRow("Локальные теги", self.tags)
        form.addRow("Приватные заметки", self.notes)
        layout.addLayout(form)
        layout.addWidget(QLabel("Изображения · перетащите для изменения порядка · двойной щелчок для подписи"))
        self.images = QListWidget()
        self.images.setIconSize(QSize(88, 68))
        self.images.setDragDropMode(QAbstractItemView.DragDropMode.InternalMove)
        self.images.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
        self.images.itemDoubleClicked.connect(self.caption)
        for img in content.get("images", []):
            self.add_image(img)
        layout.addWidget(self.images, 1)
        controls = QHBoxLayout()
        for label, callback in [("Добавить изображения", self.import_images), ("Удалить выбранные", self.remove_images), ("Подпись", self.caption_selected)]:
            button = QPushButton(label)
            button.clicked.connect(callback)
            controls.addWidget(button)
        layout.addLayout(controls)
        row = QHBoxLayout()
        self.schedule = QCheckBox("Запланировать")
        self.schedule.setChecked(bool(post and post["scheduled"] is not None))
        self.when = time_widget(post["scheduled"] if post and post["scheduled"] is not None else time.time() + 3600, self.zone)
        self.when.setEnabled(self.schedule.isChecked())
        self.schedule.toggled.connect(self.when.setEnabled)
        row.addWidget(self.schedule)
        row.addWidget(self.when)
        row.addWidget(QLabel(self.zone))
        layout.addLayout(row)
        self.flair_status = QLabel("Метки хранятся локально; перед отправкой проверяются на Reddit.")
        layout.addWidget(self.flair_status)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Save | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self.save)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
        worker.events.completed.connect(self.on_result)
        self.finished.connect(self.detach)

    def detach(self, _):
        self.worker.events.completed.disconnect(self.on_result)

    def add_image(self, image):
        item = QListWidgetItem(QIcon(str(self.store.media_path(image["file"]))), image.get("caption") or "Без подписи")
        item.setData(Qt.ItemDataRole.UserRole, image)
        item.setSizeHint(QSize(100, 76))
        self.images.addItem(item)

    def import_images(self):
        paths, _ = QFileDialog.getOpenFileNames(self, "Выберите изображения", "", "Изображения (*.jpg *.jpeg *.png *.webp)")
        try:
            if self.images.count() + len(paths) > 20:
                raise ValueError("Не больше 20 изображений в галерее")
            for path in paths:
                self.add_image(self.store.import_image(path))
        except Exception as exc:
            error(self, exc)

    def remove_images(self):
        for item in self.images.selectedItems():
            self.images.takeItem(self.images.row(item))

    def caption_selected(self):
        if self.images.currentItem():
            self.caption(self.images.currentItem())

    def caption(self, item):
        image = item.data(Qt.ItemDataRole.UserRole)
        text, ok = QInputDialog.getText(self, "Подпись к изображению", "До 180 символов", text=image.get("caption", ""))
        if ok:
            if len(text) > 180:
                error(self, "Не больше 180 символов")
                return
            image["caption"] = text
            item.setData(Qt.ItemDataRole.UserRole, image)
            item.setText(text or "Без подписи")

    def refresh_flairs(self):
        if not self.store.setting("account"):
            error(self, "Сначала подключите Reddit на главном экране")
            return
        if self.worker.busy.is_set():
            error(self, "Дождитесь завершения текущей операции браузера")
            return
        self.flair_status.setText("Получаю доступные flair…")
        self.worker.submit("flairs", self.community.text().strip().removeprefix("r/"))

    def on_result(self, operation, result):
        if operation != "flairs":
            return
        if isinstance(result, dict):
            self.flair_status.setText(result["error"])
        else:
            previous = self.flair.currentText()
            self.flair.clear()
            self.flair.addItem("")
            self.flair.addItems(result)
            self.flair.setCurrentText(previous)
            self.flair_status.setText("Метки обновлены")

    def save(self):
        try:
            content = {"title": self.title.text().strip(), "community": self.community.text().strip().removeprefix("r/"),
                       "flair": self.flair.currentText().strip(), "nsfw": self.nsfw.isChecked(),
                       "tags": list(dict.fromkeys(x.strip() for x in self.tags.text().split(",") if x.strip())),
                       "notes": self.notes.toPlainText(), "images": [self.images.item(i).data(Qt.ItemDataRole.UserRole) for i in range(self.images.count())]}
            when = utc_from_widget(self, self.when, self.zone) if self.schedule.isChecked() else None
            if when is not None:
                import time
                if when <= time.time():
                    raise ValueError("Выберите время в будущем")
            self.store.save(content, when, self.post["id"] if self.post else None)
            self.accept()
        except Exception as exc:
            error(self, exc)


class Copies(QDialog):
    def __init__(self, store, post_id, parent=None):
        import time
        super().__init__(parent)
        self.store, self.post_id = store, post_id
        self.zone = store.setting("timezone", "Europe/Moscow")
        self.setWindowTitle("Несколько копий")
        self.resize(460, 470)
        layout = QVBoxLayout(self)
        form = QFormLayout()
        self.count = QSpinBox()
        self.count.setRange(1, 500)
        self.count.setValue(3)
        self.scheduled = QCheckBox("Назначить время копиям")
        self.when = time_widget(time.time() + 3600, self.zone)
        self.interval = QSpinBox()
        self.interval.setRange(1, 525600)
        self.interval.setValue(120)
        self.interval.setSuffix(" мин.")
        form.addRow("Количество", self.count)
        form.addRow(self.scheduled)
        form.addRow("Первая копия · " + self.zone, self.when)
        form.addRow("Интервал", self.interval)
        layout.addLayout(form)
        self.preview = QTextEdit()
        self.preview.setReadOnly(True)
        layout.addWidget(self.preview)
        for signal in (self.count.valueChanged, self.scheduled.toggled, self.when.dateTimeChanged, self.interval.valueChanged):
            signal.connect(self.update_preview)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self.save)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
        self.update_preview()

    def update_preview(self):
        self.when.setEnabled(self.scheduled.isChecked())
        self.interval.setEnabled(self.scheduled.isChecked())
        value = datetime.strptime(self.when.dateTime().toString("yyyy-MM-dd HH:mm"), "%Y-%m-%d %H:%M")
        choices = local_candidates(value, self.zone)
        if self.scheduled.isChecked() and len(choices) != 1:
            self.preview.setText("Время попало в перевод часов. При сохранении потребуется выбор или исправление.")
            return
        self.preview.setText("\n".join(f"{i+1}. " + (display_time(choices[0] + i * self.interval.value() * 60, self.zone) if self.scheduled.isChecked() else "Черновик без даты") for i in range(self.count.value())))

    def save(self):
        import time
        try:
            start = utc_from_widget(self, self.when, self.zone) if self.scheduled.isChecked() else None
            if start is not None and start <= time.time():
                raise ValueError("Выберите время в будущем")
            self.store.copies(self.post_id, self.count.value(), start, self.interval.value() * 60)
            self.accept()
        except Exception as exc:
            error(self, exc)


class Settings(QDialog):
    def __init__(self, store, parent=None):
        super().__init__(parent)
        self.store = store
        self.setWindowTitle("Настройки")
        self.resize(560, 390)
        form = QFormLayout(self)
        self.zone = QComboBox()
        self.zone.setEditable(True)
        self.zone.addItems(sorted(available_timezones()))
        self.zone.setCurrentText(store.setting("timezone", "Europe/Moscow"))
        self.startup = QSpinBox()
        self.retry = QSpinBox()
        for widget, key, default in [(self.startup, "startup_minutes", 5), (self.retry, "retry_minutes", 15)]:
            widget.setRange(1, 1440)
            widget.setSuffix(" мин.")
            widget.setValue(store.setting(key, default))
        self.autostart = QCheckBox("Запускать в трее после входа в Windows")
        self.autostart.setChecked(store.setting("autostart", False))
        self.password = QLineEdit()
        self.password.setEchoMode(QLineEdit.EchoMode.Password)
        self.password.setPlaceholderText("Оставьте пустым, чтобы не менять")
        self.repeat = QLineEdit()
        self.repeat.setEchoMode(QLineEdit.EchoMode.Password)
        form.addRow("Часовой пояс", self.zone)
        form.addRow("После включения / сна", self.startup)
        form.addRow("Повтор при сетевом сбое", self.retry)
        form.addRow(self.autostart)
        form.addRow("Новый пароль интерфейса", self.password)
        form.addRow("Повторите пароль", self.repeat)
        hint = QLabel("Пароль блокирует интерфейс. Фоновая очередь продолжает работу.\nИзображения и база не шифруются этим паролем.")
        hint.setWordWrap(True)
        form.addRow(hint)
        form.addRow(QLabel("Данные: " + str(store.root)))
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Save | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self.save)
        buttons.rejected.connect(self.reject)
        form.addRow(buttons)

    def save(self):
        try:
            ZoneInfo(self.zone.currentText())
            password = self.password.text()
            if password:
                if len(password) < 8 or password != self.repeat.text():
                    raise ValueError("Пароль должен содержать минимум 8 символов; повтор должен совпадать")
            from .platform import set_autostart
            set_autostart(self.autostart.isChecked())
            if password:
                self.store.set_password(password)
            for key, value in [("timezone", self.zone.currentText()), ("startup_minutes", self.startup.value()),
                               ("retry_minutes", self.retry.value()), ("autostart", self.autostart.isChecked())]:
                self.store.set_setting(key, value)
            self.accept()
        except Exception as exc:
            error(self, exc)
