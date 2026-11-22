"""Exercise the report workflow against a disposable V2 app served by the .NET test assembly."""
import os
from datetime import datetime, timedelta
from zoneinfo import ZoneInfo
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
    browser = playwright.chromium.launch(
        executable_path=os.environ.get("TASKSERVICE_CHROMIUM"),
        args=["--no-sandbox", "--ignore-certificate-errors"],
    )

    platform = browser.new_context(viewport={"width": 1366, "height": 900}, ignore_https_errors=True,
        extra_http_headers={"X-Test-Subject": "platform-admin", "X-Test-Mfa": "1"})
    platform_page = platform.new_page()
    platform_page.goto(origin + "/plattform", wait_until="domcontentloaded")
    expect(platform_page.get_by_role("heading", name="Plattformübersicht")).to_be_visible()
    platform_page.get_by_role("link", name="Unternehmen", exact=True).click()
    setup = platform_page.locator("section").filter(has=platform_page.get_by_role("heading", name="Neues Unternehmen"))
    # The new design keeps provisioning collapsed until explicitly requested.
    setup.locator("details.company-create > summary").click()
    slug = "qa-browser-" + os.environ["TASKSERVICE_BROWSER_REQUEST"][:8]
    setup.get_by_label("Portaladresse", exact=False).fill(slug)
    setup.get_by_label("Unternehmensname").fill("Servicewerkstatt Browserprüfung")
    with platform_page.expect_response(lambda response: response.url.endswith("/platform/companies") and
        response.request.method == "POST") as creation:
        setup.get_by_role("button", name="Unternehmen anlegen").click()
    assert creation.value.status == 201, (creation.value.status, creation.value.text())
    company = platform_page.locator("details.company-record").filter(
        has=platform_page.get_by_text("Servicewerkstatt Browserprüfung", exact=True))
    expect(company).to_be_visible()
    company.locator("summary").click()
    platform_page.get_by_role("button", name="Ersteinrichtung anzeigen").click()
    platform_page.get_by_label("Name der Administration").fill("Test Administration")
    platform_page.get_by_label("E-Mail-Adresse der Administration").fill("browser-initial@example.invalid")
    with platform_page.expect_response(lambda r: r.url.endswith("/setup") and r.request.method == "POST") as onboarding:
        platform_page.get_by_role("button", name="Erste Einladung erstellen").click()
    assert onboarding.value.status == 201
    # Invitations are audited in their own dedicated section, not appended below the company editor.
    platform_page.get_by_role("link", name="Änderungsprotokoll", exact=True).click()
    expect(platform_page.get_by_text("Erste Administration eingeladen", exact=True)).to_be_visible()
    platform_page.get_by_role("link", name="Unternehmen", exact=True).click()
    for width, height in ((390, 844), (768, 1024), (1366, 900)):
        platform_page.set_viewport_size({"width": width, "height": height})
        assert platform_page.evaluate("document.documentElement.scrollWidth") <= width
        platform_page.screenshot(path=output / f"platform-companies-{width}.png")
        if width <= 800:
            platform_page.get_by_label("Bereich auswählen").select_option("overview")
        else:
            platform_page.get_by_role("link", name="Übersicht", exact=True).click()
        expect(platform_page.get_by_role("heading", name="Plattformübersicht")).to_be_visible()
        assert platform_page.evaluate("document.documentElement.scrollWidth") <= width
        platform_page.screenshot(path=output / f"platform-dashboard-{width}.png")
        if width <= 800:
            platform_page.get_by_label("Bereich auswählen").select_option("companies")
        else:
            platform_page.get_by_role("link", name="Unternehmen", exact=True).click()
        expect(platform_page.get_by_role("heading", name="Unternehmen", exact=True).first).to_be_visible()
    platform.close()

    for role, route, heading in (
        ("admin-a", "betrieb", "Serviceübersicht"), ("tech-a", "technik", "Meine Einsätze"),
        ("customer-a", "konto", "Meine Anfragen"),
    ):
        for width, height in ((390, 844), (768, 1024), (1366, 900)):
            headers = {"X-Test-Subject": role}
            if route == "betrieb":
                headers["X-Test-Mfa"] = "1"
            desk = browser.new_context(viewport={"width": width, "height": height}, ignore_https_errors=True,
                extra_http_headers=headers)
            desk_page = desk.new_page()
            desk_page.goto(origin + "/unternehmen/lab/" + route, wait_until="domcontentloaded")
            expect(desk_page.get_by_role("heading", name=heading, exact=True)).to_be_visible()
            expect(desk_page.get_by_role("link", name="Testvorgang", exact=False).first).to_be_visible()
            assert desk_page.evaluate("document.documentElement.scrollWidth") <= width
            if route == "betrieb":
                desk_page.get_by_label("Vorgang suchen").fill("Testvorgang")
                desk_page.get_by_role("button", name="Suchen", exact=True).click()
                expect(desk_page.get_by_role("link", name="Testvorgang", exact=False).first).to_be_visible()
                desk_page.get_by_label("Sortierung").select_option("title")
                expect(desk_page.get_by_role("table")).to_be_visible()
                desk_page.get_by_label("Vorgang suchen").clear()
                expect(desk_page.get_by_role("link", name="Testvorgang", exact=False).first).to_be_visible()
                if width == 1366:
                    desk_page.screenshot(path=output / "betrieb-overview-desktop.png")
                    desk_page.get_by_role("link", name="Team und Rechte", exact=True).click()
                    team = desk_page.locator("#team")
                    team.get_by_label("Name", exact=True).fill("Test Teammitglied")
                    team.get_by_label("Geschäftliche E-Mail-Adresse").fill("browser-staff@example.invalid")
                    with desk_page.expect_response(lambda r: r.url.endswith("/team/invitations") and r.request.method == "POST") as invite:
                        team.get_by_role("button", name="Einladung erstellen").click()
                    assert invite.value.status == 201
                    expect(team.get_by_text("Test Teammitglied", exact=True)).to_be_visible()
                    team.screenshot(path=output / "team-admin-desktop.png")
                    team.get_by_role("button", name="Widerrufen").click()
                    expect(team.get_by_text("Einladung widerrufen.", exact=True)).to_be_visible()
                    desk_page.evaluate("window.scrollTo(0, 0)")
            desk_page.screenshot(path=output / f"{route}-dashboard-{width}.png")
            desk.close()

    # A valid session of a different role must not expose another workspace shell.
    for actor, denied_route in (
        ("tech-a", "/unternehmen/lab/betrieb"),
        ("admin-a", "/unternehmen/lab/technik"),
        ("customer-a", "/unternehmen/lab/betrieb"),
        ("admin-a", "/plattform"),
    ):
        context = browser.new_context(viewport={"width": 390, "height": 844}, ignore_https_errors=True,
            extra_http_headers={"X-Test-Subject": actor, "X-Test-Mfa": "1"})
        denied_page = context.new_page()
        denied_page.goto(origin + denied_route, wait_until="domcontentloaded")
        expect(denied_page.get_by_role("heading", name="Kein Zugriff auf diesen Bereich")).to_be_visible()
        expect(denied_page.locator(".workspace-sidebar")).to_have_count(0)
        expect(denied_page.locator(".workspace-mobile-choose")).to_have_count(0)
        assert denied_page.evaluate("document.documentElement.scrollWidth") <= 390
        context.close()


    tabs = {"overview": "Überblick", "actions": "Bearbeiten", "report": "Arbeitsbericht",
            "invoices": "Rechnungen", "messages": "Nachrichten", "internal": "Interne Notizen",
            "timeline": "Verlauf"}

    def show_tab(page, tab):
        if page.viewport_size["width"] <= 780:
            page.get_by_label("Bereich des Servicevorgangs").select_option(tab)
        else:
            page.get_by_role("tab", name=tabs[tab], exact=True).click()

    def open_as(subject, privileged=False, mobile=False, tab="report"):
        headers = {"X-Test-Subject": subject}
        if privileged:
            headers["X-Test-Mfa"] = "1"
        context = browser.new_context(
            viewport={"width": 390, "height": 844} if mobile else {"width": 1366, "height": 900},
            ignore_https_errors=True,
            extra_http_headers=headers,
        )
        page = context.new_page()
        page.set_default_timeout(10000)
        workspace_path = path.replace("/lab/anfragen/", "/lab/betrieb/anfragen/") if privileged else (
            path.replace("/lab/anfragen/", "/lab/technik/anfragen/") if subject == "tech-a" else path
        )
        page.goto(origin + workspace_path, wait_until="domcontentloaded")
        show_tab(page, tab)
        panel = page.locator("section").filter(has=page.get_by_role("heading", name="Arbeitsbericht", exact=True))
        if tab == "report":
            expect(panel.get_by_role("heading")).to_be_visible()
        return context, page, panel


    # Full-role visual and responsive audit. No request for manually supplied screenshots:
    # every major request view is captured from the synthetic browser fixture.
    for role, staff, allowed_tabs in (
        ("admin-a", True, ("overview", "actions", "report", "invoices", "messages", "internal", "timeline")),
        ("tech-a", False, ("overview", "actions", "report", "messages", "internal", "timeline")),
        ("customer-a", False, ("overview", "actions", "report", "invoices", "messages", "timeline")),
    ):
        for width, height in ((390, 844), (768, 1024), (1366, 900)):
            headers = {"X-Test-Subject": role}
            if staff:
                headers["X-Test-Mfa"] = "1"
            context = browser.new_context(viewport={"width": width, "height": height},
                ignore_https_errors=True, extra_http_headers=headers)
            page = context.new_page()
            workspace_path = path.replace("/lab/anfragen/", "/lab/betrieb/anfragen/") if staff else (
                path.replace("/lab/anfragen/", "/lab/technik/anfragen/") if role == "tech-a" else path
            )
            page.goto(origin + workspace_path, wait_until="domcontentloaded")
            expect(page.locator(".request-workspace-title h1")).to_be_visible()
            for chosen in allowed_tabs:
                # Only verify authorized UI sections; a user cannot access internal notes
                # if the API has not granted that claim.
                available = page.locator("#ticket-tab-" + chosen)
                if available.count() == 0:
                    continue
                show_tab(page, chosen)
                expect(page.locator("#ticket-panel")).to_be_visible()
                assert page.evaluate("document.documentElement.scrollWidth") <= width, (role, chosen, width)
                page.screenshot(path=output / f"request-{role}-{chosen}-{width}.png", full_page=True)
            if role == "customer-a":
                assert page.locator("#ticket-tab-internal").count() == 0
            context.close()

    def send_request_command(page, action, button_name="Änderung speichern"):
        commands = page.locator("section").filter(has=page.get_by_role("heading", name="Vorgang bearbeiten", exact=True))
        commands.get_by_label("Aktion", exact=True).select_option(action)
        with page.expect_response(lambda r: r.url.endswith("/commands") and "/report/" not in r.url) as response:
            commands.get_by_role("button", name=button_name, exact=True).click()
        assert response.value.ok, (response.value.status, response.value.text())

    context, page, panel = open_as("admin-a", privileged=True, tab="actions")
    commands = page.locator("section").filter(has=page.get_by_role("heading", name="Vorgang bearbeiten", exact=True))
    commands.get_by_label("Aktion", exact=True).select_option("schedule")
    day = (datetime.now(ZoneInfo("Europe/Berlin")) + timedelta(days=4)).date()
    commands.get_by_label("Beginn (Berliner Zeit)").fill(f"{day}T09:00")
    commands.get_by_label("Ende (Berliner Zeit)").fill(f"{day}T10:00")
    commands.get_by_label("Hinweis für den Kunden").fill("Bitte den Eingang freihalten.")
    send_request_command(page, "schedule")
    show_tab(page, "overview")
    expect(page.get_by_text("Bitte den Eingang freihalten.", exact=True)).to_be_visible()
    context.close()

    context, page, panel = open_as("customer-a", mobile=True, tab="actions")
    send_request_command(page, "confirm_appointment", "Termin bestätigen")
    show_tab(page, "overview")
    expect(page.get_by_text("Bestätigt am", exact=False)).to_be_visible()
    page.locator("section").filter(has=page.get_by_role("heading", name="Einsatztermin", exact=True)).screenshot(
        path=output / "appointment-customer-mobile.png"
    )
    context.close()

    context, page, panel = open_as("tech-a", mobile=True, tab="actions")
    commands = page.locator("section").filter(has=page.get_by_role("heading", name="Vorgang bearbeiten", exact=True))
    commands.get_by_label("Aktion", exact=True).select_option("arrival")
    commands.get_by_label("Anfahrt").select_option("arrived")
    send_request_command(page, "arrival")
    show_tab(page, "overview")
    expect(page.get_by_text("Vor Ort", exact=True)).to_be_visible()
    show_tab(page, "actions")
    send_request_command(page, "start")
    show_tab(page, "report")
    panel = page.locator("section").filter(has=page.get_by_role("heading", name="Arbeitsbericht", exact=True))
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
    manifest_url = page.locator('link[rel="manifest"]').get_attribute("href")
    manifest = context.request.get(origin + manifest_url)
    assert manifest.ok and manifest.json()["start_url"] == "/unternehmen/lab"
    deadline = monotonic() + 8
    while not page.evaluate("navigator.serviceWorker.controller !== null") and monotonic() < deadline:
        sleep(0.2)
    if not page.evaluate("navigator.serviceWorker.controller !== null"):
        diagnostic = page.evaluate("""async () => {
            try {
                const registration = await navigator.serviceWorker.register('/sw.js');
                return registration.active?.state ?? registration.installing?.state ?? 'Waiting';
            } catch (error) { return error.message; }
        }""")
        raise AssertionError(f"The TaskService service worker did not activate: {diagnostic}")
    context.set_offline(True)
    page.reload(wait_until="domcontentloaded")
    expect(page.get_by_role("heading", name="Keine Internetverbindung")).to_be_visible()
    cached = page.evaluate("""async () => {
        const entries = await Promise.all((await caches.keys()).map(async key => (await (await caches.open(key)).keys()).map(r => new URL(r.url).pathname)));
        return entries.flat();
    }""")
    assert "/offline.html" in cached
    assert not any(p.startswith(("/api/", "/unternehmen/", "/hubs/")) for p in cached)
    context.close()
    browser.close()

print("Appointment scheduling, customer confirmation, technician status, report review and customer download passed in browser.")
