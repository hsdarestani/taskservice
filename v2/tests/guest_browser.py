"""Customer discovery and an actual API-backed three-step guest submission."""
import base64
import os
from pathlib import Path
from uuid import uuid4

from playwright.sync_api import expect, sync_playwright

base = os.environ["TASKSERVICE_BROWSER_BASE"]
if not base.startswith(("https://localhost:", "https://127.0.0.1:")):
    raise ValueError("Run guest acceptance only against an isolated local site.")
tenant = os.environ.get("TASKSERVICE_BROWSER_TENANT", "lab")
output = Path(os.environ.get("TASKSERVICE_BROWSER_OUTPUT", "."))
output.mkdir(parents=True, exist_ok=True)
png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+lpb8AAAAASUVORK5CYII=")

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(
        executable_path=os.environ.get("TASKSERVICE_CHROMIUM"),
        args=["--no-sandbox", "--ignore-certificate-errors"],
    )
    for width, height in ((390, 844), (768, 1024), (1366, 900)):
        page = browser.new_page(viewport={"width": width, "height": height}, ignore_https_errors=True)
        page.goto(f"{base}/unternehmen/{tenant}", wait_until="domcontentloaded")
        expect(page.get_by_role("heading", name="Services für dein Zuhause")).to_be_visible()
        tiles = page.locator(".service-tile")
        expect(tiles).to_have_count(6)
        page.evaluate("""async () => {
            await Promise.all([...document.images].map(image => {
                image.loading = 'eager';
                return image.decode();
            }));
        }""")
        assert page.evaluate("document.documentElement.scrollWidth") <= width
        page.screenshot(path=output / f"v2-home-{width}.png", full_page=True)
        search = page.get_by_role("searchbox", name="Service suchen")
        search.fill("Wasserhahn")
        expect(tiles).to_have_count(1)
        expect(tiles.first).to_have_text("Reparaturen")
        search.fill("Suchbegriff ohne Treffer")
        expect(page.get_by_text("wurde kein Service gefunden", exact=False)).to_be_visible()
        page.get_by_role("button", name="Suche zurücksetzen").click()
        expect(tiles).to_have_count(6)

        entry = page.get_by_role("button", name="Reparaturen", exact=True)
        entry.focus()
        entry.press("Enter")
        dialog = page.get_by_role("dialog")
        expect(dialog.get_by_role("heading", name="Was muss erledigt werden?")).to_be_focused()
        page.keyboard.press("Escape")
        expect(dialog).to_have_count(0)
        expect(entry).to_be_focused()
        entry.click()
        expect(dialog.get_by_role("heading", name="Was muss erledigt werden?")).to_be_visible()
        expect(dialog.get_by_role("radio", name="Reparaturen")).to_be_checked()
        page.screenshot(path=output / f"v2-wizard-{width}.png")
        dialog.get_by_role("button", name="Weiter").click()
        expect(dialog.get_by_text("Schritt 1 von 3", exact=True)).to_be_visible()

        subject = f"Prüfanfrage {uuid4().hex[:8]}"
        dialog.get_by_label("Titel deiner Anfrage").fill(subject)
        dialog.get_by_role("button", name="Weiter").click()
        expect(dialog.get_by_text("Schritt 2 von 3", exact=True)).to_be_visible()
        dialog.get_by_label("Dein Anliegen").fill("Synthetische Anfrage zur Prüfung des Bilderuploads.")
        dialog.get_by_label("Fotos auswählen").set_input_files({"name": "test.txt", "mimeType": "text/plain", "buffer": b"synthetic"})
        expect(dialog.get_by_role("alert")).to_contain_text("Bis zu 3 Fotos")
        dialog.get_by_label("Fotos auswählen").set_input_files({"name": "Testfoto.png", "mimeType": "image/png", "buffer": png})
        expect(dialog.get_by_text("Testfoto.png", exact=False)).to_be_visible()
        dialog.get_by_role("button", name="Zurück").click()
        expect(dialog.get_by_label("Titel deiner Anfrage")).to_have_value(subject)
        dialog.get_by_role("button", name="Weiter").click()
        expect(dialog.get_by_text("Testfoto.png", exact=False)).to_be_visible()
        dialog.get_by_role("button", name="Weiter").click()
        expect(dialog.get_by_text("Schritt 3 von 3", exact=True)).to_be_visible()
        dialog.get_by_label("Vor- und Nachname").fill("Test Kunde")
        dialog.get_by_label("Telefonnummer").fill("+49 123456789")
        dialog.get_by_label("E-Mail-Adresse").fill("qa@example.invalid")
        dialog.get_by_role("checkbox").check()
        with page.expect_response(lambda r: r.url.endswith("/requests") and r.request.method == "POST") as hit:
            dialog.get_by_role("button", name="Anfrage absenden").click()
        assert hit.value.status == 201, (hit.value.status, hit.value.text())
        assert hit.value.request.post_data_json["photos"][0]["mime"] == "image/png"
        expect(dialog.get_by_role("heading", name="Deine Anfrage ist eingegangen.")).to_be_visible()

        # TaskService One: unauthenticated visitors never see privileged menus or forms.
        for forbidden in ("/plattform", f"/unternehmen/{tenant}/betrieb", f"/unternehmen/{tenant}/technik"):
            page.goto(base + forbidden, wait_until="domcontentloaded")
            expect(page.get_by_role("heading", name="Im Arbeitsbereich anmelden")).to_be_visible()
            expect(page.locator(".workspace-sidebar")).to_have_count(0)
            expect(page.locator(".workspace-signout")).to_have_count(0)
            expect(page.locator(".demo-bar nav")).to_have_count(0)
            assert page.evaluate("document.documentElement.scrollWidth") <= width
            name = forbidden.strip("/").replace("/", "-")
            page.screenshot(path=output / f"v2-access-{name}-{width}.png", full_page=True)
        page.close()
    browser.close()
print("Service catalog, search, three wizard steps, validation and photo submission passed at 390, 768 and 1366 pixels.")
