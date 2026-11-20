export type Session = { signedIn: boolean; csrfToken: string };
export type Company = { slug: string; name: string; accent: string; role?: string };
export type RequestRow = { id: string; title: string; state: string; revision: number; createdAt: string };
export type Detail = RequestRow & {
  description: string;
  appointment?: { startsAt: string; endsAt: string; arrivalStatus: string };
  actions: string[];
  history: { state: string; at: string }[];
};
export type Message = { sequence: number; author: string; body: string; sentAt: string };
export type ReportView = {
  revision?: number;
  published?: { version: number; publishedAt: string };
  publishedVersion?: number;
  pdfReady?: boolean;
  actions?: string[];
  versions?: {
    version: number;
    status: string;
    submittedAt: string;
    reviewReason?: string;
    activities: { description: string; minutes: number }[];
    result: string;
    materials?: string;
  }[];
};

export async function get<T>(url: string): Promise<T> {
  return parse<T>(await fetch(url, { headers: { Accept: 'application/json' } }));
}

export async function send<T>(url: string, body: unknown, csrfToken: string, key = crypto.randomUUID(), revision?: number): Promise<T> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'X-CSRF-Token': csrfToken,
    'Idempotency-Key': key,
  };
  if (revision !== undefined) headers['If-Match'] = `"r${revision}"`;
  return parse<T>(await fetch(url, { method: 'POST', headers, body: JSON.stringify(body) }));
}

async function parse<T>(response: Response): Promise<T> {
  if (response.status === 204) return undefined as T;
  const json = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(json.detail || 'Die Anfrage konnte nicht verarbeitet werden.');
  return json as T;
}
