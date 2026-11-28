// TaskService One — presentation guard only. The API remains the authority for permissions.
// A denied workspace must never render privileged navigation, controls or data.
export type WorkspaceKind = 'customer' | 'company' | 'technician' | 'platform';
export type WorkspaceAccess = 'allowed' | 'sign-in' | 'checking' | 'denied' | 'unavailable';

export function resolveWorkspaceAccess(
  kind: WorkspaceKind,
  signedIn: boolean,
  tenantRole: string | undefined,
  platform: { ready: boolean; error?: string; status?: number }
): WorkspaceAccess {
  if (kind === 'customer') return 'allowed';
  if (!signedIn) return 'sign-in';
  if (kind === 'platform') {
    if (platform.ready) return 'allowed';
    if (platform.error) return platform.status === 401 || platform.status === 403 ? 'denied' : 'unavailable';
    return 'checking';
  }
  if (kind === 'company') return tenantRole === 'admin' || tenantRole === 'dispatcher' ? 'allowed' : 'denied';
  return tenantRole === 'technician' ? 'allowed' : 'denied';
}

export function navigationForRole(kind: Exclude<WorkspaceKind, 'customer'>, tenantRole?: string) {
  if (kind === 'platform') return ['overview', 'companies', 'features', 'audit'];
  if (kind === 'technician') return ['requests', 'schedule', 'conversations'];
  const common = ['overview', 'dispatch', 'requests', 'technicians'];
  return tenantRole === 'admin'
    ? [...common, 'team', 'integrations', 'conversations']
    : [...common, 'conversations'];
}
