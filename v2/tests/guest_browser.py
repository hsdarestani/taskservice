"""Browser acceptance for the guest intake against an isolated running V2 instance."""
import os
from pathlib import Path
from uuid import uuid4

from playwright.sync_api import expect, sync_playwright

base = os.environ.get("TASKSERVICE_BROWSER_BASE", "https://localhost:17443")
if not base.startswith(("https://localhost:", "https://127.0.0.1:")):
    raise ValueError("The guest smoke test requires an isolated local site.")
tenant = os.environ.get("TASKSERVICE_BROWSER_TENANT", "werkstatt-nord")
output = Path(os.environ.get("TASKSERVICE_BROWSER_OUTPUT", "."))
output.mkdir(parents=True, exist_ok=True)

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(
        executable_path=os.environ.get("TASKSERVICE_CHROMIUM"),
        args=["--no-sandbox"],
    )
    for width, height in ((390, 844), (1366, 900)):
        page = browser.new_page(viewport={"width": width, "height": height}, ignore_https_errors=True)
        page.goto(f"{base}/unternehmen/{tenant}", wait_until="networkidle")
        expect(page.get_by_role("heading", name="Ihr Servicekontakt")).to_be_visible()
        expect(page.get_by_role("navigation")).to_be_visible()
        assert page.evaluate("document.documentElement.scrollWidth") <= width
        page.screenshot(path=output / f"guest-{width}.png", full_page=True)
        page.get_by_label("Betreff").fill(f"Prüfanfrage {uuid4().hex[:8]}")
        page.get_by_label("E-Mail-Adresse").fill("qa@example.invalid")
        page.get_by_label("Beschreibung").fill("Synthetische Anfrage zur Browserprüfung.")
        with page.expect_response(lambda r: r.url.endswith("/requests") and r.request.method == "POST") as hit:
            page.get_by_role("button", name="Anfrage absenden").click()
        assert hit.value.status == 201, (hit.value.status, hit.value.text())
        expect(page.get_by_text("Ihre Anfrage ist eingegangen.", exact=False)).to_be_visible()
        expect(page.get_by_label("Betreff")).to_have_value("")
        page.close()
    browser.close()
print("Guest intake passed at 390 and 1366 pixels.")
