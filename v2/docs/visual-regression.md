# TaskService V2 visual and workflow checks

The live Worker response at `taskservice.smarbiz.sbs/` was saved without mutation. Its HTML matched `frontend.html` on main byte for byte (SHA-256 `946ca96e86b21fd622f02c6fb0ed6f78dae42092c59698e182ca8df312baa8a4`). Chromium rendered that response with the corresponding service photographs. V2 screenshots were taken against a separate loopback HTTPS process with migrations and synthetic PostgreSQL data.

| Check | 390 px | 768 px | 1366 px | Outcome |
| --- | --- | --- | --- | --- |
| Consumer home and category images | Compared | Compared | Compared | Restored navy wordmark and blue promotional panel, category-first hierarchy and three image-led service cards. |
| Service search and empty state | Exercised | Exercised | Exercised | Search matches service names and practical search terms. Results and reset follow customer input. |
| Three-step intake wizard | Compared | Compared | Compared | Category preselection, progress, validation, back navigation and retained attachments checked. |
| Photo submission | Exercised | Exercised | Exercised | API creates the request with photos. A separate authorization test exercises scanning, quarantine and download isolation. |
| Company operations workspace | Exercised | Exercised | Exercised | Data-backed request table, search, sorting, tenant team invitations and dispatch use real API endpoints. |
| Technician workspace | Exercised | Exercised | Exercised | Assigned work, daily-planning section, mobile navigation and report workflow. |
| Platform administration | Exercised | Exercised | Exercised | Synthetic operator creates a company, queues its first admin invitation and sees the audit entry. |
| Customer account | Exercised | Exercised | Exercised | Claim and account isolation are API-tested. OIDC code exchange and PKCE were exercised with a signed synthetic provider. Production identity and mail delivery remain separate gates. |
| Chat, scheduling, reports and invoices | Mixed viewport coverage | Pending additional combinations | Mixed viewport coverage | Existing SignalR, appointment, review, publication and private invoice test coverage remains in place. |

Manual review follows the viewport screenshots for spacing, focus, service imagery, visible headings, contrast and scrolling. Physical-device keyboard and screen-reader review, legal content, design acceptance and production rollout remain open.
