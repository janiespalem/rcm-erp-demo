"""Capture README screenshots against a fresh, synthetic, temporary database.

Run with the demo virtualenv after building the frontend and installing Chromium.
Never connects to an existing database or a remote application.
"""
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

import httpx
from playwright.sync_api import expect, sync_playwright

ROOT = Path(__file__).resolve().parents[1]
SHOTS = ROOT / "docs" / "screenshots"


def main():
    with tempfile.TemporaryDirectory(prefix="factoryflow-screenshots-") as tmp:
        env = {**os.environ, "APP_ENV": "dev", "DISABLE_STARTUP_SEED": "0",
               "DATABASE_URL": f"sqlite:///{tmp}/demo.db"}
        subprocess.run([sys.executable, "-m", "alembic", "upgrade", "head"],
                       cwd=ROOT, env=env, check=True)
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        url = f"http://127.0.0.1:{port}"
        server = subprocess.Popen(
            [sys.executable, "-m", "uvicorn", "main:app", "--host", "127.0.0.1", "--port", str(port)],
            cwd=ROOT / "backend", env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        try:
            for _ in range(100):
                try:
                    if httpx.get(url + "/api/health", timeout=1).is_success:
                        break
                except httpx.HTTPError:
                    pass
                if server.poll() is not None:
                    raise RuntimeError("Demo server exited during startup")
                time.sleep(0.1)
            else:
                raise RuntimeError("Demo server did not become healthy")
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page(viewport={"width": 1366, "height": 900})
                page.goto(url)
                page.screenshot(path=str(SHOTS / "login.png"))
                page.locator("select").select_option("produkcja")
                page.locator('input[type=password]').fill("4444")
                page.get_by_role("button", name="Zaloguj się").click()
                page.get_by_role("button", name="Dzisiejszy raport", exact=True).click()
                for label, value in [
                    ("Ilość osób", "3"), ("Form złożonych", "12"),
                    ("Form przygotowanych do zalania", "12"),
                    ("Gwiazdobloków zalanych na zmianie", "12"),
                    ("Form sprawdzonych po ok. 20 minutach", "12"),
                    ("Prefabrykatów wstępnie rozebranych", "10"),
                    ("Uszkodzone — szt.", "0"),
                    ("Kierownik / osoba kontrolująca", "Demo Controller"),
                ]:
                    page.get_by_label(label, exact=True).fill(value)
                page.get_by_label("Wibratory", exact=True).select_option("sprawne")
                page.get_by_label("Przedłużacze", exact=True).select_option("sprawne")
                for button in page.get_by_role("button", name="OK", exact=True).all():
                    button.click()
                page.get_by_label("Uwagi ogólne", exact=True).fill("Fikcyjny raport demonstracyjny — nie jest dokumentem produkcyjnym.")
                expect(page.get_by_role("status")).to_contain_text("Zapisano")
                page.set_viewport_size({"width": 390, "height": 844})
                page.evaluate("window.scrollTo(0, 0)")
                assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
                page.screenshot(path=str(SHOTS / "shift-mobile.png"))
                page.get_by_role("button", name="Zakończ raport", exact=True).click()
                expect(page.get_by_role("button", name="Koryguj raport", exact=True)).to_be_visible()
                page.set_viewport_size({"width": 1366, "height": 1000})
                page.get_by_role("button", name="Podgląd A4", exact=True).click()
                page.set_viewport_size({"width": 794, "height": 1123})
                page.emulate_media(media="print")
                page.locator(".shift-print").screenshot(path=str(SHOTS / "shift-report.png"))
                browser.close()
            print("Synthetic demo login, mobile autosave, finalization and A4 verified; screenshots saved.")
        finally:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()


if __name__ == "__main__":
    main()
