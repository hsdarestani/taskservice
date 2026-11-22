import { useEffect, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { send, sessionUrl, uploadInvoice } from './api';
import type { Company, Detail, InvoiceItem, InvoiceVersion, Message, ReportView, RequestRow, Session } from './api';
import { Activity, Building2, CalendarDays, ChevronRight, ClipboardList, LayoutDashboard, LockKeyhole, LogOut, MessagesSquare, Plug, Plus, ShieldCheck, UserRound, UsersRound } from 'lucide-react';
import { CustomerHeader, CustomerHome, CustomerNav, Wordmark } from './CustomerPortal';
import type { ServiceCategory } from './CustomerPortal';
import { useLoad } from './useLoad';
import { navigationForRole, resolveWorkspaceAccess } from './workspaceAccess';
import { NativeLogin } from './NativeLogin';
import { AcceptInvitation, InitialAdminSetup, Team } from './Team';
import './styles.css';
import './portal.css';
import './taskservice-one.css';

const states: Record<string, string> = {
  received: 'Eingegangen', verified: 'Bestätigt', under_review: 'In Prüfung',
  triage: 'In Prüfung', assigned: 'Zugewiesen', scheduled: 'Termin vereinbart',
  awaiting_customer: 'Rückfrage offen', in_progress: 'In Arbeit',
  completed: 'Erledigt', cancelled: 'Storniert',
  planned: 'Anfahrt geplant', departed: 'Unterwegs', delayed: 'Verspätet', arrived: 'Vor Ort',
};
const actions: Record<string, string> = {
  triage: 'Prüfung beginnen', request_information: 'Rückfrage vormerken', assign: 'Fachkraft zuweisen',
  schedule: 'Termin speichern', start: 'Einsatz beginnen', complete: 'Abschließen',
  cancel: 'Stornieren', reopen: 'Wieder öffnen', resume: 'Vorgang fortsetzen', arrival: 'Anfahrt melden',
  confirm_appointment: 'Termin bestätigen', appointment_note: 'Terminhinweis ändern',
};
const date = (value: string) => new Intl.DateTimeFormat('de-DE', {
  dateStyle: 'medium', timeStyle: 'short', timeZone: 'Europe/Berlin',
}).format(new Date(value));
const path = location.pathname.split('/').filter(Boolean);
const slug = path[0] === 'unternehmen' ? path[1] : undefined;
const base = slug ? '/unternehmen/' + encodeURIComponent(slug) : '';
const api = slug ? '/api/v1/tenants/' + encodeURIComponent(slug) : '';
const login = '/api/v1/login?returnTo=' + encodeURIComponent(location.pathname);
const staffRequest = (path[2] === 'betrieb' || path[2] === 'technik') && path[3] === 'anfragen';
const requestId = path[2] === 'anfragen' ? path[3] : staffRequest ? path[4] : undefined;
const isClaimLink = path[2] === 'anfragen' && path[4] === 'bestaetigen';

function Notice({ text }: { text: string }) {
  return text ? <p className="notice" role="alert">{text}</p> : null;
}
function Panel({ children }: { children: ReactNode }) {
  return <section className="panel">{children}</section>;
}
function Badge({ state }: { state: string }) {
  return <span className={'badge ' + state}>{states[state] || state}</span>;
}

type Portal = 'customer' | 'company' | 'technician' | 'platform';

function App() {
  const session = useLoad<Session>('/api/v1/session');
  const company = useLoad<Company>(slug ? api : undefined, session.data?.signedIn ? 1 : 0);
  // Authorization is checked with the real platform API before any platform UI is mounted.
  const platformProbe = useLoad<{ items: PlatformCompany[] }>(
    path[0] === 'plattform' && session.data?.signedIn ? '/api/v1/platform/overview' : undefined);
  const role = company.data?.role;
  const requestedView = new URLSearchParams(location.search).get('ansicht');
  const staffDetail = requestId && !isClaimLink && requestedView !== 'konto';
  const portal: Portal = path[0] === 'plattform' ? 'platform'
    : path[2] === 'technik' || staffDetail && (requestedView === 'technik' || role === 'technician') ? 'technician'
    : path[2] === 'betrieb' || staffDetail && (requestedView === 'betrieb' || role === 'admin' || role === 'dispatcher') ? 'company'
    : 'customer';
  const atCustomerHome = portal === 'customer' && !path[2] && Boolean(slug);
  const workspaceAccess = resolveWorkspaceAccess(portal, !!session.data?.signedIn, role,
    { ready: !!platformProbe.data, error: platformProbe.error });
  const catalog = useLoad<ServiceCategory[]>(atCustomerHome ? api + '/catalog' : undefined);
  useEffect(() => {
    const accent = company.data?.accent;
    if (accent && /^#[a-f0-9]{6}$/i.test(accent)) document.documentElement.style.setProperty('--tenant-accent', accent);
  }, [company.data?.accent]);

  if (!session.data || slug && !company.data) return <main id="inhalt"><Notice text={session.error || company.error}/>
    {!session.error && !company.error && <p role="status">TaskService wird geladen …</p>}</main>;
  const loginLink = '/api/v1/login?returnTo=' + encodeURIComponent(
    atCustomerHome ? base + '/konto' : location.pathname);
  async function logout() {
    try { await send('/api/v1/logout', {}, session.data!.csrfToken); location.assign(base || '/'); }
    catch { location.reload(); }
  }
  const demoBanner = session.data.demo && <aside className="demo-bar" aria-label="Demoversion">
    <strong>DEMO</strong><span>Nur Testdaten verwenden.{session.data.mailEnabled === false && ' E-Mail-Versand ist deaktiviert.'}</span>
  </aside>;
  if (path[0] === 'anmelden' && session.data.native) return <>{demoBanner}
    <header className="bar"><Wordmark href="/"/></header>
    <main id="inhalt" className="account-main"><NativeLogin session={session.data}/></main></>;
  if (path[0] === 'demo-datenschutz' && session.data.demo) return <>{demoBanner}
    <header className="bar"><Wordmark href="/"/></header>
    <main id="inhalt" className="account-main"><Panel><h1>Datenschutzhinweis zur Demo</h1>
      <p>Verwende ausschließlich Testdaten. Gib keine echten Kundendaten, vertraulichen Informationen oder privaten Dokumente ein.</p>
      <p>Die Demo speichert die Anmeldedaten deines Testkontos, geschützte Sitzungsschlüssel sowie die von dir erstellten Testanfragen.</p>
    </Panel></main></>;
  if (portal !== 'customer' && workspaceAccess !== 'allowed') return <>{demoBanner}
    <WorkspaceGate portal={portal} access={workspaceAccess} companyName={company.data?.name}
      detail={workspaceAccess === 'denied' && platformProbe.error !== 'Zugriff verweigert.' ? platformProbe.error : undefined}/>
  </>;
  if (atCustomerHome && company.data) return <>{demoBanner}
    <a className="skip" href="#inhalt">Zum Inhalt</a>
    <CustomerHome base={base} api={api} company={company.data} session={session.data}
      categories={catalog.data} catalogError={catalog.error}/>
  </>;
  const body = path[2] === 'einladungen' && path[3]
    ? <AcceptInvitation endpoint={api} base={base} login={loginLink + '&mode=workforce'} session={session.data} id={path[3]}/>
    : requestId && isClaimLink
    ? <ClaimRequest session={session.data} id={requestId}/>
    : requestId ? <RequestDetail session={session.data} id={requestId} portal={portal}/>
    : portal === 'platform' ? <Platform session={session.data}/>
    : company.data && portal !== 'customer'
      ? <Dashboard company={company.data} session={session.data} portal={portal}/>
      : company.data ? <Account session={session.data} loginLink={loginLink} logout={logout}/>
      : <Panel><h1>Ihr Kontakt zum Servicebetrieb</h1><p>Öffnen Sie das Portal Ihres Servicebetriebs über dessen persönlichen Link.</p></Panel>;

  return <>{demoBanner}
    <a className="skip" href="#inhalt">Zum Inhalt</a>
    {portal === 'customer' ? <div className="consumer-account">
      {company.data ? <CustomerHeader base={base} company={company.data}/> : <header className="bar"><Wordmark href="/"/></header>}
      <main id="inhalt" className="account-main">{body}</main>
      {company.data && <CustomerNav base={base} account/>}
    </div> : <Workspace portal={portal} company={company.data} signedIn={session.data.signedIn} logout={logout}>{body}</Workspace>}
  </>;
}

const workspaceLabels = { platform: 'Plattformverwaltung', company: 'Servicebetrieb', technician: 'Technik' };
const workspaceMenus = {
  platform: [{ name: 'Übersicht', id: 'overview', icon: LayoutDashboard },
    { name: 'Unternehmen', id: 'companies', icon: Building2 }, { name: 'Änderungsprotokoll', id: 'audit', icon: ShieldCheck }],
  company: [{ name: 'Übersicht', id: 'overview', icon: LayoutDashboard }, { name: 'Servicevorgänge', id: 'requests', icon: ClipboardList },
    { name: 'Techniker', id: 'technicians', icon: UsersRound }, { name: 'Team und Rechte', id: 'team', icon: ShieldCheck },
    { name: 'Integrationen', id: 'integrations', icon: Plug },
    { name: 'Unternehmenschat', id: 'conversations', icon: MessagesSquare }],
  technician: [{ name: 'Einsätze', id: 'requests', icon: ClipboardList }, { name: 'Tagesplan', id: 'schedule', icon: CalendarDays },
    { name: 'Chat', id: 'conversations', icon: MessagesSquare }]
};

function WorkspaceGate({ portal, access, companyName, detail }: {
  portal: Exclude<Portal, 'customer'>; access: 'sign-in' | 'checking' | 'denied'; companyName?: string; detail?: string;
}) {
  const heading = access === 'sign-in' ? 'Im Arbeitsbereich anmelden'
    : access === 'checking' ? 'Zugriff wird geprüft' : 'Kein Zugriff auf diesen Bereich';
  const destination = access === 'sign-in' ? login + '&mode=workforce' : (base || '/');
  const message = access === 'sign-in' ? 'Melde dich mit deinem Arbeitskonto an, um den Bereich zu öffnen.'
    : access === 'checking' ? 'Deine Berechtigungen werden sicher überprüft.'
    : 'Dein Konto hat für diesen Arbeitsbereich keine Berechtigung.';
  return <div className="workspace-gate">
    <header className="workspace-gate-top"><Wordmark href={base || '/'}/><span>{companyName || workspaceLabels[portal]}</span></header>
    <main id="inhalt" className="workspace-gate-main"><div className="workspace-gate-card">
      <div className="workspace-gate-icon"><LockKeyhole size={25} aria-hidden/></div>
      <p className="eyebrow">Sicherer Arbeitsbereich</p><h1>{heading}</h1><p>{message}</p>
      {access === 'denied' && detail && !detail.toLowerCase().includes('forbidden') && <p className="muted">Der Zugriff konnte nicht bestätigt werden.</p>}
      {access !== 'checking' && <a className="primary" href={destination}>{access === 'sign-in' ? 'Zur Anmeldung' : 'Zum Serviceportal'}</a>}
    </div></main>
  </div>;
}

function Workspace({ portal, company, signedIn, logout, children }: {
  portal: Exclude<Portal, 'customer'>; company?: Company; signedIn: boolean; logout: () => Promise<void>; children: ReactNode;
}) {
  const root = portal === 'platform' ? '/plattform' : base + (portal === 'technician' ? '/technik' : '/betrieb');
  const permitted = navigationForRole(portal, company?.role);
  const menu = workspaceMenus[portal].filter(item => permitted.includes(item.id));
  const defaultSection = menu[0].id;
  const [hash, setHash] = useState(location.hash.slice(1) || defaultSection);
  useEffect(() => {
    const listener = () => setHash(location.hash.slice(1) || defaultSection);
    addEventListener('hashchange', listener);
    return () => removeEventListener('hashchange', listener);
  }, [defaultSection]);
  return <div className={'workspace workspace-' + portal}>
    <aside className="workspace-sidebar" aria-label={workspaceLabels[portal]}>
      <div className="workspace-brand"><Wordmark href={root}/><span className="workspace-brand-mark">ONE</span></div>
      <p className="workspace-label">{workspaceLabels[portal]}</p>
      <nav aria-label="Arbeitsbereiche">
        {menu.map(item => <a key={item.id} href={root + '#' + item.id} aria-current={!requestId && hash === item.id ? 'location' : undefined}>
          <item.icon size={19} aria-hidden/><span>{item.name}</span>
        </a>)}
      </nav>
      <div className="workspace-sidebar-bottom"><ShieldCheck size={15} aria-hidden/>Geschützter Arbeitsbereich</div>
    </aside>
    <div className="workspace-content">
      <header className="workspace-top">
        <div className="workspace-breadcrumb"><span>TaskService</span><ChevronRight size={14} aria-hidden/>
          <strong>{company?.name || workspaceLabels[portal]}</strong></div>
        <div className="workspace-top-actions"><span className="workspace-role-tag">{portal === 'platform' ? 'Plattformadmin' : company?.role === 'dispatcher' ? 'Prüfung' : portal === 'technician' ? 'Technik' : 'Administration'}</span>
          {signedIn && <button className="workspace-signout" onClick={logout}><LogOut size={16} aria-hidden/><span>Abmelden</span></button>}
        </div>
      </header>
      <main id="inhalt" className="workspace-main">{children}</main>
    </div>
  </div>;
}

type PlatformCompany = {
  slug: string; name: string; accent: string; lifecycle: 'active' | 'suspended' | 'deactivated'; revision: number;
};
type PlatformAuditEntry = { id: string; slug: string; action: string; reason?: string; recordedAt: string };
const platformApi = '/api/v1/platform';
const companyState = { active: 'Aktiv', suspended: 'Gesperrt', deactivated: 'Deaktiviert' };
const platformAction: Record<string, string> = {
  provision: 'Unternehmen anlegen', brand: 'Marke ändern', suspend: 'Portal sperren',
  activate: 'Portal aktivieren', deactivate: 'Unternehmen deaktivieren', initial_admin_invited: 'Erste Administration eingeladen',
};

function Platform({ session }: { session: Session }) {
  const [reload, setReload] = useState(0);
  const [cursor, setCursor] = useState('');
  const [auditCursor, setAuditCursor] = useState<{ id: string; before: string }>();
  const overview = useLoad<{ items: PlatformCompany[]; nextCursor?: string }>(session.signedIn
    ? platformApi + '/overview' + (cursor ? '?cursor=' + encodeURIComponent(cursor) : '') : undefined, reload);
  const audit = useLoad<{ items: PlatformAuditEntry[]; next?: { id: string; before: string } }>(overview.data
    ? platformApi + '/audit' + (auditCursor ? '?' + new URLSearchParams({ cursor: auditCursor.id, before: auditCursor.before }) : '') : undefined, reload);
  const refresh = () => { setCursor(''); setAuditCursor(undefined); setReload(x => x + 1); };
  return <>
    <div id="overview" className="workspace-heading"><p className="eyebrow">TaskService</p><h1>Plattformverwaltung</h1>
      <p>Unternehmen, Portalzugänge und Änderungen verwalten.</p></div>
    {!session.signedIn && <Panel><h2>Anmelden</h2><p>Für die Verwaltung benötigen Sie ein berechtigtes Verwaltungskonto.</p>
      <a className="primary" href={login + '&mode=workforce'}>Zur Anmeldung</a></Panel>}
    <Notice text={overview.error}/>
    {session.signedIn && !overview.data && !overview.error && <p role="status">Unternehmen werden geladen …</p>}
    {overview.data && <>
      <div className="workspace-metrics" aria-label="Unternehmen in dieser Ansicht">
        <div><Building2 aria-hidden/><span>Unternehmen</span><strong>{overview.data.items.length}</strong></div>
        <div><Activity aria-hidden/><span>Aktive Unternehmen</span><strong>{overview.data.items.filter(x => x.lifecycle === 'active').length}</strong></div>
        <div><ShieldCheck aria-hidden/><span>Gesperrte Unternehmen</span><strong>{overview.data.items.filter(x => x.lifecycle === 'suspended').length}</strong></div>
      </div>
      <div className="platform-company-grid" id="companies">
        <Panel>
          <div className="section-line platform-section-title"><div><h2>Unternehmen</h2>
            <p className="muted">Alle Portale, Zugänge und Marken an einem Ort.</p></div>
            <span className="company-record-count">{overview.data.items.length} erfasst</span>
          </div>
          {overview.data.items.length === 0 && <p className="muted">Es wurden noch keine Unternehmen angelegt.</p>}
          <div className="report-history company-directory">{overview.data.items.map(item =>
            <details key={item.slug} className="company-record">
              <summary><span className="company-avatar" aria-hidden>{item.name.slice(0, 1).toUpperCase()}</span>
                <span className="company-record-info"><strong>{item.name}</strong>
                  <small>/unternehmen/{item.slug}</small></span>
                <span className={'company-status company-status-' + item.lifecycle}>{companyState[item.lifecycle]}</span>
              </summary>
              <div className="company-record-body">
                {item.lifecycle !== 'deactivated' && <CompanyEditor company={item} session={session} refresh={refresh}/>}
                {item.lifecycle === 'active' && <InitialAdminSetup session={session} onChange={refresh}
                  companyUrl={platformApi + '/companies/' + encodeURIComponent(item.slug)}/>}
              </div>
            </details>)}</div>
          <div className="platform-pagination">
            {overview.data.nextCursor && <button onClick={() => setCursor(overview.data!.nextCursor!)}>Weitere Unternehmen</button>}
            {cursor && <button onClick={() => setCursor('')}>Zur ersten Seite</button>}
          </div>
        </Panel>
        <Panel><div className="company-create-heading"><div className="company-create-icon"><Plus size={19} aria-hidden/></div>
          <div><h2>Neues Unternehmen</h2><p className="muted">Ein neues Serviceportal einrichten.</p></div>
        </div>
          <details className="company-create"><summary>Neues Portal erstellen <Plus size={17} aria-hidden/></summary>
            <CompanyEditor session={session} refresh={refresh}/>
          </details>
        </Panel>
      </div>
      <div id="audit"><Panel><h2>Änderungsprotokoll</h2><Notice text={audit.error}/>
        {audit.data?.items.length === 0 && <p className="muted">Es sind noch keine Änderungen erfasst.</p>}
        {audit.data && <ul className="rows">{audit.data.items.map(row => <li key={row.id}>
          <span><strong>{platformAction[row.action]}</strong><small>{row.slug}</small>
            {row.reason && <small>{row.reason}</small>}
          </span><small>{date(row.recordedAt)}</small>
        </li>)}</ul>}
        {audit.data?.next && <button onClick={() => setAuditCursor(audit.data!.next)}>Ältere Änderungen</button>}
        {auditCursor && <button onClick={() => setAuditCursor(undefined)}>Neueste Änderungen</button>}
      </Panel></div>
    </>}
  </>;
}

function CompanyEditor({ session, refresh, company }: { session: Session; refresh: () => void; company?: PlatformCompany }) {
  const [action, setAction] = useState(company ? 'brand' : 'provision');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const identity = action === 'brand' || action === 'provision';
  async function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); setError(''); setBusy(true);
    const data = Object.fromEntries(new FormData(e.currentTarget));
    try {
      await send(company ? platformApi + '/companies/' + encodeURIComponent(company.slug) + '/commands' : platformApi + '/companies',
        company ? { ...data, action } : data, session.csrfToken, crypto.randomUUID(), company?.revision ?? 0);
      refresh();
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <form onSubmit={save}>
    {company
      ? <label>Unternehmensaktion<select value={action} onChange={e => setAction(e.target.value)}>
        {['brand', company.lifecycle === 'active' ? 'suspend' : 'activate', 'deactivate'].map(key =>
          <option key={key} value={key}>{platformAction[key]}</option>)}
      </select></label>
      : <label>Portaladresse <small>Kleinbuchstaben, Ziffern und Bindestriche, zum Beispiel werkstatt-nord.</small>
        <input name="slug" maxLength={63} pattern="[a-z0-9]+(?:-[a-z0-9]+)*" required/></label>}
    {identity
      ? <div className="columns">
        <label>Unternehmensname<input name="name" defaultValue={company?.name} maxLength={120} required/></label>
        <label>Markenfarbe<input name="accent" type="color" defaultValue={company?.accent || '#075aca'} required/></label>
      </div>
      : <><label>Begründung<textarea name="reason" maxLength={500} rows={2} required/></label>
        {action === 'deactivate' && <p>Das Portal wird deaktiviert. Die gespeicherten Vorgänge bleiben erhalten.</p>}
      </>}
    <button className="primary" disabled={busy}>{busy ? 'Wird gespeichert …' : platformAction[action]}</button>
    <Notice text={error}/>
  </form>;
}

type RequestPage = { items: RequestRow[]; nextCursor?: string };
const categoryNames: Record<string, string> = {
  repair: 'Reparaturen', cleaning: 'Reinigung', electric: 'Elektro & Sanitär', garden: 'Haus & Garten',
  furniture: 'Möbelmontage', other: 'Weitere Services',
};
const berlinDay = (value: string | Date) => new Intl.DateTimeFormat('sv-SE', {
  year: 'numeric', month: '2-digit', day: '2-digit', timeZone: 'Europe/Berlin',
}).format(new Date(value));
const berlinTime = (value: string) => new Intl.DateTimeFormat('de-DE', {
  timeStyle: 'short', timeZone: 'Europe/Berlin',
}).format(new Date(value));

function Account({ session, loginLink, logout }: { session: Session; loginLink: string; logout: () => Promise<void> }) {
  const [cursor, setCursor] = useState('');
  const requests = useLoad<RequestPage>(session.signedIn
    ? api + '/requests?view=mine' + (cursor ? '&cursor=' + encodeURIComponent(cursor) : '') : undefined);
  return <>
    <div className="account-heading"><div><p className="eyebrow">Kundenkonto</p><h1>Meine Anfragen</h1>
      <p>Verfolge den Stand deiner Anfragen, Termine und Dokumente.</p></div>
      {session.signedIn && <button className="text-button" onClick={logout}>Abmelden</button>}
    </div>
    {!session.signedIn ? <Panel><div className="account-empty"><UserRound aria-hidden/><h2>In dein Kundenkonto einloggen</h2>
      <p>Nach der Anmeldung siehst du deine bestätigten Anfragen und Nachrichten.</p>
      <a className="primary" href={loginLink}>Anmelden</a></div></Panel>
      : <Panel>
        <div className="section-line"><h2>Deine Servicevorgänge</h2><a href={base + '?anfrage=neu'}>Neue Anfrage</a></div>
        <Notice text={requests.error}/>
        {!requests.data && !requests.error && <p role="status">Anfragen werden geladen …</p>}
        {requests.data?.items.length === 0 && <div className="account-empty"><ClipboardList aria-hidden/>
          <h3>Hier erscheinen deine bestätigten Anfragen</h3><p>Wähle einen Service, um eine neue Anfrage zu stellen.</p>
          <a href={base + '#services'} className="primary">Services entdecken</a></div>}
        {requests.data && <ul className="request-cards">{requests.data.items.map(row =>
          <li key={row.id}><a className="request-card" href={base + '/anfragen/' + row.id + '?ansicht=konto'}>
            <div><small>{row.serviceCategory && categoryNames[row.serviceCategory]}</small><strong>{row.title}</strong>
              <small>{row.appointment ? 'Termin: ' + date(row.appointment.startsAt) : date(row.createdAt)}</small></div>
            <Badge state={row.state}/>
          </a></li>)}</ul>}
        {requests.data?.nextCursor && <button onClick={() => setCursor(requests.data!.nextCursor!)}>Weitere Anfragen</button>}
        {cursor && <button onClick={() => setCursor('')}>Zur ersten Seite</button>}
      </Panel>}
  </>;
}

function Dashboard({ company, session, portal }: { company: Company; session: Session; portal: 'company' | 'technician' }) {
  const isCompany = portal === 'company';
  const allowed = isCompany ? company.role === 'admin' || company.role === 'dispatcher' : company.role === 'technician';
  const [cursor, setCursor] = useState('');
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState('');
  const [sort, setSort] = useState(isCompany ? 'recent' : 'appointment');
  const list = useLoad<RequestPage>(session.signedIn && allowed ? api + '/requests?view=staff'
    + (cursor ? '&cursor=' + encodeURIComponent(cursor) : '') + (query ? '&q=' + encodeURIComponent(query) : '') : undefined);
  const technicians = useLoad<{ id: string; name: string }[]>(session.signedIn && allowed && isCompany ? api + '/technicians' : undefined);
  const result = list.data?.items ?? [];
  const today = berlinDay(new Date());
  const daily = result.filter(row => row.appointment && berlinDay(row.appointment.startsAt) === today)
    .sort((a, b) => Date.parse(a.appointment!.startsAt) - Date.parse(b.appointment!.startsAt));
  const visible = result.filter(row => !filter || row.state === filter).sort((a, b) =>
    sort === 'title' ? a.title.localeCompare(b.title, 'de')
      : sort === 'appointment' ? (Date.parse(a.appointment?.startsAt ?? '') || Infinity) -
        (Date.parse(b.appointment?.startsAt ?? '') || Infinity) : Date.parse(b.createdAt) - Date.parse(a.createdAt));
  const filters = [...new Set(result.map(row => row.state))];
  const detailPath = (row: RequestRow) => base + (isCompany ? '/betrieb' : '/technik') + '/anfragen/' + row.id;
  if (!session.signedIn) return <Panel><h1>{isCompany ? 'Servicebetrieb' : 'Technik'}</h1><p>Melden Sie sich mit Ihrem Arbeitskonto an.</p>
    <a href={login + '&mode=workforce'} className="primary">Anmelden</a></Panel>;
  if (!allowed) return <Panel><h1>Zugang zum Arbeitsbereich</h1><Notice text="Ihr Konto hat keinen Zugriff auf diesen Bereich."/></Panel>;
  return <>
    <div id="overview" className="workspace-heading"><p className="eyebrow">{company.name}</p>
      <h1>{isCompany ? 'Serviceübersicht' : 'Meine Einsätze'}</h1>
      <p>{isCompany ? 'Anfragen, Disposition und Kommunikation an einem Ort.' : 'Dein Tagesplan, deine Aufträge und Nachrichten.'}</p>
    </div>
    {list.data && <div className="workspace-metrics" aria-label="Vorgänge in dieser Ansicht">
      <div><ClipboardList aria-hidden/><span>{isCompany ? 'Vorgänge' : 'Zugewiesene Einsätze'}</span><strong>{result.length}</strong></div>
      <div><CalendarDays aria-hidden/><span>Heute geplant</span><strong>{daily.length}</strong></div>
      <div><Activity aria-hidden/><span>In Arbeit</span><strong>{result.filter(r => r.state === 'in_progress').length}</strong></div>
    </div>}
    {!isCompany && <section id="schedule" className="panel day-plan"><h2>Tagesplan</h2>
      {daily.length === 0 && <p className="muted">Heute sind in dieser Ansicht keine Termine geplant.</p>}
      <ol>{daily.map(row => <li key={row.id}><time dateTime={row.appointment!.startsAt}>{berlinTime(row.appointment!.startsAt)}</time>
        <a href={detailPath(row)}>{row.title}<small>Bis {berlinTime(row.appointment!.endsAt)} Uhr</small></a></li>)}</ol>
    </section>}
    <section id="requests" className="panel operations-list">
      <div className="section-line"><h2>{isCompany ? 'Servicevorgänge' : 'Zugewiesene Aufträge'}</h2></div>
      <form className="workspace-filters" onSubmit={e => {e.preventDefault(); setCursor(''); setFilter(''); setQuery(search.trim());}}>
        <label>Vorgang suchen<input type="search" value={search} maxLength={160} placeholder="Titel eingeben" onChange={e => {
          setSearch(e.target.value); if (!e.target.value) {setCursor(''); setQuery('');}
        }}/></label>
        <button type="submit" className="primary">Suchen</button>
        <label>Status<select value={filter} onChange={e => setFilter(e.target.value)}>
          <option value="">Alle in dieser Ansicht</option>{filters.map(value => <option key={value} value={value}>{states[value]}</option>)}
        </select></label>
        <label>Sortierung<select value={sort} onChange={e => setSort(e.target.value)}>
          <option value="recent">Neueste zuerst</option><option value="appointment">Termin</option><option value="title">Titel A–Z</option>
        </select></label>
      </form>
      <Notice text={list.error}/>
      {!list.data && !list.error && <p role="status">Vorgänge werden geladen …</p>}
      {list.data && visible.length === 0 && <p className="muted">{query || filter ? 'Keine passenden Vorgänge gefunden.' : 'Hier gibt es noch keine Vorgänge.'}</p>}
      {isCompany ? visible.length > 0 && <div className="request-table-wrap"><table className="request-table">
        <thead><tr><th scope="col">Anfrage</th><th scope="col">Status</th><th scope="col">Termin</th></tr></thead>
        <tbody>{visible.map(row => <tr key={row.id}><th scope="row"><a href={detailPath(row)}>{row.title}</a>
          <small>{row.serviceCategory ? categoryNames[row.serviceCategory] : date(row.createdAt)}</small></th>
          <td><Badge state={row.state}/></td><td>{row.appointment ? date(row.appointment.startsAt) : 'Offen'}</td></tr>)}</tbody>
      </table></div> : <ul className="request-cards technician-cards">{visible.map(row => <li key={row.id}>
        <a className="request-card" href={detailPath(row)}><div><strong>{row.title}</strong>
          <small>{row.appointment ? date(row.appointment.startsAt) : 'Noch kein Termin'}</small></div><Badge state={row.state}/></a>
      </li>)}</ul>}
      {list.data?.nextCursor && <button onClick={() => setCursor(list.data!.nextCursor!)}>Weitere Vorgänge</button>}
      {cursor && <button onClick={() => setCursor('')}>Zur ersten Seite</button>}
    </section>
    {isCompany && <section id="technicians" className="panel"><h2>Techniker</h2><Notice text={technicians.error}/>
      {!technicians.data && !technicians.error && <p role="status">Techniker werden geladen …</p>}
      {technicians.data?.length === 0 && <p className="muted">Aktuell sind keine Techniker zuweisbar.</p>}
      {technicians.data && <ul className="staff-directory">{technicians.data.map(tech => <li key={tech.id}><UserRound aria-hidden/>{tech.name}</li>)}</ul>}
    </section>}
    {isCompany && company.role === 'admin' && <><Team api={api} session={session}/><IntegrationInbox/></>}
    <div id="conversations"><Chat session={session} channel="company" title="Unternehmenschat"/></div>
  </>;
}

function IntegrationInbox() {
  const [refresh, setRefresh] = useState(0);
  const inbox = useLoad<{ eventId: string; eventType: string; revision: number; receivedAt: string }[]>(
    api + '/integrations/inbox', refresh);
  return <section className="panel" id="integrations">
    <div className="section-line"><h2>Eingegangene Integrationsereignisse</h2>
      <button className="text-button" onClick={() => setRefresh(x => x + 1)}>Aktualisieren</button></div>
    <Notice text={inbox.error}/>
    {!inbox.data && !inbox.error && <p role="status">Ereignisse werden geladen …</p>}
    {inbox.data?.length === 0 && <p className="muted">Es sind noch keine signierten Ereignisse eingegangen.</p>}
    {inbox.data && inbox.data.length > 0 && <ul className="rows">
      {inbox.data.map(event => <li key={event.eventId}><span><strong>{event.eventType}</strong>
        <small>Revision {event.revision}</small></span><small>{date(event.receivedAt)}</small></li>)}
    </ul>}
  </section>;
}

function ClaimRequest({ session, id }: { session: Session; id: string }) {
  const cacheKey = 'claim:' + slug + ':' + id;
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    if (/^#[0-9a-f]{64}$/i.test(location.hash)) {
      sessionStorage.setItem(cacheKey, location.hash.slice(1));
      history.replaceState(null, '', location.pathname);
    }
  }, [cacheKey]);
  async function confirm() {
    setBusy(true); setError('');
    try {
      await send(api + '/requests/' + id + '/claim', { token: sessionStorage.getItem(cacheKey) || '' }, session.csrfToken);
      sessionStorage.removeItem(cacheKey);
      location.replace(base + '/anfragen/' + id);
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <Panel><h1>Anfrage bestätigen</h1><p>Verknüpfen Sie diese Anfrage mit Ihrem Konto.</p>
    {session.signedIn ? <button className="primary" onClick={confirm} disabled={busy}>Anfrage bestätigen</button>
      : <a className="primary" href={login}>Anmelden und bestätigen</a>}
    <Notice text={error}/>
  </Panel>;
}

function RequestDetail({ session, id, portal }: { session: Session; id: string; portal: Portal }) {
  const [revision, setRevision] = useState(0);
  const detail = useLoad<Detail>(session.signedIn ? api + '/requests/' + id : undefined, revision);
  const row = detail.data;
  if (!session.signedIn) return <Panel><h1>Servicevorgang</h1>
    <a href={portal === 'customer' ? login : login + '&mode=workforce'}>Anmelden</a></Panel>;
  if (!row) return <Panel>{detail.error ? <Notice text={detail.error}/> : <p role="status">Vorgang wird geladen …</p>}</Panel>;
  return <>
    <div className="workspace-heading"><a href={base + (portal === 'company' ? '/betrieb#requests' : portal === 'technician' ? '/technik#requests' : '/konto')}>Übersicht</a>
      <h1>{row.title}</h1><Badge state={row.state}/></div>
    <div className="columns">
      <Panel><h2>Beschreibung</h2><p className="preserve">{row.description}</p>
        {row.contactName && <p><strong>Kontakt</strong><br/>{row.contactName}{row.contactPhone && <><br/>{row.contactPhone}</>}</p>}
        {row.photos?.length > 0 && <div className="attached-photos"><h3>Fotos zur Anfrage</h3><ul>
          {row.photos.map(photo => <li key={photo.id}>{photo.scanStatus === 'clean'
            ? <a href={sessionUrl(api + '/requests/' + id + '/photos/' + photo.id)}>Foto {photo.ordinal} herunterladen</a>
            : 'Foto ' + photo.ordinal + (photo.scanStatus === 'pending' ? ' · Wird geprüft' : ' · Nicht verfügbar')}</li>)}
        </ul></div>}
        {row.appointment && <><h2>Einsatztermin</h2>
          <p>{date(row.appointment.startsAt)} bis {date(row.appointment.endsAt)} Uhr (Berlin)</p>
          <Badge state={row.appointment.arrivalStatus}/>
          {row.appointment.customerConfirmedAt && <small>Bestätigt am {date(row.appointment.customerConfirmedAt)}</small>}
          {row.appointment.updatedAt && <small>Aktualisiert am {date(row.appointment.updatedAt)}</small>}
          {row.appointment.customerNote && <p className="preserve">{row.appointment.customerNote}</p>}</>}
      </Panel>
      <Panel><h2>Verlauf</h2><ol className="history">{row.history.map((x, i) =>
        <li key={i}><strong>{states[x.state] || x.state}</strong><small>{date(x.at)}</small>
          {x.privateNote && <p className="preserve">{x.privateNote}</p>}
        </li>)}</ol></Panel>
    </div>
    {row.actions.length > 0 && <Commands key={row.revision} row={row} session={session} refresh={() => setRevision(x => x + 1)}/>}
    <ReportPanel id={id} session={session}/>
    <InvoicesPanel id={id} session={session}/>
    <Chat session={session} id={id} channel="customer" title="Nachrichten zur Anfrage"/>
    {row.canReadInternal && <Chat session={session} id={id} channel="internal" title="Interne Notizen"/>}
  </>;
}

const reportStates: Record<string, string> = {
  submitted: 'Zur Prüfung eingereicht', rejected: 'Zur Überarbeitung zurückgegeben',
  approved: 'Geprüft', published: 'Veröffentlicht', superseded: 'Durch neue Version ersetzt', revoked: 'Veröffentlichung widerrufen',
};
const reportActions: Record<string, string> = {
  submit: 'Bericht einreichen', approve: 'Bericht genehmigen', reject: 'Zur Überarbeitung zurückgeben',
  publish: 'Für den Kunden veröffentlichen', revoke: 'Veröffentlichung widerrufen',
};

function ReportPanel({ id, session }: { id: string; session: Session }) {
  const url = api + '/requests/' + id + '/report';
  const [reload, setReload] = useState(0);
  const report = useLoad<ReportView>(url, reload);
  const data = report.data;
  const current = data?.versions?.[0];
  const download = (version: number) => sessionUrl(url + '/versions/' + version + '/pdf');

  return <Panel>
    <div className="panel-heading"><h2>Arbeitsbericht</h2>
      {data?.versions && <button className="link" onClick={() => setReload(x => x + 1)}>Bericht aktualisieren</button>}
    </div>
    <Notice text={report.error}/>
    {!data && !report.error && <p role="status">Arbeitsbericht wird geladen …</p>}
    {data && (data.versions ? <>
      {current ? <p><strong>Version {current.version}</strong> · {reportStates[current.status]}</p> :
        <p className="muted">Für diesen Vorgang liegt noch kein Arbeitsbericht vor.</p>}
      {current?.status === 'approved' && !data.pdfReady &&
        <p role="status">Das PDF wird erstellt. Aktualisieren Sie den Bericht, um den Stand zu prüfen.</p>}
      {current && data.pdfReady && <p><a href={download(current.version)}>PDF zu Version {current.version} herunterladen</a></p>}
      {data.versions.length > 0 && <div className="report-history">{data.versions.map(version =>
        <details key={version.version}><summary>Version {version.version} · {reportStates[version.status]}</summary>
          <small>Eingereicht am {date(version.submittedAt)}</small>
          {version.reviewReason && <p className="notice">Rückmeldung zur Überarbeitung: {version.reviewReason}</p>}
          <ul>{version.activities.map((item, index) => <li key={index}>{item.description} · {item.minutes} Min.</li>)}</ul>
          <p className="preserve"><strong>Ergebnis</strong><br/>{version.result}</p>
          {version.materials && <p className="preserve"><strong>Material</strong><br/>{version.materials}</p>}
        </details>)}</div>}
      {!!data.actions?.length && <ReportForm url={url} data={data} session={session} refresh={() => setReload(x => x + 1)}/>}
    </> : data.published ?
      <p><a href={download(data.published.version)}>Arbeitsbericht als PDF herunterladen</a><small>
        Version {data.published.version} · Veröffentlicht am {date(data.published.publishedAt)}
      </small></p> : <p className="muted">Hier erscheint Ihr Arbeitsbericht, sobald er veröffentlicht wurde.</p>)}
  </Panel>;
}

function ReportForm({ url, data, session, refresh }: { url: string; data: ReportView; session: Session; refresh: () => void }) {
  const current = data.versions?.[0];
  const template = current?.status === 'rejected' ? current : undefined;
  const [rows, setRows] = useState(() => (template?.activities || [{ description: '', minutes: undefined }])
    .map(item => ({ ...item, id: crypto.randomUUID() })));
  const [action, setAction] = useState(data.actions![0]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); setError(''); setBusy(true);
    const form = new FormData(e.currentTarget);
    const body = action === 'submit' ? {
      action, result: form.get('result'), materials: form.get('materials'),
      activities: rows.map(row => ({ description: form.get(row.id), minutes: Number(form.get(row.id + '-minutes')) })),
    } : { action, version: action === 'revoke' ? data.publishedVersion : current?.version, reason: form.get('reason') };
    try {
      await send(url + '/commands', body, session.csrfToken, crypto.randomUUID(), data.revision);
      refresh();
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <form onSubmit={submit}>
    <label>Aktion für den Arbeitsbericht<select value={action} onChange={e => setAction(e.target.value)}>
      {data.actions!.map(key => <option key={key} value={key}>{reportActions[key]}</option>)}
    </select></label>
    {action === 'submit' && <>
      {rows.map((item, index) => <div className="report-activity" key={item.id}>
        <label>Tätigkeit {index + 1}<input name={item.id} defaultValue={item.description} maxLength={240} required/></label>
        <label>Dauer in Minuten<input name={item.id + '-minutes'} defaultValue={item.minutes} type="number" min={1} max={1440} required/></label>
        {rows.length > 1 && <button type="button" onClick={() => setRows(rows.filter(x => x.id !== item.id))}>Entfernen<span className="sr-only"> · Tätigkeit {index + 1}</span></button>}
      </div>)}
      {rows.length < 12 && <button type="button" onClick={() => setRows([...rows, { id: crypto.randomUUID(), description: '', minutes: undefined }])}>Tätigkeit hinzufügen</button>}
      <label>Ergebnis<textarea name="result" rows={3} defaultValue={template?.result} maxLength={3000} required/></label>
      <label>Material <small>Falls Material verwendet wurde.</small><textarea name="materials" rows={2} defaultValue={template?.materials} maxLength={2000}/></label>
    </>}
    {action === 'reject' && <label>Begründung<textarea name="reason" maxLength={500} rows={2} required/></label>}
    <button className="primary" disabled={busy}>{busy ? 'Wird gespeichert …' : reportActions[action]}</button>
    <Notice text={error}/>
  </form>;
}

const invoiceStatus: Record<string, string> = {
  uploaded: 'In Prüfung', approved: 'Genehmigt', rejected: 'Zurückgegeben',
  published: 'Veröffentlicht', superseded: 'Durch neue Version ersetzt', revoked: 'Veröffentlichung widerrufen',
};
const invoiceScan: Record<string, string> = {
  pending: 'Sicherheitsprüfung läuft', clean: 'Sicherheitsprüfung bestanden',
  quarantined: 'In Quarantäne', failed: 'Sicherheitsprüfung fehlgeschlagen',
};
const invoiceActions: Record<string, string> = {
  upload: 'Neue Version hochladen', approve: 'Rechnung genehmigen', reject: 'Zurückgeben',
  publish: 'Für den Kunden veröffentlichen', revoke: 'Veröffentlichung widerrufen',
};

function InvoicesPanel({ id, session }: { id: string; session: Session }) {
  const [reload, setReload] = useState(0);
  const [cursor, setCursor] = useState('');
  const url = api + '/requests/' + id + '/invoices';
  const invoices = useLoad<{ canUpload: boolean; items: InvoiceItem[]; nextCursor?: string }>(
    url + (cursor ? '?cursor=' + encodeURIComponent(cursor) : ''), reload);
  const refresh = () => setReload(x => x + 1);
  return <Panel>
    <div className="panel-heading"><h2>Rechnungen</h2>
      {invoices.data && (invoices.data.canUpload || invoices.data.items.some(x => x.revision !== null)) &&
        <button className="link" onClick={refresh}>Rechnungen aktualisieren</button>}
    </div>
    <Notice text={invoices.error}/>
    {!invoices.data && !invoices.error && <p role="status">Rechnungen werden geladen …</p>}
    {invoices.data?.items.length === 0 && <p className="muted">Für diesen Vorgang sind noch keine Rechnungen verfügbar.</p>}
    {invoices.data?.items.map(invoice => <InvoiceRow key={invoice.id} invoice={invoice} url={url} session={session} refresh={refresh}/>)}
    {invoices.data?.nextCursor && <button onClick={() => setCursor(invoices.data!.nextCursor!)}>Weitere Rechnungen</button>}
    {cursor && <button onClick={() => setCursor('')}>Zur ersten Seite</button>}
    {invoices.data?.canUpload && <InvoiceUploadForm url={url} session={session} refresh={refresh}/>}
  </Panel>;
}

function InvoiceRow({ invoice, url, session, refresh }: { invoice: InvoiceItem; url: string; session: Session; refresh: () => void }) {
  const [showHistory, setShowHistory] = useState(false);
  const [cursor, setCursor] = useState<number>();
  const [action, setAction] = useState(invoice.actions[0]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const itemUrl = url + '/' + invoice.id;
  const history = useLoad<{ items: InvoiceVersion[]; nextCursor?: number }>(
    showHistory ? itemUrl + (cursor ? '?before=' + cursor : '') : undefined);
  const internal = invoice.revision != null;
  async function change(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); setError(''); setBusy(true);
    const reason = new FormData(e.currentTarget).get('reason');
    try {
      await send(itemUrl + '/commands', { action, reason, version: action === 'revoke' ? invoice.publishedVersion : invoice.version },
        session.csrfToken, crypto.randomUUID(), invoice.revision ?? undefined);
      refresh();
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <div className="invoice-item">
    <p><strong>{invoice.originalName}</strong>
      <small>{internal
        ? `Version ${invoice.version} · ${invoiceStatus[invoice.status]} · ${invoiceScan[invoice.scanStatus]}`
        : invoice.publishedAt && 'Veröffentlicht am ' + date(invoice.publishedAt)}</small>
    </p>
    {invoice.canDownload && <p><a href={sessionUrl(itemUrl + '/versions/' + invoice.version + '/download')}>Original herunterladen</a></p>}
    {internal && <button className="link" aria-expanded={showHistory} onClick={() => { setShowHistory(!showHistory); setCursor(undefined); }}>
      Versionsverlauf {showHistory ? 'ausblenden' : 'anzeigen'}
    </button>}
    {history.data && <ul className="rows">{history.data.items.map(version => <li key={version.version}>
      <span><strong>Version {version.version} · {version.originalName}</strong>
        <small>{invoiceStatus[version.status]} · {invoiceScan[version.scanStatus]}</small>
        {version.reviewReason && <small>{version.reviewReason}</small>}
      </span><small>{date(version.uploadedAt)}</small>
    </li>)}</ul>}
    {history.data?.nextCursor && <button onClick={() => setCursor(history.data!.nextCursor)}>Ältere Versionen</button>}
    {cursor && <button onClick={() => setCursor(undefined)}>Neueste Versionen</button>}
    <Notice text={history.error || error}/>
    {!!invoice.actions.length && <>
      <label>Aktion für die Rechnung<select value={action} onChange={e => setAction(e.target.value)}>
        {invoice.actions.map(a => <option key={a} value={a}>{invoiceActions[a]}</option>)}
      </select></label>
      {action === 'upload' ? <InvoiceUploadForm url={url} invoice={invoice} session={session} refresh={refresh}/> :
        <form onSubmit={change}>
          {action === 'reject' && <label>Begründung<textarea name="reason" maxLength={500} rows={2} required/></label>}
          <button className="primary" disabled={busy}>{invoiceActions[action]}</button>
        </form>}
    </>}
  </div>;
}

function InvoiceUploadForm({ url, invoice, session, refresh }: { url: string; invoice?: InvoiceItem; session: Session; refresh: () => void }) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  async function upload(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); setError(''); setBusy(true);
    const file = new FormData(e.currentTarget).get('invoice');
    try {
      if (!(file instanceof File) || file.size < 1 || file.size > 10 * 1024 * 1024)
        throw new Error('Bitte wählen Sie eine Datei mit höchstens 10 MB.');
      await uploadInvoice(url, file, session.csrfToken, invoice);
      refresh();
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <form onSubmit={upload}>
    <label>{invoice ? 'Korrigiertes Original' : 'Rechnungsoriginal'} <small>PDF oder XML bis 10 MB.</small>
      <input type="file" name="invoice" accept=".pdf,.xml" required/></label>
    <button className="primary" disabled={busy}>{busy ? 'Wird hochgeladen …' : 'Datei hochladen'}</button>
    <Notice text={error}/>
  </form>;
}

function Commands({ row, session, refresh }: { row: Detail; session: Session; refresh: () => void }) {
  const [action, setAction] = useState(row.actions[0]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const technicians = useLoad<{ id: string; name: string }[]>(action === 'assign' ? api + '/technicians' : undefined);
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); setError(''); setBusy(true);
    const data = new FormData(e.currentTarget);
    const body: Record<string, string> = { action };
    if (action === 'assign') body.technicianId = String(data.get('technicianId'));
    if (action === 'arrival') body.arrivalStatus = String(data.get('arrivalStatus'));
    try {
      if (action === 'schedule') {
        body.startsAtLocal = String(data.get('startsAt'));
        body.endsAtLocal = String(data.get('endsAt'));
        body.startsAtFold = String(data.get('startsAtFold'));
        body.endsAtFold = String(data.get('endsAtFold'));
      }
      if (data.has('note')) body.note = String(data.get('note'));
      await send(api + '/requests/' + row.id + '/commands', body, session.csrfToken, crypto.randomUUID(), row.revision); refresh(); }
    catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <Panel><h2>Vorgang bearbeiten</h2><form onSubmit={submit}>
    <label>Aktion<select aria-label="Aktion" value={action} onChange={e => setAction(e.target.value)}>
      {row.actions.map(key => <option key={key} value={key}>{actions[key]}</option>)}
    </select></label>
    {action === 'assign' && <label>Fachkraft<select name="technicianId" required>
      <option value="">Bitte wählen</option>
      {technicians.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}
    </select></label>}
    {action === 'schedule' && <>
      <div className="columns">
        <label>Beginn (Berliner Zeit)<input name="startsAt" type="datetime-local" required/></label>
        <label>Ende (Berliner Zeit)<input name="endsAt" type="datetime-local" required/></label>
      </div>
      <details><summary>Falls eine Uhrzeit durch die Zeitumstellung doppelt vorkommt</summary>
        <div className="columns">
          <label>Beginn<select name="startsAtFold"><option value="">Keine Auswahl</option>
            <option value="earlier">Erste Stunde</option><option value="later">Zweite Stunde</option>
          </select></label>
          <label>Ende<select name="endsAtFold"><option value="">Keine Auswahl</option>
            <option value="earlier">Erste Stunde</option><option value="later">Zweite Stunde</option>
          </select></label>
        </div>
      </details>
    </>}
    {(action === 'schedule' || action === 'appointment_note') && <label>Hinweis für den Kunden<textarea name="note"
      maxLength={500} rows={2} defaultValue={row.appointment?.customerNote}/></label>}
    {action === 'reopen' && <label>Interner Grund für die Wiedereröffnung<textarea name="note" required maxLength={500} rows={2}/></label>}
    {action === 'arrival' && <label>Anfahrt<select aria-label="Anfahrt" name="arrivalStatus">
      <option value="departed">Unterwegs</option><option value="delayed">Verspätet</option><option value="arrived">Vor Ort</option>
    </select></label>}
    <button className="primary" disabled={busy}>{action === 'confirm_appointment' ? 'Termin bestätigen' : 'Änderung speichern'}</button><Notice text={error || technicians.error}/>
  </form></Panel>;
}

function Chat({ session, id, channel, title }: { session: Session; id?: string; channel: string; title: string }) {
  const baseUrl = api + '/rooms/' + channel + (id ? '?requestId=' + id : '');
  const [after, setAfter] = useState<number>();
  const url = after ? baseUrl + (id ? '&' : '?') + 'after=' + after : baseUrl;
  const [tick, setTick] = useState(0);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const payload = useLoad<{ items: Message[]; next?: number }>(url, tick);

  useEffect(() => {
    if (!slug) return;
    const hub = new HubConnectionBuilder()
      .withUrl(sessionUrl('/hubs/conversations'), { skipNegotiation: true, transport: HttpTransportType.WebSockets })
      .configureLogging(LogLevel.None)
      .withAutomaticReconnect()
      .build();
    let disposed = false;
    let latest = 0;
    function subscribe() {
      if (disposed) return;
      hub.stream<number>('Watch', slug, channel, id ?? null, latest).subscribe({
        next: value => {
          latest = value;
          setTick(x => x + 1);
        },
        error: () => { if (!disposed) setTick(x => x + 1); },
        complete: () => {},
      });
    }
    hub.onreconnected(() => { setTick(x => x + 1); subscribe(); });
    void hub.start().then(subscribe).catch(() => { /* REST and manual refresh remain available. */ });
    return () => { disposed = true; void hub.stop(); };
  }, [channel, id]);
  async function sendMessage(e: FormEvent<HTMLFormElement>) {
    e.preventDefault(); setError(''); setBusy(true);
    const form = e.currentTarget;
    try {
      await send(baseUrl, { body: new FormData(form).get('body') }, session.csrfToken);
      form.reset(); setTick(x => x + 1);
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <Panel><div className="panel-heading"><h2>{title}</h2><button className="link" onClick={() => setTick(x => x + 1)}>Aktualisieren</button></div>
    <Notice text={payload.error || error}/>
    {!payload.data && !payload.error && <p role="status">Nachrichten werden geladen …</p>}
    {payload.data && <>
      {payload.data.items.length === 0 && <p className="muted">Noch keine Nachrichten.</p>}
      <ul className="messages">{payload.data.items.map(m => <li key={m.sequence}>
        <strong>{m.author}</strong><small>{date(m.sentAt)}</small><p className="preserve">{m.body}</p>
      </li>)}</ul>
      {payload.data.next && <button onClick={() => setAfter(payload.data!.next)}>Weitere Nachrichten</button>}
      {after && <button onClick={() => setAfter(undefined)}>Zum Anfang</button>}
      <form onSubmit={sendMessage}>
        <label>Nachricht<textarea name="body" rows={2} maxLength={2000} required/></label>
        <button className="primary" disabled={busy}>Nachricht senden</button>
      </form>
    </>}
  </Panel>;
}

if (slug && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(slug))
  document.querySelector<HTMLLinkElement>('link[rel="manifest"]')!.href = api + '/manifest.webmanifest';

createRoot(document.getElementById('root')!).render(<App/>);
if ('serviceWorker' in navigator)
  void navigator.serviceWorker.register('/sw.js').catch(() => { /* Online access remains available. */ });
