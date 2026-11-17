# TaskService MVP

Mobile-first German service request web app (inspired by Urban Company, Justlife, Thumbtack and Airtasker patterns; no copied branding or assets).

## Stack
Cloudflare Worker + D1 EU jurisdiction; the Worker serves the customer UI and the company desk at /admin. Repository intentionally has no build dependencies.

## Security and production blockers
- Cloudflare Access protects /admin and /api/admin/* with a single-account allowlist; Worker independently verifies RS256 JWT using the Cloudflare Access JWKS and audience. Extend admin identity policies when company staff are approved.
- R2 is disabled in this Cloudflare account. Images are temporarily stored privately as base64 in D1, capped at 3 x 350 KB. Move to private R2 before scale.
- Add real Impressum and Datenschutzerklärung and an approved retention/deletion policy before collecting actual customer data.
- Add Turnstile (verified server-side), rate limiting, email confirmation, notifications, customer authentication/tracking, and a robust anti-abuse strategy before launch.
- Service categories are temporary placeholders pending company service catalog.
- Company desk notes are internal only, not emailed to customers.

## Deployment
Cloudflare Worker: taskservice; Database: taskservice-prod (EU D1). Custom domain: taskservice.smarbiz.sbs.
To deploy from CLI, upload worker.js as an ES module with binding DB of type d1 to the database UUID in Cloudflare. Use GitHub deployment automation after initial live verification.

## Design references
Urban Company and Justlife: navigation, approachable category cards. Airtasker: lightweight request journey. Thumbtack: clean professional typography and content hierarchy.

## Design-rebuild verification (2026-11-17)

- Replaced the custom green/cream landing UI with a screenshot-guided Urban Company three-column category layout and a split-blue CTA banner.
- Rebuilt the Justlife-style horizontal image-service row, and an Airtasker-style three-step guest ticket submission flow.
- Source-to-section screenshot mapping in [DESIGN_REFERENCES.md](DESIGN_REFERENCES.md).
- Cloudflare Browser Rendering: validated the new mobile category layout and wizard step 1 at 390×844.
- End-to-end test: submitted a fictional test ticket through the live wizard; verified new D1 row with status `new`, then deleted the QA ticket and confirmed zero rows remaining (ticket `TS-MWKQB87K-BBD46`). No real customer contact data used.
- Cloudflare Access still protects the company dashboard; untrusted unauthenticated browser navigation to /admin returned login_required.
- **Not yet production-ready:** add GDPR docs, confirm official company identity/services and permissioned staff access, implement verified Turnstile and rate limits, R2 file storage, notifications, and customer sign-in/tracking if needed.

## 2026-11-17 customer portal and imagery follow-up
- Implemented public guest request to private email-OTP customer portal journey with a post-submit login CTA.
- Replaced line category icons with service-photo thumbnails matching the reference's image-first categories, and replaced bottom-nav symbols with filled two-tone SVG icons. All decorative arrow glyphs removed.
- Fixed the broken garden image URL; six category photos, three service cards and hero are now served from same-origin `/media/*` routes with caching and an SVG fallback for upstream image failures.
- Verified all ten visible image elements loaded in the live mobile browser screenshot; verified guest form could create a real D1 ticket, then deleted the synthetic QA ticket and verified the record was gone.
- Unauthenticated requests to `/konto` and `/api/customer/tickets` were blocked by Cloudflare Access. **Email OTP end-to-end sign-in still needs a test with a real consenting test mailbox.**
- R2 uploads and real GDPR legal pages, Turnstile, submission rate limiting, organization identity customization, and a final security review are required before taking real customer traffic.
