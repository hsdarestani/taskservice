#!/usr/bin/env python3
"""Import a trusted TaskService D1 export into one explicitly approved, empty tenant."""
import argparse
import base64
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import sys
from uuid import UUID, uuid5

import psycopg

CATEGORIES = {
    "Reparaturen": "repair", "Reinigung": "cleaning", "Elektro & Sanitär": "electric",
    "Haus & Garten": "garden", "Möbelmontage": "furniture", "Weitere Services": "other",
    "Sonstige Arbeiten": "other",
}
STATES = {"new": "received", "progress": "triage", "closed": "completed"}
CHANNELS = {"public": "customer", "internal": "internal"}
SOURCE_TABLES = {"tickets", "attachments", "ticket_messages"}


def utc(value):
    date = datetime.fromisoformat(value)
    return date.replace(tzinfo=timezone.utc) if date.tzinfo is None else date.astimezone(timezone.utc)


def limited(value, limit):
    if not isinstance(value, str) or not value.strip() or len(value) > limit or "\x00" in value:
        raise ValueError("A source text field is invalid or exceeds the destination limit.")
    return value


def read_snapshot(path):
    data = path.read_bytes()
    with sqlite3.connect(":memory:") as source:
        source.set_authorizer(
            lambda action, *_: sqlite3.SQLITE_DENY
            if action in (sqlite3.SQLITE_ATTACH, sqlite3.SQLITE_DETACH) else sqlite3.SQLITE_OK
        )
        source.row_factory = sqlite3.Row
        source.executescript(data.decode("utf-8"))
        if source.execute("PRAGMA integrity_check").fetchone()[0] != "ok" or source.execute(
            "PRAGMA foreign_key_check"
        ).fetchone():
            raise ValueError("The SQLite export did not pass integrity checks.")
        tables = {
            row[0] for row in source.execute("SELECT name FROM sqlite_schema WHERE type='table'")
        }
        if not SOURCE_TABLES <= tables or tables - SOURCE_TABLES - {"_cf_KV", "sqlite_sequence"}:
            raise ValueError("The D1 table inventory needs review before import.")
        tickets = [dict(row) for row in source.execute("SELECT * FROM tickets ORDER BY id")]
        photos = [dict(row) for row in source.execute("SELECT * FROM attachments ORDER BY ticket_id,id")]
        messages = [dict(row) for row in source.execute(
            "SELECT * FROM ticket_messages ORDER BY ticket_id,created_at,id"
        )]
    return hashlib.sha256(data).digest(), tickets, photos, messages


def import_snapshot(cursor, tenant, slug, snapshot):
    digest, tickets, photos, messages = snapshot
    cursor.execute("SELECT set_config('taskservice.tenant', %s, true)", (str(tenant),))
    cursor.execute("SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))", ("taskservice:d1:" + str(tenant),))
    cursor.execute("SELECT 1 FROM ts.tenants WHERE id=%s AND slug=%s AND active", (tenant, slug))
    if cursor.fetchone() is None:
        raise ValueError("The approved destination tenant ID and slug did not match an active tenant.")
    cursor.execute("CREATE SCHEMA IF NOT EXISTS ts_import")
    cursor.execute("REVOKE ALL ON SCHEMA ts_import FROM PUBLIC")
    cursor.execute("""CREATE TABLE IF NOT EXISTS ts_import.d1_batches (
        tenant_id uuid PRIMARY KEY REFERENCES ts.tenants(id), source_digest bytea NOT NULL,
        tickets integer NOT NULL, photos integer NOT NULL, messages integer NOT NULL)""")
    cursor.execute("""CREATE TABLE IF NOT EXISTS ts_import.d1_request_ids (
        tenant_id uuid NOT NULL, legacy_id varchar(128) NOT NULL, request_id uuid NOT NULL,
        PRIMARY KEY (tenant_id,legacy_id), UNIQUE (tenant_id,request_id),
        FOREIGN KEY (tenant_id,request_id) REFERENCES ts.requests(tenant_id,id))""")
    cursor.execute("""CREATE TABLE IF NOT EXISTS ts_import.d1_message_ids (
        tenant_id uuid NOT NULL, legacy_id varchar(128) NOT NULL, message_sequence bigint NOT NULL,
        PRIMARY KEY (tenant_id,legacy_id), UNIQUE (tenant_id,message_sequence),
        FOREIGN KEY (tenant_id,message_sequence) REFERENCES ts.messages(tenant_id,sequence))""")
    cursor.execute("SELECT source_digest FROM ts_import.d1_batches WHERE tenant_id=%s", (tenant,))
    recorded = cursor.fetchone()
    if recorded:
        if recorded[0] != digest:
            raise ValueError("A different snapshot was already imported. Reconcile changes before continuing.")
        return "already_imported"
    cursor.execute("SELECT EXISTS (SELECT 1 FROM ts.requests WHERE tenant_id=%s)", (tenant,))
    if cursor.fetchone()[0]:
        raise ValueError("Destination has requests. An explicit reconciliation is required.")

    requests = {}
    for row in tickets:
        legacy_id = limited(row["id"], 128)
        request = uuid5(tenant, "d1-request:" + legacy_id)
        created, updated = utc(row["created_at"]), utc(row["updated_at"])
        state = STATES[row["status"]]
        if updated < created:
            raise ValueError("A source request was updated before its creation time.")
        later = updated != created or state != "received"
        cursor.execute("""INSERT INTO ts.requests
            (tenant_id,id,title,description,contact_email,contact_name,contact_phone,service_category,state,revision,created_at)
            VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)""", (
            tenant, request, limited(row["category"], 160), limited(row["message"], 4000),
            limited(row["email"], 254), limited(row["name"], 120), limited(row["phone"], 50),
            CATEGORIES[row["category"]], state, 2 if later else 1, created
        ))
        cursor.execute("INSERT INTO ts.history (tenant_id,request_id,revision,state,at) VALUES (%s,%s,1,'received',%s)",
                       (tenant, request, created))
        if later:
            cursor.execute("INSERT INTO ts.history (tenant_id,request_id,revision,state,at) VALUES (%s,%s,2,%s,%s)",
                           (tenant, request, state, updated))
        cursor.execute("INSERT INTO ts.audit VALUES (%s,%s,'legacy-import','request.d1.imported',%s,now())",
                       (tenant, uuid5(tenant, "d1-request:" + str(request)), request))
        cursor.execute("INSERT INTO ts_import.d1_request_ids VALUES (%s,%s,%s)", (tenant, legacy_id, request))
        requests[legacy_id] = request

    ordinal = defaultdict(int)
    for row in photos:
        if row["ticket_id"] not in requests:
            raise ValueError("A photo references a missing legacy request.")
        request, photo = requests[row["ticket_id"]], UUID(row["id"])
        content = base64.b64decode(row["body"], validate=True)
        mime = row["mime"]
        image_matches = {
            "image/png": content.startswith(b"\x89PNG\r\n\x1a\n") and content[12:16] == b"IHDR",
            "image/jpeg": content.startswith(b"\xff\xd8\xff") and content.endswith(b"\xff\xd9"),
            "image/webp": content.startswith(b"RIFF") and content[8:12] == b"WEBP",
        }
        ordinal[request] += 1
        if not image_matches.get(mime) or not 1 <= len(content) <= 350000 or ordinal[request] > 3:
            raise ValueError("A legacy photo needs explicit review before import.")
        cursor.execute("""INSERT INTO ts.request_photos
            (tenant_id,request_id,id,ordinal,mime,size,checksum,scan_status)
            VALUES (%s,%s,%s,%s,%s,%s,%s,'pending')""",
            (tenant, request, photo, ordinal[request], mime, len(content), hashlib.sha256(content).digest()))
        cursor.execute("INSERT INTO ts.request_photo_files VALUES (%s,%s,%s,%s)", (tenant, request, photo, content))
        cursor.execute("""INSERT INTO ts.outbox (tenant_id,id,kind,payload,available_at,attempts)
            VALUES (%s,%s,'request_photo_scan',%s,now(),0)""", (
            tenant, uuid5(tenant, "d1-photo:" + str(photo)),
            json.dumps({"requestId": str(request), "photoId": str(photo)}),
        ))

    for row in messages:
        if row["ticket_id"] not in requests:
            raise ValueError("A conversation message references a missing legacy request.")
        request = requests[row["ticket_id"]]
        cursor.execute("""INSERT INTO ts.messages (tenant_id,request_id,channel,author,body,sent_at)
            VALUES (%s,%s,%s,%s,%s,%s) RETURNING sequence""", (tenant, request, CHANNELS[row["visibility"]],
            limited(row["author"], 120), limited(row["body"], 2000), utc(row["created_at"])))
        cursor.execute("INSERT INTO ts_import.d1_message_ids VALUES (%s,%s,%s)",
                       (tenant, limited(row["id"], 128), cursor.fetchone()[0]))

    cursor.execute("INSERT INTO ts_import.d1_batches VALUES (%s,%s,%s,%s,%s)",
                   (tenant, digest, len(tickets), len(photos), len(messages)))
    return "imported"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--tenant", type=UUID, required=True)
    parser.add_argument("--slug", required=True)
    parser.add_argument("--apply", action="store_true", help="Commit the import after a successful rehearsal.")
    args = parser.parse_args()
    try:
        snapshot = read_snapshot(args.snapshot)
        with psycopg.connect(os.environ["TASKSERVICE_IMPORT_DSN"], connect_timeout=5) as db:
            with db.transaction(force_rollback=not args.apply):
                with db.cursor() as cursor:
                    result = import_snapshot(cursor, args.tenant, args.slug, snapshot)
        digest, tickets, photos, messages = snapshot
        print(f"{'apply' if args.apply else 'rehearsal'} {result}: "
              f"tickets={len(tickets)} photos={len(photos)} messages={len(messages)} "
              f"sha256={digest.hex()}")
        return 0
    except (KeyError, OSError, ValueError, sqlite3.Error, psycopg.Error) as error:
        # Database and parser exceptions may include email addresses or other source values.
        print(f"Import stopped and rolled back ({type(error).__name__}).", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
