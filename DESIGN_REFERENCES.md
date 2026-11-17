# Visual design implementation — reference map
This is a **section-by-section reconstruction** of specific app screens, not a bespoke design system. No external trademarks or logos were copied.

## 1. Category navigation + search — Urban Company
- Visual reference: https://miro.medium.com/v2/resize%3Afit%3A1400/1%2A_HGGQkbEQuluxLpa-fMB8Q.gif
- Alternative reference: https://mir-s3-cdn-cf.behance.net/project_modules/1400/137d58200772341.66684bb7e3a37.png (explicitly a third-party *redesign*, not official UI).
- Rebuilt: slim offer strip, location header + bordered search, three-column service icons with muted grey image squares, concise captions, and a category-first landing layout.
- Removed: green/cream custom styling, oversized custom illustrated house, oversized marketing hero and rounded marketing tiles.

## 2. Image-led secondary service section — Justlife
- Visual references: https://cdn.dribbble.com/userupload/10211888/file/0ae6f844cd746a9c583aaea793329fb7.png (designer work, not official shipping UI), https://a.storyblok.com/f/285972427025846/407x463/7718844817/mobile-apps-v2.png/m/680x774/filters%3Aformat%28webp%29
- Rebuilt: a swipeable horizontal image-card row on mobile with title and short category caption. This is a product design pattern, not a claim of actual popularity statistics. Before launch rename "Häufig angefragte" if unsupported by real ticket volumes.

## 3. Multi-step request form — Airtasker
- Visual reference: https://images.airtasker.com/v7/www.airtasker.com/static/assets/910387fc6b5b793c.png?func=bound&org_if_sml=1&w=760
- Rebuilt: full-screen-on-mobile form, white background, navy editorial headings, thin green progress bar, top back control, and single-topic fields per step.
- 3 steps adapted for the simpler direct-to-company ticket use case: title + category → description + photos → guest contact details.
- Not copied: Airtasker bidding, budgets, tasker selection, payment, profiles.

## QA / outstanding
- The backend, D1 EU data and Cloudflare Access company desk remain intact.
- External stock imagery loaded from Pexels; replace with vetted company-owned imagery before client launch, or self-host licensed assets to avoid third-party requests.
- Automated form E2E test and admin login test need to be completed with an authenticated browser; do not claim these passed.
- Add genuine legal docs, Turnstile, per-IP rate limiting, notification delivery, R2 before production use.
- Frontend source is in frontend.html, and the same HTML is embedded as PAGE in worker.js for a dependency-free Worker deploy.

## Iteration 2026-11-17: user screenshot defects
- Removed all arrow-symbol icons in the consumer and company interfaces.
- Category tiles now use actual service images rather than generic monochrome line glyphs. This preserves the Urban Company category-first reference layout but uses Pexels-licensed photographic substitutes instead of copying Urban Company's original illustrations.
- Image sources are proxied through `/media/*`; gallery photos now use `8055202` (cleaning), `32588548` (repairs), `5027602` (garden), `20500461` (electrical), `31755424` (furniture), `27176670` (other), `27176671` (hero) from Pexels. Verify Pexels license and actual client branding before production.
- Customer sign-in (`/konto`) uses Cloudflare Access email OTP and only exposes tickets belonging to the verified email.
