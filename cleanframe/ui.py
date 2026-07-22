
from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Tuple

import cv2
from PySide6.QtCore import QObject, QPoint, QRect, Qt, QThread, Signal
from PySide6.QtGui import QColor, QImage, QMouseEvent, QPainter, QPen, QPixmap
from PySide6.QtWidgets import (
    QApplication, QFileDialog, QFrame, QHBoxLayout, QLabel, QListWidget,
    QListWidgetItem, QMainWindow, QMessageBox, QPushButton, QProgressBar,
    QRadioButton, QButtonGroup, QSlider, QVBoxLayout, QWidget
)

from .detector import suggest_overlay_rects
from .processor import CancelledError, VideoProcessor


APP_STYLE = """
QWidget { background:#0d1117; color:#e6edf3; font-family:'Segoe UI'; font-size:13px; }
QMainWindow { background:#0d1117; }
QFrame#panel { background:#131922; border:1px solid #273142; border-radius:14px; }
QPushButton { background:#202938; border:1px solid #324056; border-radius:9px; padding:9px 13px; }
QPushButton:hover { background:#283448; }
QPushButton#primary { background:#6c63ff; border-color:#817aff; font-weight:600; }
QPushButton#danger { background:#4a2028; border-color:#77323e; }
QListWidget { background:#10151d; border:0; border-radius:10px; padding:6px; }
QListWidget::item { padding:9px; border-radius:7px; }
QListWidget::item:selected { background:#29334a; }
QProgressBar { border:1px solid #2c3749; border-radius:7px; background:#0b0f15; text-align:center; }
QProgressBar::chunk { background:#6c63ff; border-radius:6px; }
"""


class PreviewCanvas(QWidget):
    rectChanged = Signal(tuple)

    def __init__(self):
        super().__init__()
        self.setMinimumSize(640, 360)
        self._pixmap: Optional[QPixmap] = None
        self._start: Optional[QPoint] = None
        self._rect = QRect()
        self._relative_rect: Optional[Tuple[float, float, float, float]] = None

    def set_frame(self, frame_bgr) -> None:
        rgb = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB)
        h, w = rgb.shape[:2]
        image = QImage(rgb.data, w, h, 3 * w, QImage.Format_RGB888).copy()
        self._pixmap = QPixmap.fromImage(image)
        self.update()

    def set_relative_rect(self, rect) -> None:
        self._relative_rect = rect
        self.update()

    def relative_rect(self):
        return self._relative_rect

    def _image_rect(self) -> QRect:
        if not self._pixmap:
            return QRect()
        scaled = self._pixmap.size().scaled(self.size(), Qt.KeepAspectRatio)
        x = (self.width() - scaled.width()) // 2
        y = (self.height() - scaled.height()) // 2
        return QRect(x, y, scaled.width(), scaled.height())

    def paintEvent(self, _event):
        p = QPainter(self)
        p.fillRect(self.rect(), QColor("#080b10"))
        if not self._pixmap:
            p.setPen(QColor("#768399"))
            p.drawText(self.rect(), Qt.AlignCenter, "Thêm video để bắt đầu")
            return
        target = self._image_rect()
        p.drawPixmap(target, self._pixmap)
        draw_rect = self._rect
        if self._relative_rect and self._start is None:
            x, y, w, h = self._relative_rect
            draw_rect = QRect(
                target.x() + int(x * target.width()),
                target.y() + int(y * target.height()),
                int(w * target.width()),
                int(h * target.height()),
            )
        if not draw_rect.isNull():
            p.setPen(QPen(QColor("#9b8cff"), 2))
            p.setBrush(QColor(124, 105, 255, 48))
            p.drawRoundedRect(draw_rect.normalized(), 5, 5)

    def mousePressEvent(self, event: QMouseEvent):
        if event.button() == Qt.LeftButton and self._pixmap:
            self._start = event.position().toPoint()
            self._rect = QRect(self._start, self._start)
            self.update()

    def mouseMoveEvent(self, event: QMouseEvent):
        if self._start is not None:
            self._rect = QRect(self._start, event.position().toPoint()).normalized()
            self.update()

    def mouseReleaseEvent(self, event: QMouseEvent):
        if self._start is None:
            return
        self._rect = QRect(self._start, event.position().toPoint()).normalized()
        self._start = None
        target = self._image_rect()
        clipped = self._rect.intersected(target)
        if clipped.width() < 3 or clipped.height() < 3:
            self._relative_rect = None
        else:
            self._relative_rect = (
                (clipped.x() - target.x()) / target.width(),
                (clipped.y() - target.y()) / target.height(),
                clipped.width() / target.width(),
                clipped.height() / target.height(),
            )
            self.rectChanged.emit(self._relative_rect)
        self.update()


class Worker(QObject):
    progress = Signal(int, str)
    finished = Signal()
    failed = Signal(str)

    def __init__(self, files, out_dir, rect, model):
        super().__init__()
        self.files = files
        self.out_dir = out_dir
        self.rect = rect
        self.processor = VideoProcessor(model)

    def run(self):
        try:
            total = len(self.files)
            for i, path in enumerate(self.files):
                out = self.out_dir / f"{path.stem}_clean{path.suffix}"
                base = int(i * 100 / total)
                span = 100 / total
                self.processor.process(
                    path, out, self.rect,
                    lambda p, msg: self.progress.emit(
                        min(100, int(base + p * span / 100)), msg
                    ),
                )
            self.finished.emit()
        except CancelledError:
            self.failed.emit("Đã huỷ tác vụ.")
        except Exception as exc:
            self.failed.emit(str(exc))

    def cancel(self):
        self.processor.cancel()


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("CleanFrame Video")
        self.resize(1280, 760)
        self.files: list[Path] = []
        self.current_frame = None
        self.thread: Optional[QThread] = None
        self.worker: Optional[Worker] = None

        root = QWidget()
        self.setCentralWidget(root)
        layout = QHBoxLayout(root)
        layout.setContentsMargins(18, 18, 18, 18)
        layout.setSpacing(14)

        left = QFrame(objectName="panel")
        left_l = QVBoxLayout(left)
        title = QLabel("VIDEO")
        title.setStyleSheet("font-weight:700; letter-spacing:1px; color:#98a6ba;")
        left_l.addWidget(title)
        self.list = QListWidget()
        self.list.currentRowChanged.connect(self.load_preview)
        left_l.addWidget(self.list, 1)
        add_files = QPushButton("+ Thêm file")
        add_folder = QPushButton("+ Thêm thư mục")
        add_files.clicked.connect(self.choose_files)
        add_folder.clicked.connect(self.choose_folder)
        left_l.addWidget(add_files)
        left_l.addWidget(add_folder)
        left.setFixedWidth(250)

        center = QFrame(objectName="panel")
        center_l = QVBoxLayout(center)
        self.canvas = PreviewCanvas()
        center_l.addWidget(self.canvas, 1)
        self.slider = QSlider(Qt.Horizontal)
        self.slider.setRange(0, 1000)
        self.slider.valueChanged.connect(self.seek_preview)
        center_l.addWidget(self.slider)

        right = QFrame(objectName="panel")
        right_l = QVBoxLayout(right)
        right.setFixedWidth(280)
        mode = QLabel("VÙNG XOÁ")
        mode.setStyleSheet("font-weight:700; letter-spacing:1px; color:#98a6ba;")
        right_l.addWidget(mode)
        self.auto = QRadioButton("Tự động đề xuất")
        self.manual = QRadioButton("Khoanh thủ công")
        self.manual.setChecked(True)
        group = QButtonGroup(self)
        group.addButton(self.auto)
        group.addButton(self.manual)
        right_l.addWidget(self.auto)
        right_l.addWidget(self.manual)

        detect = QPushButton("Tự tìm overlay")
        detect.clicked.connect(self.detect_overlay)
        right_l.addWidget(detect)

        hint = QLabel("Thủ công: kéo chuột trực tiếp trên video để tạo khung.")
        hint.setWordWrap(True)
        hint.setStyleSheet("color:#8491a6;")
        right_l.addWidget(hint)

        self.apply_same = QRadioButton("Áp dụng cho tất cả file")
        self.apply_same.setChecked(True)
        right_l.addWidget(self.apply_same)
        right_l.addStretch(1)

        self.status = QLabel("Sẵn sàng")
        self.status.setWordWrap(True)
        self.status.setStyleSheet("color:#9ca9ba;")
        right_l.addWidget(self.status)
        self.progress = QProgressBar()
        right_l.addWidget(self.progress)

        choose_out = QPushButton("Chọn thư mục xuất")
        choose_out.clicked.connect(self.choose_output)
        self.output_dir: Optional[Path] = None
        right_l.addWidget(choose_out)

        run = QPushButton("Xử lý tất cả", objectName="primary")
        run.clicked.connect(self.start_processing)
        cancel = QPushButton("Huỷ", objectName="danger")
        cancel.clicked.connect(self.cancel_processing)
        right_l.addWidget(run)
        right_l.addWidget(cancel)

        layout.addWidget(left)
        layout.addWidget(center, 1)
        layout.addWidget(right)

    def choose_files(self):
        paths, _ = QFileDialog.getOpenFileNames(
            self, "Chọn video", "", "Video (*.mp4 *.mov *.mkv *.webm *.avi)"
        )
        self.add_paths([Path(p) for p in paths])

    def choose_folder(self):
        folder = QFileDialog.getExistingDirectory(self, "Chọn thư mục")
        if folder:
            exts = {".mp4", ".mov", ".mkv", ".webm", ".avi"}
            self.add_paths(
                sorted(p for p in Path(folder).rglob("*") if p.suffix.lower() in exts)
            )

    def add_paths(self, paths):
        existing = set(self.files)
        for p in paths:
            if p not in existing:
                self.files.append(p)
                self.list.addItem(QListWidgetItem(p.name))
        if self.files and self.list.currentRow() < 0:
            self.list.setCurrentRow(0)

    def load_preview(self, row: int):
        if row < 0 or row >= len(self.files):
            return
        cap = cv2.VideoCapture(str(self.files[row]))
        ok, frame = cap.read()
        cap.release()
        if ok:
            self.current_frame = frame
            self.canvas.set_frame(frame)
            self.status.setText(self.files[row].name)

    def seek_preview(self, value: int):
        row = self.list.currentRow()
        if row < 0 or row >= len(self.files):
            return
        cap = cv2.VideoCapture(str(self.files[row]))
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT)) or 1
        cap.set(cv2.CAP_PROP_POS_FRAMES, int(value / 1000 * max(0, total - 1)))
        ok, frame = cap.read()
        cap.release()
        if ok:
            self.current_frame = frame
            self.canvas.set_frame(frame)

    def detect_overlay(self):
        row = self.list.currentRow()
        if row < 0:
            QMessageBox.information(self, "CleanFrame", "Hãy thêm video trước.")
            return
        suggestions = suggest_overlay_rects(self.files[row])
        if suggestions:
            self.canvas.set_relative_rect(suggestions[0])
            self.status.setText(
                "Đã đề xuất một vùng overlay tổng quát. "
                "Hãy kiểm tra và kéo lại nếu cần."
            )

    def choose_output(self):
        folder = QFileDialog.getExistingDirectory(self, "Chọn thư mục xuất")
        if folder:
            self.output_dir = Path(folder)
            self.status.setText(f"Xuất vào: {folder}")

    def start_processing(self):
        if not self.files:
            QMessageBox.information(self, "CleanFrame", "Chưa có video.")
            return
        rect = self.canvas.relative_rect()
        if not rect:
            QMessageBox.information(self, "CleanFrame", "Hãy chọn vùng cần xoá.")
            return
        if not self.output_dir:
            self.choose_output()
            if not self.output_dir:
                return
        model = Path(sys._MEIPASS if getattr(sys, "frozen", False) else Path(__file__).resolve().parents[1]) / "models" / "lama_fp32.onnx"
        selected = self.files if self.apply_same.isChecked() else [self.files[self.list.currentRow()]]
        self.thread = QThread()
        self.worker = Worker(selected, self.output_dir, rect, model)
        self.worker.moveToThread(self.thread)
        self.thread.started.connect(self.worker.run)
        self.worker.progress.connect(self.on_progress)
        self.worker.finished.connect(self.on_finished)
        self.worker.failed.connect(self.on_failed)
        self.thread.start()

    def on_progress(self, value, text):
        self.progress.setValue(value)
        self.status.setText(text)

    def on_finished(self):
        self.progress.setValue(100)
        self.status.setText("Đã xử lý xong.")
        QMessageBox.information(self, "CleanFrame", "Đã xử lý xong.")
        self.cleanup_thread()

    def on_failed(self, message):
        self.status.setText(message)
        QMessageBox.warning(self, "CleanFrame", message)
        self.cleanup_thread()

    def cleanup_thread(self):
        if self.thread:
            self.thread.quit()
            self.thread.wait(3000)
        self.worker = None
        self.thread = None

    def cancel_processing(self):
        if self.worker:
            self.worker.cancel()
            self.status.setText("Đang huỷ sau frame hiện tại…")


def run():
    app = QApplication(sys.argv)
    app.setStyleSheet(APP_STYLE)
    window = MainWindow()
    window.show()
    sys.exit(app.exec())
