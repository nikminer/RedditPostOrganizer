from __future__ import annotations

import calendar
from datetime import date, datetime, timedelta
from zoneinfo import ZoneInfo

from PySide6.QtCore import QMimeData, QSize, Qt, Signal
from PySide6.QtGui import QColor, QDrag, QIcon
from PySide6.QtWidgets import QAbstractItemView, QHeaderView, QListWidget, QListWidgetItem, QTableWidget, QTableWidgetItem

from .core import STATES

MIME = "application/x-reddit-gallery-post"


class Slot(QListWidget):
    moved = Signal(str, object, object)
    opened = Signal(str)

    def __init__(self, day, hour=None, parent=None):
        super().__init__(parent)
        self.day, self.hour = day, hour
        self.setAcceptDrops(True)
        self.setDragEnabled(True)
        self.setIconSize(QSize(36, 36))
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        self.setStyleSheet("QListWidget {border: 0; background: transparent; padding: 0;} QListWidget::item {padding: 3px;}")
        self.itemDoubleClicked.connect(lambda item: self.opened.emit(item.data(Qt.ItemDataRole.UserRole)))

    def startDrag(self, _):
        item = self.currentItem()
        if not item:
            return
        drag = QDrag(self)
        mime = QMimeData()
        mime.setData(MIME, item.data(Qt.ItemDataRole.UserRole).encode())
        drag.setMimeData(mime)
        drag.exec(Qt.DropAction.MoveAction)

    def dragEnterEvent(self, event):
        if event.mimeData().hasFormat(MIME):
            event.acceptProposedAction()

    def dragMoveEvent(self, event):
        if event.mimeData().hasFormat(MIME):
            event.acceptProposedAction()

    def dropEvent(self, event):
        if event.mimeData().hasFormat(MIME):
            self.moved.emit(bytes(event.mimeData().data(MIME)).decode(), self.day, self.hour)
            event.acceptProposedAction()


class Calendar(QTableWidget):
    moved = Signal(str, object, object)
    opened = Signal(str)

    def __init__(self, store, parent=None):
        super().__init__(parent)
        self.store = store
        self.selected_id = None
        self.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.setSelectionMode(QAbstractItemView.SelectionMode.NoSelection)
        self.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        self.setShowGrid(True)
        self.setMinimumHeight(400)

    def render(self, anchor, mode, posts, zone):
        self.selected_id = None
        self.clear()
        tz = ZoneInfo(zone)
        if mode == "Месяц":
            first = anchor.replace(day=1)
            start = first - timedelta(days=first.weekday())
            self.setRowCount(6)
            self.setColumnCount(7)
            self.setHorizontalHeaderLabels(["ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС"])
            self.verticalHeader().hide()
            self.verticalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
            slots = {(start + timedelta(days=r*7+c), None): (r, c) for r in range(6) for c in range(7)}
        else:
            start = anchor - timedelta(days=anchor.weekday()) if mode == "Неделя" else anchor
            columns = 7 if mode == "Неделя" else 1
            self.setRowCount(24)
            self.setColumnCount(columns)
            self.setHorizontalHeaderLabels([(start + timedelta(days=c)).strftime("%d.%m") for c in range(columns)])
            self.setVerticalHeaderLabels([f"{hour:02d}:00" for hour in range(24)])
            self.verticalHeader().show()
            self.verticalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Fixed)
            self.verticalHeader().setDefaultSectionSize(95)
            slots = {(start + timedelta(days=c), r): (r, c) for r in range(24) for c in range(columns)}
        widgets = {}
        for (day, hour), (r, c) in slots.items():
            slot = Slot(day, hour)
            slot.itemClicked.connect(lambda item, owner=slot: self.select_post(owner, item))
            slot.moved.connect(self.moved.emit)
            slot.opened.connect(self.opened.emit)
            if mode == "Месяц":
                label = QListWidgetItem(str(day.day) + (" · сегодня" if day == datetime.now(tz).date() else ""))
                label.setFlags(Qt.ItemFlag.NoItemFlags)
                label.setForeground(QColor("#7c8aa5") if day.month != anchor.month else QColor("#315ce6"))
                slot.addItem(label)
            self.setCellWidget(r, c, slot)
            widgets[(day, hour)] = slot
        for post in posts:
            if post["scheduled"] is None:
                continue
            local = datetime.fromtimestamp(post["scheduled"], tz)
            key = (local.date(), None if mode == "Месяц" else local.hour)
            if key not in widgets:
                continue
            content = post["content"]
            label = f"{local:%H:%M} {'18+ · ' if content.get('nsfw') else ''}{content['title']}"
            item = QListWidgetItem(label)
            item.setData(Qt.ItemDataRole.UserRole, post["id"])
            item.setToolTip(f"{label}\nr/{content.get('community', '')}\n{STATES[post['status']]}")
            colors = {"published": "#157355", "error": "#b6463c", "review": "#a06b00", "scheduled": "#315ce6"}
            item.setForeground(QColor(colors.get(post["status"], "#44516b")))
            if content.get("images"):
                item.setIcon(QIcon(str(self.store.media_path(content["images"][0]["file"]))))
            widgets[key].addItem(item)

    def select_post(self, owner, item):
        self.selected_id = item.data(Qt.ItemDataRole.UserRole)
        for other in self.findChildren(Slot):
            if other is not owner:
                other.clearSelection()
