"""Exercise invoice upload, review, publication and original download with synthetic data."""
import os
from pathlib import Path
from time import monotonic, sleep

from playwright.sync_api import expect, sync_playwright

origin = os.environ["TASKSERVICE_BROWSER_BASE"]
if not origin.startswith("https://127.0.0.1:"):
    raise ValueError("The invoice acceptance scenario must use the loopback test server.")
path = "/unternehmen/lab/anfragen/" + os.environ["TASKSERVICE_BROWSER_REQUEST"]
output = Path(os.environ.get("TASKSERVICE_BROWSER_OUTPUT", "."))
output.mkdir(parents=True, exist_ok=True)
original = b'<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"><Synthetic>QA</Synthetic></Invoice>'

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(executable_path=os.environ.get("TASKSERVICE_CHROMIUM"), args=["--no-sandbox"])

    def open_as(subject, privileged=False, mobile=False):
        headers = {"X-Test-Subject": subject}
        if privileged:
            headers["X-Test-Mfa"] = "1"
        context = browser.new_context(
            viewport={"width": 390, "height": 844} if mobile else {"width": 1366, "height": 900},
            ignore_https_errors=True, extra_http_headers=headers,
        )
        page = context.new_page()
        page.goto(origin + path, wait_until="domcontentloaded")
        panel = page.locator("section").filter(has=page.get_by_role("heading", name="Rechnungen", exact=True))
        expect(panel.get_by_role("heading")).to_be_visible()
        return context, page, panel

    def click_command(page, button, path_part, status):
        with page.expect_response(lambda r: path_part in r.url and r.request.method == "POST") as result:
            button.click()
        assert result.value.status == status, (result.value.status, result.value.text())

    context, page, panel = open_as("invoice-uploader", privileged=True)
    panel.locator('input[type="file"]').set_input_files(
        {"name": "Rechnung-QA.xml", "mimeType": "application/xml", "buffer": original}
    )
    click_command(page, panel.get_by_role("button", name="Datei hochladen"), "/invoices/upload", 201)
    expect(panel.get_by_text("Rechnung-QA.xml", exact=True)).to_be_visible()
    context.close()

    context, page, panel = open_as("report-reviewer", privileged=True)
    approve = panel.get_by_role("button", name="Rechnung genehmigen")
    deadline = monotonic() + 10
    while not approve.is_visible() and monotonic() < deadline:
        refresh = panel.get_by_role("button", name="Rechnungen aktualisieren")
        expect(refresh).to_be_visible()
        with page.expect_response(lambda r: r.url.endswith("/invoices")):
            refresh.click()
        sleep(0.25)
    expect(approve).to_be_visible()
    expect(panel.get_by_role("link", name="Original herunterladen")).to_be_visible()
    panel.screenshot(path=output / "invoice-review-desktop.png")
    click_command(page, approve, "/commands", 200)
    expect(panel.get_by_text("Genehmigt", exact=False)).to_be_visible()
    context.close()

    context, page, panel = open_as("report-publisher", privileged=True)
    click_command(page, panel.get_by_role("button", name="Für den Kunden veröffentlichen"), "/commands", 200)
    expect(panel.get_by_text("Veröffentlicht", exact=False)).to_be_visible()
    context.close()

    context, page, panel = open_as("customer-a", mobile=True)
    download = panel.get_by_role("link", name="Original herunterladen")
    expect(download).to_be_visible()
    assert page.evaluate("document.documentElement.scrollWidth") <= 390
    panel.screenshot(path=output / "invoice-customer-mobile.png")
    response = context.request.get(origin + download.get_attribute("href"))
    assert response.ok and response.body() == original
    assert response.headers["cache-control"] == "no-store"
    context.close()
    browser.close()

print("Invoice browser test passed for uploader, reviewer, publisher and customer.")
