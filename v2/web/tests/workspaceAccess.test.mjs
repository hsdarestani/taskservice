import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveWorkspaceAccess, navigationForRole } from '../src/workspaceAccess.ts';

const hasPlatform = { ready: true };
const noPlatform = { ready: false, error: 'Zugriff verweigert.', status: 403 };
const loadingPlatform = { ready: false };

test('all unauthenticated staff routes are gated without privileged navigation', () => {
  for (const portal of ['platform', 'company', 'technician']) {
    assert.equal(resolveWorkspaceAccess(portal, false, undefined, noPlatform), 'sign-in');
  }
  assert.equal(resolveWorkspaceAccess('customer', false, undefined, noPlatform), 'allowed');
});

test('platform requires API-confirmed authorization, not just a signed-in cookie', () => {
  assert.equal(resolveWorkspaceAccess('platform', true, 'admin', noPlatform), 'denied');
  assert.equal(resolveWorkspaceAccess('platform', true, undefined, loadingPlatform), 'checking');
  assert.equal(resolveWorkspaceAccess('platform', true, undefined, { ready: false, error: 'Datenbank nicht erreichbar', status: 503 }), 'unavailable');
  assert.equal(resolveWorkspaceAccess('platform', true, undefined, { ready: false, error: 'Netzwerkfehler' }), 'unavailable');
  assert.equal(resolveWorkspaceAccess('platform', true, undefined, { ready: false, error: 'Bitte anmelden', status: 401 }), 'denied');
  assert.equal(resolveWorkspaceAccess('platform', true, undefined, hasPlatform), 'allowed');
});

test('staff role cannot access another role’s workspace', () => {
  assert.equal(resolveWorkspaceAccess('technician', true, 'admin', hasPlatform), 'denied');
  assert.equal(resolveWorkspaceAccess('company', true, 'technician', hasPlatform), 'denied');
  assert.equal(resolveWorkspaceAccess('company', true, undefined, hasPlatform), 'denied');
  assert.equal(resolveWorkspaceAccess('technician', true, 'technician', loadingPlatform), 'allowed');
  assert.equal(resolveWorkspaceAccess('company', true, 'dispatcher', loadingPlatform), 'allowed');
  assert.equal(resolveWorkspaceAccess('company', true, 'admin', loadingPlatform), 'allowed');
});

test('menus contain only capabilities authorized for that role', () => {
  const reviewer = navigationForRole('company', 'dispatcher');
  assert.deepEqual(reviewer, ['overview', 'requests', 'technicians', 'conversations']);
  assert.ok(!reviewer.includes('team'));
  assert.ok(!reviewer.includes('integrations'));
  const admin = navigationForRole('company', 'admin');
  assert.ok(admin.includes('team') && admin.includes('integrations'));
  assert.deepEqual(navigationForRole('technician'), ['requests', 'schedule', 'conversations']);
  assert.deepEqual(navigationForRole('platform'), ['overview', 'companies', 'features', 'audit']);
});
