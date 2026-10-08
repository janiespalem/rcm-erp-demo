"""Optional real-browser contract test; API responses are synthetic, no production access.

RUN_ORDER_CONFLICT_BROWSER=1 python -m pytest tests/test_order_conflict_browser.py -q
Requires a built frontend and playwright with Chromium installed.
"""
import json
import os
import threading
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

if os.getenv("RUN_ORDER_CONFLICT_BROWSER") != "1":
    pytest.skip("Opt-in order conflict browser test", allow_module_level=True)

from playwright.sync_api import sync_playwright, expect


def test_conflict_keeps_draft_and_reopen_uses_current_version():
    directory = Path(__file__).resolve().parents[1] / "frontend-vite" / "dist"
    server = ThreadingHTTPServer(("127.0.0.1", 0), partial(SimpleHTTPRequestHandler, directory=str(directory)))
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    order = {"id": 1, "version_id": 1, "client": "Synthetic customer", "order_number": "1/2026",
             "deadline": "2026-12-31", "status": "in_production", "quantity": 1, "order_type": "remont"}
    patches = []

    def respond(route):
        path = route.request.url.split("/api", 1)[1]
        data, status = [], 200
        if path == "/auth/login":
            data = {"role": "technolog", "name": "Synthetic user", "access_token": "test-only"}
        elif path == "/orders/1" and route.request.method == "PATCH":
            payload = route.request.post_data_json
            patches.append(payload)
            if payload["version_id"] == 1:
                order.update(version_id=2, client="Other user's change")
                data, status = {"detail": "Zlecenie zostało zmienione"}, 409
            else:
                order.update(payload)
                order["version_id"] += 1
                data = order
        elif path == "/orders/1":
            data = order
        elif path.rstrip("/") == "/orders":
            data = [order]
        route.fulfill(status=status, content_type="application/json", body=json.dumps(data))

    try:
        with sync_playwright() as p:
            browser = p.chromium.launch()
            page = browser.new_page()
            page.route("**/api/**", respond)
            page.goto(f"http://127.0.0.1:{server.server_port}")
            page.locator("select").select_option("technolog")
            page.locator('input[type="password"]').fill("1234")
            page.get_by_role("button", name="Zaloguj się").click()
            page.get_by_text("Synthetic customer", exact=True).first.click()
            page.get_by_role("button", name="Edytuj", exact=True).click()
            field = page.get_by_placeholder("Nazwa klienta", exact=True)
            field.fill("My unsaved draft")
            page.get_by_role("button", name="Zapisz zmiany").click()
            expect(page.get_by_role("alert")).to_contain_text("Anuluj")
            expect(field).to_have_value("My unsaved draft")
            page.get_by_role("button", name="Zapisz zmiany").click()
            expect(field).to_have_value("My unsaved draft")
            assert [p["version_id"] for p in patches] == [1, 1]
            page.get_by_role("button", name="Anuluj", exact=True).click()
            page.get_by_role("button", name="Edytuj", exact=True).click()
            expect(field).to_have_value("Other user's change")
            field.fill("Reviewed change")
            page.get_by_role("button", name="Zapisz zmiany").click()
            expect(field).not_to_be_visible()
            assert patches[-1]["version_id"] == 2
            browser.close()
    finally:
        server.shutdown()
        server.server_close()
        thread.join(5)
