"""Reddit UI adapter. Selectors fail closed; no private API or challenge bypass."""
from __future__ import annotations

import re
import shutil
import time
from contextlib import contextmanager
from urllib.parse import urlparse

from playwright.sync_api import Error as PlaywrightError, TimeoutError as PlaywrightTimeout, sync_playwright


class BrowserProblem(Exception):
    pass


class NetworkProblem(BrowserProblem):
    pass


class NeedsLogin(BrowserProblem):
    pass


class FormChanged(BrowserProblem):
    pass


class Uncertain(BrowserProblem):
    pass


class RateLimited(BrowserProblem):
    def __init__(self, seconds=900):
        self.seconds = seconds
        super().__init__(f"Reddit просит подождать {seconds // 60} мин.")


def visible_one(locators, label, required=True):
    for locator in locators:
        visible = [locator.nth(i) for i in range(locator.count()) if locator.nth(i).is_visible()]
        if len(visible) == 1:
            return visible[0]
        if len(visible) > 1:
            raise FormChanged(f"Неоднозначное поле: {label}")
    if required:
        raise FormChanged(f"Не найдено поле «{label}». Возможно, Reddit изменил форму.")
    return None


def valid_post_url(url):
    parsed = urlparse(url)
    return parsed.scheme == "https" and parsed.hostname in {"www.reddit.com", "reddit.com"} and bool(re.match(r"/r/[^/]+/comments/[a-z0-9]+(?:/|$)", parsed.path))


class RedditBrowser:
    def __init__(self, store):
        self.store = store
        self.profile = store.root / "browser-profile"

    @contextmanager
    def context(self, headless=True):
        with sync_playwright() as pw:
            try:
                context = pw.chromium.launch_persistent_context(
                    str(self.profile), headless=headless, channel="chromium",
                    locale="en-US", viewport={"width": 1400, "height": 1000},
                    accept_downloads=False,
                )
            except PlaywrightError as exc:
                raise FormChanged("Не удалось запустить Chromium. Переустановите полную сборку приложения или проверьте, что профиль не занят.") from exc
            context.set_default_timeout(15000)
            try:
                yield context
            finally:
                context.close()

    @staticmethod
    def guard(page):
        body = page.locator("body").inner_text(timeout=15000).lower()
        if any(s in body for s in ("blocked by network security", "verify you are human", "prove you're human", "prove your humanity", "подтвердите, что вы человек")):
            raise NeedsLogin("Reddit требует проверки в видимом браузере. Откройте «Подключить Reddit».")
        if any(s in body for s in ("too many requests", "take a break", "doing that too much", "try again in")):
            match = re.search(r"(?:try again in|for)\s+(\d+)\s*(second|minute|hour)", body)
            delay = int(match[1]) * {"second": 1, "minute": 60, "hour": 3600}[match[2]] if match else 900
            raise RateLimited(delay)
        if any(s in body for s in ("our cdn was unable", "upstream connect error", "service unavailable")):
            raise NetworkProblem("Reddit временно недоступен")

    def goto(self, page, url):
        try:
            response = page.goto(url, wait_until="domcontentloaded", timeout=45000)
            if response and response.status >= 500:
                raise NetworkProblem("Reddit временно недоступен")
            if response and response.status == 429:
                retry = response.headers.get("retry-after", "900")
                raise RateLimited(int(retry) if retry.isdigit() else 900)
            if response and response.status in (401, 403):
                raise NeedsLogin("Reddit ограничил доступ. Откройте окно входа и проверьте аккаунт.")
            page.locator("body").wait_for()
            self.guard(page)
        except PlaywrightError as exc:
            raise NetworkProblem("Не удалось загрузить страницу Reddit") from exc

    def identity(self, page):
        self.goto(page, "https://www.reddit.com/user/me/")
        try:
            page.wait_for_url(re.compile(r"https://www\.reddit\.com/user/(?!me(?:/|$))[^/]+/?.*"), timeout=12000)
        except PlaywrightTimeout:
            raise NeedsLogin("Не удалось подтвердить аккаунт. Выполните вход через окно браузера.")
        match = re.match(r"/user/([^/]+)", urlparse(page.url).path)
        if not match or match[1] == "me":
            raise NeedsLogin("Сессия Reddit не подтверждена")
        return match[1]

    def login(self, finished, cancelled, notify):
        with self.context(headless=False) as context:
            page = context.pages[0] if context.pages else context.new_page()
            page.goto("https://www.reddit.com/login/", wait_until="domcontentloaded", timeout=45000)
            notify("Войдите в открытом Chromium, затем нажмите «Вход завершён» в приложении.")
            deadline = time.monotonic() + 900
            while not finished.is_set():
                if cancelled.is_set() or page.is_closed():
                    raise NeedsLogin("Вход отменён")
                if time.monotonic() > deadline:
                    raise NeedsLogin("Время ожидания входа истекло")
                page.wait_for_timeout(200)
            name = self.identity(page)
            self.store.set_setting("account", name)
            return name

    def disconnect(self):
        root = self.store.root.resolve()
        profile = self.profile.resolve()
        if profile.parent != root or profile.name != "browser-profile":
            raise ValueError("Неверный путь профиля")
        if profile.exists():
            shutil.rmtree(profile)
        self.store.set_setting("account", None)

    def check_session(self):
        with self.context() as context:
            page = context.pages[0] if context.pages else context.new_page()
            name = self.identity(page)
            self.check_account(name)
            return name

    def check_account(self, name):
        expected = self.store.setting("account")
        if not expected or name.lower() != expected.lower():
            raise NeedsLogin("Аккаунт браузера отличается от подключённого. Подключите его заново.")

    def open_form(self, page, community):
        if not re.fullmatch(r"[a-zA-Z0-9_]{2,32}", community):
            raise FormChanged("Некорректное имя сообщества")
        self.goto(page, f"https://www.reddit.com/r/{community}/submit/?type=IMAGE")
        # Readiness waits are deliberately separate from selector lookup.
        try:
            page.locator('textarea[name="title"], input[name="title"], textarea[placeholder="Title"], textarea[placeholder="Заголовок"]').first.wait_for(timeout=20000)
        except PlaywrightTimeout:
            self.guard(page)
            raise FormChanged("Форма создания поста недоступна. Проверьте вход, права и поддержку галерей в сообществе.")
        return page

    @staticmethod
    def flair_button(page):
        return visible_one([
            page.get_by_role("button", name=re.compile(r"^(Add flair and tags|Add flair|Flair|Добавить флейр.*|Добавить метки.*)$", re.I)),
            page.locator('button#flair-button, button[data-testid="flair-picker"]'),
        ], "Flair и метки")

    def flair_options(self, page):
        self.flair_button(page).click()
        page.get_by_role("dialog").wait_for(timeout=10000)
        dialog = visible_one([page.get_by_role("dialog")], "Выбор flair")
        radios = dialog.get_by_role("radio")
        labels = [radios.nth(i).inner_text().strip() or radios.nth(i).get_attribute("aria-label") or "" for i in range(radios.count())]
        if not labels:
            raise FormChanged("Не удалось прочитать варианты flair")
        return dialog, [label for label in labels if label and label.lower() not in {"none", "no flair"}]

    def fetch_flairs(self, community):
        with self.context() as context:
            page = context.pages[0] if context.pages else context.new_page()
            self.check_account(self.identity(page))
            self.open_form(page, community)
            _, labels = self.flair_options(page)
            self.store.cache_flairs(community, labels)
            return labels

    def apply_labels(self, page, content):
        flair = content.get("flair", "")
        nsfw = bool(content.get("nsfw"))
        if not flair and not nsfw:
            return
        self.flair_button(page).click()
        dialog = page.get_by_role("dialog")
        dialog.wait_for(timeout=10000)
        if flair:
            option = visible_one([dialog.get_by_role("radio", name=flair, exact=True)], flair)
            option.check()
            if not option.is_checked():
                raise FormChanged("Не удалось выбрать flair")
        if nsfw:
            toggle = visible_one([
                dialog.get_by_role("checkbox", name=re.compile(r"NSFW|18\+|Mature", re.I)),
                dialog.get_by_role("switch", name=re.compile(r"NSFW|18\+|Mature", re.I)),
            ], "NSFW / 18+")
            toggle.check()
            if not toggle.is_checked():
                raise FormChanged("Не удалось включить NSFW")
        visible_one([dialog.get_by_role("button", name=re.compile(r"^(Apply|Save|Применить|Сохранить)$", re.I))], "Применить метки").click()
        dialog.wait_for(state="hidden")
        # Reopen and verify persisted state, not just the original click.
        self.flair_button(page).click()
        dialog.wait_for()
        if flair and not dialog.get_by_role("radio", name=flair, exact=True).is_checked():
            raise FormChanged("Reddit не сохранил flair")
        if nsfw:
            toggle = visible_one([dialog.get_by_role("checkbox", name=re.compile(r"NSFW|18\+|Mature", re.I)),
                                  dialog.get_by_role("switch", name=re.compile(r"NSFW|18\+|Mature", re.I))], "NSFW")
            if not toggle.is_checked():
                raise FormChanged("Reddit не сохранил NSFW")
        visible_one([dialog.get_by_role("button", name=re.compile(r"^(Apply|Save|Применить|Сохранить)$", re.I))], "Применить метки").click()
        dialog.wait_for(state="hidden")

    def fill_gallery(self, page, content):
        title = visible_one([page.get_by_role("textbox", name=re.compile(r"^Title|^Заголовок", re.I)),
                             page.locator('textarea[name="title"], input[name="title"], textarea[placeholder="Title"]')], "Заголовок")
        title.fill(content["title"])
        if title.input_value() != content["title"]:
            raise FormChanged("Заголовок не сохранился")
        uploader = page.locator('input[type="file"]')
        if uploader.count() != 1:
            raise FormChanged("Не найден однозначный загрузчик галереи")
        uploader.set_input_files([str(self.store.media_path(i["file"])) for i in content["images"]])
        # Reddit's media editor exposes a caption per item; requiring the exact count
        # prevents silent partial uploads and caption-to-image misalignment.
        captions = page.locator('input[placeholder*="caption" i], textarea[placeholder*="caption" i], input[placeholder*="подпись" i], textarea[placeholder*="подпись" i]')
        deadline = time.monotonic() + 90
        while captions.count() != len(content["images"]):
            self.guard(page)
            if time.monotonic() > deadline:
                raise FormChanged("Не удалось подтвердить все изображения и поля подписей. Отправка остановлена.")
            page.wait_for_timeout(300)
        for index, image in enumerate(content["images"]):
            caption = image.get("caption", "")
            captions.nth(index).fill(caption)
            if captions.nth(index).input_value() != caption:
                raise FormChanged("Не удалось сохранить подпись изображения")
        self.apply_labels(page, content)
        self.guard(page)

    @staticmethod
    def result(page, content):
        if not valid_post_url(page.url):
            return None
        if urlparse(page.url).path.split("/")[2].lower() != content["community"].lower():
            return None
        post = page.locator("shreddit-post").first
        if post.count() and post.get_attribute("post-title") == content["title"]:
            return page.url.split("?")[0]
        heading = page.get_by_role("heading", name=content["title"], exact=True)
        return page.url.split("?")[0] if heading.count() == 1 else None

    def send(self, post):
        self.store.validate(post["content"], publishing=True)
        clicked = False
        attempt_id = None
        with self.context() as context:
            page = context.pages[0] if context.pages else context.new_page()
            try:
                self.check_account(self.identity(page))
                self.open_form(page, post["content"]["community"])
                self.fill_gallery(page, post["content"])
                button = visible_one([page.get_by_role("button", name=re.compile(r"^(Post|Опубликовать)$"))], "Опубликовать")
                if not button.is_enabled():
                    raise FormChanged("Reddit не разрешает отправку. Проверьте требования сообщества.")
                attempt_id = self.store.attempt(post["id"])
                clicked = True  # Any error after this boundary is potentially a successful submit.
                button.click(timeout=20000)
                deadline = time.monotonic() + 60
                while time.monotonic() < deadline:
                    url = self.result(page, post["content"])
                    if url:
                        self.store.complete_attempt(attempt_id, url)
                        return url
                    page.wait_for_timeout(500)
                raise Uncertain("Reddit не подтвердил результат. Проверьте профиль перед повторной отправкой.")
            except Exception as exc:
                if clicked:
                    try:
                        url = self.result(page, post["content"])
                        if url:
                            self.store.complete_attempt(attempt_id, url)
                            return url
                    except Exception:
                        pass
                    raise Uncertain("Результат отправки неизвестен. Проверьте Reddit и укажите ссылку либо подтвердите отсутствие поста.") from exc
                if isinstance(exc, PlaywrightTimeout):
                    raise FormChanged("Истекло ожидание элемента формы Reddit; отправка не выполнялась") from exc
                if isinstance(exc, PlaywrightError):
                    raise NetworkProblem("Соединение с браузером или Reddit прервано до отправки") from exc
                raise
