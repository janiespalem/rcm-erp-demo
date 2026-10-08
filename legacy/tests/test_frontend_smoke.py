"""Optional Playwright browser smoke tests.

Always-on build artifact checks live in tests/test_build.py.
Set RUN_FRONTEND_SMOKE=1 to run browser tests (requires playwright + chromium).
"""
import os
import pathlib

import pytest

if os.getenv("RUN_FRONTEND_SMOKE") != "1":
    pytest.skip(
        "Browser smoke tests skipped — set RUN_FRONTEND_SMOKE=1 to enable.",
        allow_module_level=True,
    )

pytest.importorskip("playwright.sync_api")
from playwright.sync_api import sync_playwright

VITE_INDEX = pathlib.Path(__file__).resolve().parents[1] / "frontend-vite" / "dist" / "index.html"


def test_frontend_roles_have_visible_navigation():
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch()
        page = browser.new_page(viewport={"width": 1440, "height": 900})
        page.goto(VITE_INDEX.as_uri())

        page.get_by_text("FactoryFlow ERP Demo").first.wait_for(state="visible")
        assert page.get_by_text("Biuro").count() > 0
        assert page.get_by_text("Technolog").count() > 0
        assert page.get_by_text("CEO").count() > 0
        assert page.locator("body").bounding_box()["height"] > 200

        browser.close()
