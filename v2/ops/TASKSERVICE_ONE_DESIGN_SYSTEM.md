# TaskService One — unified product design system

**Status:** Implemented in `v2/web/src/taskservice-one.css` and `workspaceAccess.ts` on the UX overhaul branch. This is a code-backed UI architecture, not a redesign mockup. No backend schema or permission model changes.

## Design principles

1. **Permission first.** A signed-in session is not authorization. Platform navigation mounts only after `GET /api/v1/platform/overview` succeeds; company/technician workspaces mount only with the corresponding tenant membership `role` returned by `GET /api/v1/tenants/{slug}`. The API independently enforces the same access. An unprivileged visitor sees a **dedicated standalone access screen**, not a disabled admin menu or preview of private controls.
2. **One shell, differentiated workflows.** Platform and company use a desktop side rail and compact horizontal mobile nav. Technician uses the same typography and UI tokens but a task-centric three-action bottom nav on mobile. Customer intake preserves its separate, conversion-oriented shopping/service layout.
3. **Intentional density.** Information hierarchy: page title → concise description → numerical context → active list → secondary workflows. Company administration uses disclosure patterns to keep rarely used forms collapsed until requested.
4. **Responsive at 360, 390, 440, 768, 1024 and 1440px.** No horizontal overflow of the document (table wrappers may scroll); 42–48px controls; visible focus states; reduced motion support.

## Design tokens

| Element | Token | Value |
|---|---|---|
| Text | `--ts-ink` | `#17243c` |
| Canvas | `--ts-canvas` | `#f5f7fb` |
| Surface | `--ts-surface` | `#fff` |
| Sidebar | `--ts-sidebar` | `#111e35` |
| Action | `--blue` | `#2563eb` |
| Border | `--ts-border` | `#e1e7ef` |
| Muted | `--ts-muted` | `#65748c` |
| Radius | `--ts-radius` | `14px` |
| Spacing | source of truth | 4px multiples; primary vertical rhythm 8/16/24/32 |
| Font stack | shared | Inter → Segoe UI → Arial → system sans |

Colors for action status are semantic, not decorative. Tenant brand accents remain reserved for their existing customer experience.

## Permissions and visibility

| Viewer | Platform menu | Company menu | Technician menu | Customer portal |
|---|---|---|---|---|
| Guest | No | No | No | Public service discovery |
| Customer | No | No | No | Own account |
| Company admin | No | Yes: overview, requests, techs, team, integrations, chat | No | Public only |
| Dispatcher/reviewer | No | Yes: overview, requests, techs, chat | No | Public only |
| Technician | No | No | Yes: assignments, day plan, chat | Public only |
| Platform admin | Yes, after API proof | No without tenant member | No | Public only |

**Invariant:** the demo banner contains **no cross-role deep links**. The frontend `resolveWorkspaceAccess` is only a presentation gate; the API stays the ultimate authority. No privilege can be gained by editing frontend role names, hash fragments or CSS.

## QA expectations

- `npm run --prefix v2/web test:access`: access state and role navigation contract.
- `npm run --prefix v2/web build`: TS and bundle integrity.
- `dotnet test ...`: backend permission isolation and browser acceptance.
- Browser acceptance visits all three restricted workspace routes as guest on 390/768/1366 widths and confirms **zero sidebar, zero privileged nav and no horizontal document overflow**; screenshots are generated as job artifacts.
- Before any production merge/deploy: review real generated screenshots, check customer flow regression, platform admin MFA flag behavior, backend auth status, and cohost memory limits. Release only after explicit approval.

## Future phases

Extend the same tokens to invoice/report timelines and build an interaction-level command palette; do not add fake actions, placeholder buttons or client-side-only security. Prefer further small, QA-verified improvements rather than moving APIs or databases while the existing Publisher VPS is co-hosted.
