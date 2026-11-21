"""Exercise the native demo on the canonical HTTPS origin using synthetic data."""
import base64
import datetime as dt
import hmac
import json
import os
from pathlib import Path
import time
from urllib.parse import quote
from uuid import uuid4

from playwright.sync_api import expect, sync_playwright

ORIGIN = "https://taskservice.smarbiz.sbs"
TENANT = "/api/v1/tenants/demo"
SECRET_DIR = Path(os.environ["TASKSERVICE_DEMO_QA_CREDENTIALS"])
PHOTO = Path(__file__).resolve().parents[1] / "web/public/services/repair.webp"


def code_at(secret):
    step = int(time.time()) // 30
    digest = hmac.digest(bytes.fromhex(secret), step.to_bytes(8, "big"), "sha1")
    offset = digest[-1] & 15
    return f"{(int.from_bytes(digest[offset:offset + 4], 'big') & 0x7fffffff) % 1000000:06d}"


def api(page, path, workforce=False, body=None, expected=200, revision=None, upload=None):
    result = page.evaluate("""async c => {
        const headers = c.workforce ? {'X-TaskService-Session':'workforce'} : {};
        const opts = {headers, redirect:'error', cache:'no-store'};
        if (c.body !== null || c.upload !== null) {
            const session = await (await fetch('/api/v1/session',opts)).json();
            headers['X-CSRF-Token'] = session.csrfToken;
            headers['Idempotency-Key'] = crypto.randomUUID();
            if (c.revision !== null) headers['If-Match'] = '"r' + c.revision + '"';
            opts.method = 'POST';
            if (c.upload !== null) {
                headers['Content-Type'] = 'application/xml';
                headers['X-Invoice-Name'] = 'Demo-Rechnung.xml';
                opts.body = c.upload;
            } else {
                headers['Content-Type'] = 'application/json';
                opts.body = JSON.stringify(c.body);
            }
        }
        const response = await fetch(c.path,opts);
        const type = response.headers.get('content-type') || '';
        let value = null;
        if (type.includes('json')) value = await response.json();
        else if (type.startsWith('image/')) value = (await response.arrayBuffer()).byteLength;
        else value = await response.text();
        return {status:response.status, etag:response.headers.get('etag'), value};
    }""", dict(path=path, workforce=workforce, body=body, revision=revision, upload=upload))
    if expected is not None:
        assert result["status"] == expected, f"{path.split('/')[-1]} HTTP {result['status']}, expected {expected}"
    return result


def sign_in(page, destination, email, password, seed=None, staff=False):
    target = ORIGIN + "/anmelden?returnTo=" + quote(destination)
    page.goto(target + ("&mode=workforce" if staff else ""), wait_until="domcontentloaded")
    page.get_by_label("E-Mail-Adresse").fill(email)
    page.get_by_label("Passwort", exact=True).fill(password)
    if seed:
        # Avoid crossing the end of a time step during form submission.
        if time.time() % 30 > 27:
            time.sleep(3)
        page.locator("input[name=code]").fill(code_at(seed))
    page.get_by_role("button", name="Anmelden", exact=True).click()
    page.wait_for_url(ORIGIN + destination)
    expect(page.get_by_role("complementary", name="Demoversion")).to_be_visible()


def staff_credentials(alias):
    return json.loads((SECRET_DIR / (alias + ".json")).read_text())


def revision(result):
    return int(result["etag"][2:-1])


def await_state(test, seconds=35):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = test()
        if value:
            return value
        time.sleep(1)
    raise AssertionError("Timed out waiting for the background operation")


def main():
    origin_ip = os.environ.get("TASKSERVICE_DEMO_ORIGIN_IP")
    args = []
    if origin_ip:
        import ipaddress
        args.append("--host-resolver-rules=MAP taskservice.smarbiz.sbs " + str(ipaddress.IPv4Address(origin_ip)))
        args.append("--no-proxy-server")
    nonce = uuid4().hex[:12]
    email = f"qa-{nonce}@taskservice-demo.invalid"
    password = "Qa-" + uuid4().hex + "!"
    with sync_playwright() as p:
        browser = p.chromium.launch(args=args)
        mobile = browser.new_context(viewport={"width": 390, "height": 844}, is_mobile=True, has_touch=True)
        customer = mobile.new_page()
        customer.set_default_timeout(12000)
        customer.goto(ORIGIN + "/anmelden?returnTo=%2Funternehmen%2Fdemo", wait_until="domcontentloaded")
        customer.get_by_role("button", name="Passwort vergessen?", exact=True).click()
        expect(customer.get_by_text("Diese Funktion ist in der Demoversion noch nicht verfügbar.", exact=True)).to_be_visible()
        customer.get_by_role("button", name="Zur Anmeldung", exact=True).click()
        customer.get_by_role("button", name="Kundenkonto erstellen", exact=True).click()
        customer.get_by_label("E-Mail-Adresse").fill(email)
        customer.get_by_label("Passwort", exact=True).fill(password)
        customer.get_by_role("button", name="Konto erstellen", exact=True).click()
        expect(customer.get_by_text("Wenn das Konto neu ist", exact=False)).to_be_visible()
        sign_in(customer, "/unternehmen/demo", email, password)
        expect(customer.get_by_role("heading", name="Services für dein Zuhause")).to_be_visible()
        customer.get_by_role("button", name="Reparaturen", exact=True).click()
        form = customer.get_by_role("dialog")
        title = "Automatisierter Demovorgang " + nonce
        form.get_by_label("Titel deiner Anfrage").fill(title)
        form.get_by_role("button", name="Weiter").click()
        form.get_by_label("Dein Anliegen").fill("Synthetische Testanfrage ohne personenbezogene Daten.")
        form.get_by_label("Fotos auswählen").set_input_files(PHOTO)
        form.get_by_role("button", name="Weiter").click()
        form.get_by_label("Vor- und Nachname").fill("Demo Testkonto")
        form.get_by_label("Telefonnummer").fill("00000")
        form.get_by_role("checkbox").check()
        form.get_by_role("button", name="Anfrage absenden").click()
        expect(form.get_by_role("heading", name="Deine Anfrage ist eingegangen.")).to_be_visible()
        listing = api(customer, TENANT + "/requests?view=mine")["value"]["items"]
        ticket = TENANT + "/requests/" + next(item["id"] for item in listing if item["title"] == title)
        details = api(customer, ticket)
        assert len(details["value"]["photos"]) == 1
        photo_url = ticket + "/photos/" + details["value"]["photos"][0]["id"]
        await_state(lambda: api(customer, photo_url, expected=None)["status"] == 200)
        print("Customer registration, recovery notice, mobile wizard and malware-scanned photo passed.")

        staff = {}
        for alias, path in (
            ("platform", "/plattform"), ("company", "/unternehmen/demo/betrieb"),
            ("technician", "/unternehmen/demo/technik"), ("reviewer", "/unternehmen/demo/betrieb"),
            ("other", "/unternehmen/demo-validation/betrieb")
        ):
            page = browser.new_context(viewport={"width": 1366, "height": 850}).new_page()
            creds = staff_credentials(alias)
            if alias == "platform":
                page.goto(ORIGIN + "/anmelden?mode=workforce")
                expect(page.get_by_label("Passwort", exact=True)).to_be_visible()
                api(page, "/api/v1/auth/login", True, dict(email=creds["email"], password=creds["password"],
                    workforce=True), expected=401)
            sign_in(page, path, creds["email"], creds["password"], creds.get("totpSecretHex"), staff=True)
            staff[alias] = page

        platform, company, technician, reviewer, other = (staff[x] for x in
            ("platform", "company", "technician", "reviewer", "other"))
        assert api(platform, "/api/v1/platform/overview", True)["value"]["items"]
        api(platform, "/api/v1/platform/companies", True,
            dict(slug="qa-" + nonce, name="Synthetisches Demo-Unternehmen", accent="#1064e8"), 201, 0)
        assert api(company, TENANT + "/team", True)["value"]["members"]
        worker = next(x for x in api(company, TENANT + "/technicians", True)["value"] if x["name"] == "Demo-Technik")
        api(other, ticket, True, expected=404)
        api(other, photo_url, True, expected=404)
        api(technician, ticket, True, expected=404)
        print("All four portals, platform TOTP, tenant creation and denied cross-tenant access passed.")

        def action(page, name, is_staff=True, **fields):
            current = api(page, ticket, is_staff)
            return api(page, ticket + "/commands", is_staff, dict(action=name, **fields), revision=revision(current))

        action(company, "triage")
        action(company, "assign", technicianId=worker["id"])
        # Separate smoke-test bookings so repeated runs do not conflict.
        start = dt.datetime.now(dt.timezone.utc).replace(minute=0, second=0, microsecond=0) + dt.timedelta(
            hours=48 + int(nonce, 16) % (24 * 90))
        action(company, "schedule", startsAt=start.isoformat(), endsAt=(start + dt.timedelta(hours=1)).isoformat())
        action(customer, "confirm_appointment", is_staff=False)
        action(technician, "arrival", arrivalStatus="arrived")
        action(technician, "start")
        room = TENANT + "/rooms/customer?requestId=" + ticket.rsplit("/", 1)[-1]
        api(company, room, True, {"body":"Synthetische Nachricht zur Demo-Anfrage."}, 201)
        assert api(customer, room)["value"]["items"]
        api(other, room, True, expected=404)
        print("Dispatch, technician workflow, appointment confirmation and chat passed.")

        report = ticket + "/report"
        api(technician, report + "/commands", True,
            dict(action="submit", activities=[dict(description="Demo-Funktion geprüft", minutes=15)], result="Test abgeschlossen."),
            revision=0)
        api(reviewer, report + "/commands", True, dict(action="approve", version=1), revision=1)
        await_state(lambda: api(reviewer, report, True)["value"]["pdfReady"])
        api(reviewer, report + "/commands", True, dict(action="publish", version=1), revision=2)
        assert api(customer, report + "/versions/1/pdf")["value"].startswith("%PDF-")

        invoice = '<Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"><ID>DEMO</ID></Invoice>'
        added = api(company, ticket + "/invoices/upload", True, expected=201, revision=0, upload=invoice)
        invoice_path = ticket + "/invoices/" + added["value"]["id"]
        await_state(lambda: api(reviewer, invoice_path, True)["value"]["items"][0]["scanStatus"] == "clean")
        api(reviewer, invoice_path + "/commands", True, dict(action="approve", version=1), revision=1)
        api(reviewer, invoice_path + "/commands", True, dict(action="publish", version=1), revision=2)
        assert api(customer, invoice_path + "/versions/1/download")["value"] == invoice
        api(other, invoice_path + "/versions/1/download", True, expected=404)
        action(company, "complete")
        api(customer, "/api/v1/logout", body={}, expected=204)
        api(customer, ticket, expected=404)
        print("Report PDF, approved invoice download, cross-tenant file denial and logout passed.")
        browser.close()


if __name__ == "__main__":
    main()
