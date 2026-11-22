# TaskService One — product-wide visual audit

## Baseline problem
A consistent dark sidebar and rounded cards are not a full design system. In the November 22 review the desktop request detail was a long column of exposed forms, reports, invoices and chat; user journeys competed for attention, raw file controls leaked into the UI, and the customer landing and staff authentication used unrelated layouts.

## Implemented design architecture
All changes are frontend-only; request API, database, tenant data, authorizations and background workers are unchanged.

| Surface | Architecture | Tested behavior |
|---|---|---|
| Public service landing `/unternehmen/{slug}` | One cohesive hero, service picker, featured categories, trust/benefit section | Category search; six service buttons; search zero-state; guest 3-step wizard |
| Request wizard | Three-stage intake with visible progress, consistent fields and deliberate photo selector | Keyboard opening, Escape, validation, image MIME/size checks, upload, back/forward state |
| Login `/anmelden` | Two-column native-account experience (independent staff / customer copy) | Existing password, registration and optional TOTP security unchanged |
| Customer account `/unternehmen/{slug}/konto` | Unified headers, cards, states, actions | Own requests, private permissions |
| Company `/betrieb` | One role-filtered responsive workspace | Overview, requests, team, technicians, integration, chat |
| Technician `/technik` | Task-first mobile navigation | Assigned work, day plan, chat |
| Platform `/plattform` | Overview, company directory, audit are separate actual views | Role guard and tenant isolation |
| Request detail | Independent tabs: summary, available actions, report, invoices (when relevant), messages, authorized internal notes, timeline | Role-scoped content, scheduled work, confirmation, report, approval, invoice upload, publication, download |
| Upload file controls | Shared custom document/photo surface with actual native file input retained underneath | Real file selection and upload; not a fake visual button |
| Empty, loading, error states | Shared tokens, visible feedback, status distinction | Login failures, no-requests, no-documents, no-activity, permission denial |

## Rules of TaskService One
- No privileged nav to unauthorized or unauthenticated users; server permissions are authoritative.
- Every page has one heading, one primary action (when relevant), clear secondary actions, and explicit empty states.
- Risky operations such as cancellation, rejection and revocation are visually distinguished from ordinary saves.
- No raw file-input text, body-width forms with two fields, duplicate page headings, or endless mixed-purpose content sections.
- Control minimum height ~44–48px, visible focus, and typography >=12px for meaningful interactive labels.
- Same tokens apply to *all* personas and public journeys, while workspace composition adapts to the task.
- Mobile request details do not show two different section selectors; request tabs use an accessible single selector.
- No misleading placeholder metrics or fabricated data. No promises of service times or delivery outcomes.

## Automated visual acceptance
`v2/tests/guest_browser.py`: responsive customer service discovery, category search, wizard, uploads, validation at 390/768/1366.
`v2/tests/report_browser.py`: cross-role platform/company/technician/customer workflows, permission denial and responsive screenshots for each available request tab at 390/768/1366.
`v2/tests/invoice_browser.py`: actual invoice upload, review, publish and customer original download.
`npm run --prefix v2/web test:access`: deterministic role navigation tests.
`npm run --prefix v2/web build`: TypeScript and asset build.
`dotnet test v2/tests/TaskService.Tests.csproj --configuration Release`: API and end-to-end regression tests.
Browser screenshots are saved in GitHub Actions artifact `taskservice-one-browser-qa` for design review.

## Safe release rules
Deploy via a sha-pinned Publisher Action using the pre-existing hardened SSH key, never editing production accounts or migrations, and only recreating the TaskService API container. The workflow must:
1. Verify required CI checks green for the exact SHA.
2. Check currently deployed image, demo MFA option and normalized Caddy alias.
3. Build off-server on GitHub Actions; upload Docker image without stressing the cohost VPS.
4. Back up current `.env` and retain previous image.
5. Recreate API only; ensure session and readiness endpoints return HTTP 200.
6. Check the cohost Publisher health and automatically restore the previous TaskService image on failure.

## Acceptance caveat
Browser pass proves flows render and work in the fixtures, not that every real-world workflow has been manually visually approved. Review image artifacts before claiming full production design sign-off.
