#!/usr/bin/env python3
"""Render WinSight's local UI with a mocked Tauri bridge; requires Playwright and an existing Edge install."""
import functools
import json
import re
import threading
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

ROOT = Path(__file__).resolve().parent.parent
MOCK = r"""
(() => {
  const info = {
    setUp: false, offered: [], chosen: [], skipped: [], folders: [], skipTypes: [],
    hotkey: 'Ctrl+Shift+Space', shortcut: {method: 'grab', command: 'WinSight.exe --toggle'},
    model: {state: 'ready'}, state: {model: {state: 'ready'}, kinds: {}, files: 0, pending: 0},
    canOpenAtLogin: true, canChooseTrayClick: true, trayClick: 'window',
    updatesConfigured: false, updateChecks: false, installsUpdates: true, version: '0.7.0-beta.2',
    storage: {}, code: {folders: [], editors: [], editor: 'default'}
  };
  window.__brandInvokes = [];
  window.__TAURI__ = {
    core: {
      invoke: async (command, args) => {
        window.__brandInvokes.push(command);
        if (command === 'setup_info' || command === 'settings_info') return structuredClone(info);
        if (command === 'estimate') return [];
        if (command === 'ask') return {ok: true, folders: [], summaries: [], kinds: {}, files: 0, pending: 0};
        throw new Error('Unexpected mocked command: ' + command);
      },
      convertFileSrc: path => path
    },
    event: {listen: async () => () => {}},
    path: {homeDir: async () => 'C:/WinSightFixtures'}
  };
})();
"""


class QuietHandler(SimpleHTTPRequestHandler):
    def log_message(self, format, *args):
        pass


def main():
    server = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(QuietHandler, directory=str(ROOT / "shell/ui")))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{server.server_port}/"
    errors = []
    try:
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(channel="msedge", headless=True,
                args=["--disable-background-networking", "--disable-component-update", "--no-first-run"])
            context = browser.new_context(viewport={"width": 800, "height": 700}, service_workers="block")
            context.route("**/*", lambda route: route.continue_() if route.request.url.startswith(base) else route.abort())
            context.add_init_script(MOCK)
            page = context.new_page()
            page.on("pageerror", lambda error: errors.append(str(error)))
            page.goto(base + "setup.html")
            expect(page).to_have_title("Set Up WinSight")
            expect(page.locator("h1")).to_have_text("WinSight")
            expect(page.locator(".tagline")).to_have_text("See Through Files, Instantly.")
            expect(page.locator(".icon").first).to_be_visible()
            page.wait_for_function("document.querySelector('.icon').naturalWidth === 256")
            page.screenshot(path=str(ROOT / "docs/winsight-setup-preview.png"))
            page.locator("#next").click()
            expect(page.locator("#folders")).to_be_visible()
            page.goto(base + "settings.html")
            expect(page).to_have_title("WinSight Settings")
            expect(page.locator("#update-info")).to_contain_text("Updates are disabled")
            expect(page.locator("#update-checks")).to_be_disabled()
            expect(page.locator("#check-now")).to_be_disabled()
            page.screenshot(path=str(ROOT / "docs/winsight-settings-preview.png"))
            for tab in ("storage", "folders", "code", "general"):
                page.locator(f'[data-tab="{tab}"]').click()
                expect(page.locator(f"#{tab}")).to_be_visible()
            expect(page.locator("#version")).to_have_text(re.compile(r"WinSight .*EmbeddingGemma"))
            assert not errors, errors
            report = {"scope": "Local frontend with mocked Tauri; not the installed native application",
                      "setup_title": "Set Up WinSight", "settings_title": "WinSight Settings",
                      "icon_loaded": True, "disabled_update_controls": True,
                      "tested_tabs": ["storage", "folders", "code", "general"], "page_errors": errors,
                      "settings_invokes": page.evaluate("window.__brandInvokes")}
            (ROOT / "docs/winsight-ui-checks.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            context.close()
            browser.close()
            print("WinSight setup/icon, folder-step navigation, settings tabs and disabled updates verified; no page errors")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
