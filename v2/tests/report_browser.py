"""Exercise the report workflow against a disposable V2 app served by the .NET test assembly."""
import os
from time import monotonic, sleep
from pathlib import Path

from playwright.sync_api import expect, sync_playwright

origin = os.environ["TASKSERVICE_BROWSER_BASE"]
if not origin.startswith("https://127.0.0.1:"):
    raise ValueError("The report acceptance scenario must use the loopback test server.")

path = "/unternehmen/lab/anfragen/" + os.environ["TASKSERVICE_BROWSER_REQUEST"]
output = Path(os.environ.get("TASKSERVICE_BROWSER_OUTPUT", "."))
output.mkdir(parents=True, exist_ok=True)

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(executable_path=os.environ.get("TASKSERVICE_CHROMIUM"), args=["--no-sandbox"])

    def open_as(subject, privileged=False, mobile=False):
        headers = {"X-Test-Subject": subject}
        if privileged:
            headers["X-Test-Mfa"] = "1"
        context = browser.new_context(
            viewport={"width": 390, "height": 844} if mobile else {"width": 1366, "height": 900},
            ignore_https_errors=True,
            extra_http_headers=headers,
        )
        page = context.new_page()
        page.goto(origin + path, wait_until="domcontentloaded")
        panel = page.locator("section").filter(has=page.get_by_role("heading", name="Arbeitsbericht", exact=True))
        expect(panel.get_by_role("heading")).to_be_visible()
        return context, page, panel

    context, page, panel = open_as("tech-a", mobile=True)
    panel.get_by_label("Tätigkeit 1").fill("Gerät geprüft und Dichtung gewechselt")
    panel.get_by_label("Dauer in Minuten").fill("45")
    panel.get_by_label("Ergebnis").fill("Gerät funktioniert wieder.")
    panel.get_by_label("Material").fill("Eine Dichtung")
    assert page.evaluate("document.documentElement.scrollWidth") <= 390
    panel.screenshot(path=output / "report-technician-mobile.png")
    panel.get_by_role("button", name="Bericht einreichen").click()
    expect(panel.get_by_text("Zur Prüfung eingereicht", exact=False).first).to_be_visible()
    context.close()

    context, page, panel = open_as("report-reviewer", privileged=True)
    assert page.evaluate("document.documentElement.scrollWidth") <= 1366
    expect(panel.get_by_role("button", name="Bericht genehmigen")).to_be_enabled()
    panel.locator("summary").first.click()
    panel.screenshot(path=output / "report-review-desktop.png")
    with page.expect_response(lambda r: r.url.endswith("/report/commands") and r.request.method == "POST") as review:
        panel.get_by_role("button", name="Bericht genehmigen").click()
    assert review.value.ok, (review.value.status, review.value.text())
    expect(panel.get_by_text("Version 1 · Geprüft", exact=True).first).to_be_visible()
    context.close()

    context, page, panel = open_as("report-publisher", privileged=True)
    publish = panel.get_by_role("button", name="Für den Kunden veröffentlichen")
    deadline = monotonic() + 10
    while not publish.is_visible() and monotonic() < deadline:
        refresh = panel.get_by_role("button", name="Bericht aktualisieren")
        expect(refresh).to_be_visible()
        with page.expect_response(lambda r: r.url.endswith("/report")):
            refresh.click()
        sleep(0.25)
    expect(publish).to_be_enabled()
    with page.expect_response(lambda r: r.url.endswith("/report/commands") and r.request.method == "POST") as publication:
        publish.click()
    assert publication.value.ok, (publication.value.status, publication.value.text())
    expect(panel.get_by_text("Version 1 · Veröffentlicht", exact=True).first).to_be_visible()
    context.close()

    context, page, panel = open_as("customer-a", mobile=True)
    download = panel.get_by_role("link", name="Arbeitsbericht als PDF herunterladen")
    expect(download).to_be_visible()
    panel.screenshot(path=output / "report-customer-mobile.png")
    response = context.request.get(origin + download.get_attribute("href"))
    assert response.ok and response.body().startswith(b"%PDF-")
    assert response.headers["cache-control"] == "no-store"
    context.close()
    browser.close()

print("Report browser test passed for technician, reviewer, publisher and customer.")
