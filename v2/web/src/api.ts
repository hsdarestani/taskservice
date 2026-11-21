const route = location.pathname.split('/').filter(Boolean);
const view = new URLSearchParams(location.search).get('ansicht');
const workforce = route[0] === 'plattform' || ['betrieb', 'technik', 'einladungen'].includes(route[2]) ||
  route[2] === 'anfragen' && (view === 'betrieb' || view === 'technik');
const sessionHeaders: Record<string, string> = workforce ? { 'X-TaskService-Session': 'workforce' } : {};

export function sessionUrl(url: string): string {
  return workforce ? url + (url.includes('?') ? '&' : '?') + 'session=workforce' : url;
}

export type Session = { signedIn: boolean; csrfToken: string };
export type Company = { slug: string; name: string; accent: string; role?: string; privacyNoticeUrl?: string };
export type AppointmentView = {
  startsAt: string; endsAt: string; arrivalStatus: string; timeZone: string;
  updatedAt?: string; customerConfirmedAt?: string; customerNote?: string;
};
export type RequestRow = {
  id: string; title: string; state: string; revision: number; createdAt: string;
  serviceCategory?: string; appointment?: AppointmentView;
};
export type Detail = RequestRow & {
  description: string;
  canReadInternal: boolean;
  contactName?: string;
  contactPhone?: string;
  photos: { id: string; ordinal: number; scanStatus: 'pending' | 'clean' | 'quarantined' | 'failed' }[];
  actions: string[];
  history: { state: string; at: string; privateNote?: string }[];
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

export type InvoiceVersion = {
  version: number;
  originalName: string;
  uploadedAt: string;
  publishedAt?: string;
  size: number;
  status: string;
  scanStatus: string;
  reviewReason?: string;
};
export type InvoiceItem = InvoiceVersion & {
  id: string;
  revision: number | null;
  publishedVersion?: number;
  canDownload: boolean;
  actions: string[];
};

export async function uploadInvoice(url: string, file: File, csrfToken: string, invoice?: InvoiceItem) {
  const target = url + '/upload' + (invoice ? '?invoiceId=' + encodeURIComponent(invoice.id) : '');
  return parse(await fetch(target, {
    method: 'POST',
    headers: {
      ...sessionHeaders,
      'Content-Type': file.name.toLowerCase().endsWith('.xml') ? 'application/xml' : 'application/pdf',
      'X-Invoice-Name': encodeURIComponent(file.name),
      'X-CSRF-Token': csrfToken,
      'Idempotency-Key': crypto.randomUUID(),
      'If-Match': `"r${invoice?.revision ?? 0}"`,
    },
    body: file,
  }));
}

export async function get<T>(url: string): Promise<T> {
  return parse<T>(await fetch(url, { headers: { ...sessionHeaders, Accept: 'application/json' } }));
}

export async function send<T>(url: string, body: unknown, csrfToken: string, key = crypto.randomUUID(), revision?: number): Promise<T> {
  const headers: Record<string, string> = {
    ...sessionHeaders,
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
