# TaskService MVP

Mobile-first German service request web app (inspired by Urban Company, Justlife, Thumbtack and Airtasker patterns; no copied branding or assets).

## Stack
Cloudflare Worker + D1 EU jurisdiction; the Worker serves the customer UI and the company desk at /admin. Repository intentionally has no build dependencies.

## Security and production blockers
- Cloudflare Access must protect /admin and /api/admin/*; Worker also expects a verified access identity, not a public endpoint. **Do not rely on presence of an unverified JWT**. Before production, replace the current header-presence check with Cloudflare Access JWT verification against JWKS/aud or configure an internal service-only auth boundary.
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
