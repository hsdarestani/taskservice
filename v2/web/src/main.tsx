import { useEffect, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { get, send } from './api';
import type { Company, Detail, Message, RequestRow, Session } from './api';
import './styles.css';

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
};
const date = (value: string) => new Intl.DateTimeFormat('de-DE', {
  dateStyle: 'medium', timeStyle: 'short', timeZone: 'Europe/Berlin',
}).format(new Date(value));
const path = location.pathname.split('/').filter(Boolean);
const slug = path[0] === 'unternehmen' ? path[1] : undefined;
const base = slug ? '/unternehmen/' + encodeURIComponent(slug) : '';
const api = slug ? '/api/v1/tenants/' + encodeURIComponent(slug) : '';
const login = '/api/v1/login?returnTo=' + encodeURIComponent(location.pathname);
const requestId = path[2] === 'anfragen' ? path[3] : undefined;

function useLoad<T>(url: string | undefined, revision = 0) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    setData(undefined);
    setError('');
    if (url) get<T>(url).then(x => { if (active) setData(x); })
      .catch(x => { if (active) setError(x.message); });
    return () => { active = false; };
  }, [url, revision]);
  return { data, error };
}

function Notice({ text }: { text: string }) {
  return text ? <p className="notice" role="alert">{text}</p> : null;
}
function Panel({ children }: { children: ReactNode }) {
  return <section className="panel">{children}</section>;
}
function Badge({ state }: { state: string }) {
  return <span className={'badge ' + state}>{states[state] || state}</span>;
}

function App() {
  const session = useLoad<Session>('/api/v1/session');
  const company = useLoad<Company>(slug ? api : undefined, session.data?.signedIn ? 1 : 0);
  const isStaff = company.data?.role === 'admin' || company.data?.role === 'dispatcher';
  useEffect(() => {
    if (company.data?.accent && /^#[a-f0-9]{6}$/i.test(company.data.accent))
      document.documentElement.style.setProperty('--brand-accent', company.data.accent);
  }, [company.data?.accent]);
  const portal = path[2] === 'betrieb' ? 'Betrieb' : path[2] === 'technik' ? 'Technik' : 'Kundenportal';

  return <>
    <a className="skip" href="#inhalt">Zum Inhalt</a>
    <header className="bar">
      <a href={base || '/'} className="brand"><span aria-hidden="true">TS</span>{company.data?.name || 'TaskService'}</a>
      <nav aria-label="Hauptnavigation">
        {slug && <a href={base}>Kundenportal</a>}
        {isStaff && <a href={base + '/betrieb'}>Betrieb</a>}
        {company.data?.role === 'technician' && <a href={base + '/technik'}>Technik</a>}
        {session.data?.signedIn
          ? <button className="link" onClick={async () => {
            try { await send('/api/v1/logout', {}, session.data!.csrfToken); location.assign(base || '/'); }
            catch { location.reload(); }
          }}>Abmelden</button>
          : <a href={login}>Anmelden</a>}
      </nav>
    </header>
    <main id="inhalt">
      <Notice text={session.error || company.error}/>
      {!session.data && !session.error && <p role="status">Sitzung wird geladen …</p>}
      {!slug && path[0] === 'plattform' && <Platform/>}
      {!slug && path[0] !== 'plattform' && <Panel>
        <p className="eyebrow">Serviceportal</p>
        <h1>Ihr Kontakt zum Servicebetrieb</h1>
        <p>Öffnen Sie das Portal Ihres Servicebetriebs über dessen persönlichen Link.</p>
      </Panel>}
      {company.data && session.data && <>
        {requestId && path[4] === 'bestaetigen'
          ? <ClaimRequest session={session.data} id={requestId}/>
          : requestId
            ? <RequestDetail session={session.data} id={requestId} company={company.data}/>
            : <Dashboard company={company.data} session={session.data} portal={portal}/>}
      </>}
    </main>
  </>;
}

function Platform() {
  const catalog = useLoad<{ slug: string; name: string; active: boolean }[]>('/api/v1/platform/overview');
  return <Panel><h1>Plattformverwaltung</h1><Notice text={catalog.error}/>
    {!catalog.data && !catalog.error && <p role="status">Unternehmen werden geladen …</p>}
    {catalog.data && <ul className="rows">{catalog.data.map(x => <li key={x.slug}>
      <strong>{x.name}</strong><span>{x.active ? 'Aktiv' : 'Inaktiv'}</span>
    </li>)}</ul>}
  </Panel>;
}

function Dashboard({ company, session, portal }: { company: Company; session: Session; portal: string }) {
  const authorized = portal === 'Kundenportal' || (portal === 'Technik' ? company.role === 'technician' :
    company.role === 'admin' || company.role === 'dispatcher');
  const [cursor, setCursor] = useState('');
  const list = useLoad<{ items: RequestRow[]; nextCursor?: string }>(
    session.signedIn && authorized ? api + '/requests?view=' + (portal === 'Kundenportal' ? 'mine' : 'staff') + (cursor ? '&cursor=' + cursor : '') : undefined);
  if (!authorized) return <Panel><h1>{portal}</h1><Notice text="Ihr Konto hat keinen Zugriff auf diesen Bereich."/></Panel>;

  return <>
    <div className="hero"><p className="eyebrow">{company.name} · {portal}</p>
      <h1>{portal === 'Betrieb' ? 'Servicevorgänge' : portal === 'Technik' ? 'Meine Einsätze' : 'Ihr Servicekontakt'}</h1>
      <p>{portal === 'Kundenportal' ? 'Anfragen stellen und den Stand Ihrer Vorgänge verfolgen.' :
        'Aufträge, Termine und Nachrichten im Überblick.'}</p></div>
    {portal === 'Kundenportal' && <NewRequest session={session}/>}
    {session.signedIn && <Panel>
      <h2>{portal === 'Kundenportal' ? 'Meine Anfragen' : 'Vorgänge und Einsätze'}</h2><Notice text={list.error}/>
      {!list.data && !list.error && <p role="status">Vorgänge werden geladen …</p>}
      {list.data?.items.length === 0 && <p className="muted">Hier gibt es noch keine Vorgänge.</p>}
      {list.data && <ul className="rows">{list.data.items.map(row => <li key={row.id}>
        <a href={base + '/anfragen/' + row.id}><strong>{row.title}</strong><small>{date(row.createdAt)}</small></a>
        <Badge state={row.state}/>
      </li>)}</ul>}
      {list.data?.nextCursor && <button onClick={() => setCursor(list.data!.nextCursor!)}>Weitere Vorgänge</button>}
      {cursor && <button onClick={() => setCursor('')}>Zur ersten Seite</button>}
    </Panel>}
    {portal !== 'Kundenportal' && session.signedIn && <Chat session={session} channel="company" title="Unternehmenschat"/>}
  </>;
}

function NewRequest({ session }: { session: Session }) {
  const [status, setStatus] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [key, setKey] = useState(() => crypto.randomUUID());
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget;
    setError(''); setStatus(''); setBusy(true);
    try {
      await send(api + '/requests', Object.fromEntries(new FormData(form)), session.csrfToken, key);
      setStatus('Ihre Anfrage ist eingegangen. Bitte bestätigen Sie sie über den Link in Ihrer E-Mail.');
      form.reset();
      setKey(crypto.randomUUID());
    } catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <Panel><h2>Neue Serviceanfrage</h2>
    <form onSubmit={submit} onInput={() => setKey(crypto.randomUUID())}>
      <label>Betreff<input name="title" maxLength={160} required/></label>
      <label>E-Mail-Adresse<input name="email" type="email" maxLength={254} required/></label>
      <label>Beschreibung<textarea name="description" rows={4} maxLength={4000} required/></label>
      <button className="primary" disabled={busy}>{busy ? 'Wird gesendet …' : 'Anfrage absenden'}</button>
      <Notice text={error}/><p role="status">{status}</p>
    </form>
  </Panel>;
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

function RequestDetail({ session, id, company }: { session: Session; id: string; company: Company }) {
  const [revision, setRevision] = useState(0);
  const detail = useLoad<Detail>(session.signedIn ? api + '/requests/' + id : undefined, revision);
  const row = detail.data;
  if (!session.signedIn) return <Panel><h1>Servicevorgang</h1><a href={login}>Anmelden</a></Panel>;
  if (!row) return <Panel>{detail.error ? <Notice text={detail.error}/> : <p role="status">Vorgang wird geladen …</p>}</Panel>;
  return <>
    <div className="hero"><a href={base}>Übersicht</a><h1>{row.title}</h1><Badge state={row.state}/></div>
    <div className="columns">
      <Panel><h2>Beschreibung</h2><p className="preserve">{row.description}</p>
        {row.appointment && <><h2>Einsatztermin</h2>
          <p>{date(row.appointment.startsAt)} bis {date(row.appointment.endsAt)} Uhr (Berlin)</p>
          <Badge state={row.appointment.arrivalStatus}/></>}
      </Panel>
      <Panel><h2>Verlauf</h2><ol className="history">{row.history.map((x, i) =>
        <li key={i}><strong>{states[x.state] || x.state}</strong><small>{date(x.at)}</small></li>)}</ol></Panel>
    </div>
    {row.actions.length > 0 && <Commands row={row} session={session} refresh={() => setRevision(x => x + 1)}/>}
    <Chat session={session} id={id} channel="customer" title="Nachrichten zur Anfrage"/>
    {company.role && <Chat session={session} id={id} channel="internal" title="Interne Notizen"/>}
  </>;
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
        body.startsAt = new Date(String(data.get('startsAt'))).toISOString();
        body.endsAt = new Date(String(data.get('endsAt'))).toISOString();
      }
      await send(api + '/requests/' + row.id + '/commands', body, session.csrfToken, crypto.randomUUID(), row.revision); refresh(); }
    catch (x) { setError((x as Error).message); }
    finally { setBusy(false); }
  }
  return <Panel><h2>Vorgang bearbeiten</h2><form onSubmit={submit}>
    <label>Aktion<select value={action} onChange={e => setAction(e.target.value)}>
      {row.actions.map(key => <option key={key} value={key}>{actions[key]}</option>)}
    </select></label>
    {action === 'assign' && <label>Fachkraft<select name="technicianId" required>
      <option value="">Bitte wählen</option>
      {technicians.data?.map(x => <option key={x.id} value={x.id}>{x.name}</option>)}
    </select></label>}
    {action === 'schedule' && <div className="columns">
      <label>Beginn (Gerätezeit)<input name="startsAt" type="datetime-local" required/></label>
      <label>Ende (Gerätezeit)<input name="endsAt" type="datetime-local" required/></label>
    </div>}
    {action === 'arrival' && <label>Anfahrt<select name="arrivalStatus">
      <option value="departed">Unterwegs</option><option value="delayed">Verspätet</option><option value="arrived">Vor Ort</option>
    </select></label>}
    <button className="primary" disabled={busy}>Änderung speichern</button><Notice text={error || technicians.error}/>
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
      .withUrl('/hubs/conversations', { skipNegotiation: true, transport: HttpTransportType.WebSockets })
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

createRoot(document.getElementById('root')!).render(<App/>);
