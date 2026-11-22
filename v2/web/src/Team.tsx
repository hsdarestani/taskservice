import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { MailPlus, ShieldCheck } from 'lucide-react';
import { send } from './api';
import type { Session } from './api';
import { useLoad } from './useLoad';

const roleNames: Record<string, string> = { admin: 'Unternehmensverwaltung', dispatcher: 'Disposition', technician: 'Technik' };
const rights: Record<string, string> = {
  'report.create': 'Arbeitsbericht erstellen', 'report.approve': 'Arbeitsbericht prüfen',
  'report.publish': 'Arbeitsbericht veröffentlichen', 'invoice.upload': 'Rechnung hochladen',
  'invoice.read': 'Rechnung lesen', 'invoice.approve': 'Rechnung prüfen', 'invoice.publish': 'Rechnung veröffentlichen',
};

type Member = { id: string; name: string; role: string; active: boolean; revision: number; permissions: string[]; allowedPermissions: string[] };
type Invite = { id: string; name: string; role: string; email: string; expiresAt: string };

export function Team({ api, session }: { api: string; session: Session }) {
  const [revision, refresh] = useState(0);
  const [cursor, setCursor] = useState<string>();
  const endpoint = api + '/team';
  const team = useLoad<{ members: Member[]; invitations: Invite[]; nextCursor?: string }>(
    endpoint + (cursor ? '?cursor=' + encodeURIComponent(cursor) : ''), revision);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const inviteKey = useRef(crypto.randomUUID());

  async function invite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    setBusy(true); setError(''); setNotice('');
    try {
      await send(endpoint + '/invitations', Object.fromEntries(new FormData(form)), session.csrfToken, inviteKey.current);
      form.reset();
      inviteKey.current = crypto.randomUUID();
      setNotice('Einladung zur Zustellung vorgemerkt.');
      refresh(x => x + 1);
    } catch (cause) { setError((cause as Error).message); }
    finally { setBusy(false); }
  }
  async function revoke(id: string) {
    setBusy(true); setError(''); setNotice('');
    try {
      await send(endpoint + '/invitations/' + id + '/revoke', {}, session.csrfToken);
      setNotice('Einladung widerrufen.');
      refresh(x => x + 1);
    } catch (cause) { setError((cause as Error).message); }
    finally { setBusy(false); }
  }

  return <section id="team" className="panel team-panel">
    <div className="section-line"><h2>Team und Berechtigungen</h2></div>
    {error || team.error ? <p className="notice" role="alert">{error || team.error}</p> : null}
    {notice && <p role="status">{notice}</p>}
    {!team.data && !team.error && <p role="status">Team wird geladen …</p>}
    {!!team.data && <div className="team-grid">
      <div><h3>Mitglieder</h3><div className="report-history">
        {team.data.members.map(member => <MemberEditor key={member.id + ':' + member.revision} member={member}
          endpoint={endpoint} session={session} onSave={() => refresh(x => x + 1)}/>)}
      </div>
      {team.data.nextCursor && <button onClick={() => setCursor(team.data!.nextCursor)}>Weitere Mitglieder</button>}
      {cursor && <button onClick={() => setCursor(undefined)}>Zur ersten Seite</button>}
      </div>
      <div>
        <h3><MailPlus size={19} aria-hidden/> Mitarbeitende einladen</h3>
        <form onSubmit={invite} onInput={() => {inviteKey.current = crypto.randomUUID();}}>
          <label>Name<input name="name" required maxLength={120}/></label>
          <label>Geschäftliche E-Mail-Adresse<input type="email" name="email" required maxLength={254}/></label>
          <label>Rolle<select name="role" defaultValue="technician"><option value="technician">Technik</option>
            <option value="dispatcher">Disposition</option></select></label>
          <button className="primary" type="submit" disabled={busy}>Einladung erstellen</button>
        </form>
        {team.data.invitations.length > 0 && <><h3>Offene Einladungen</h3><ul className="rows">
          {team.data.invitations.map(x => <li key={x.id}><span><strong>{x.name}</strong><small>{x.email} · {roleNames[x.role]}</small></span>
            <button type="button" className="text-button" disabled={busy} onClick={() => void revoke(x.id)}>Widerrufen</button></li>)}
        </ul></>}
      </div>
    </div>}
  </section>;
}

function MemberEditor({ member, endpoint, session, onSave }: { member: Member; endpoint: string; session: Session; onSave: () => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const key = useRef(crypto.randomUUID());
  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(''); setBusy(true);
    const form = new FormData(event.currentTarget);
    try {
      await send(endpoint + '/members/' + member.id + '/commands', {
        active: member.role === 'admin' || form.has('active'),
        permissions: form.getAll('permission').map(String)
      }, session.csrfToken, key.current, member.revision);
      onSave();
    } catch (cause) { setError((cause as Error).message); }
    finally { setBusy(false); }
  }
  return <details><summary>{member.name} · {roleNames[member.role]}{member.active ? '' : ' · Gesperrt'}</summary>
    <form onSubmit={save} onInput={() => {key.current = crypto.randomUUID();}}>
      {member.role !== 'admin' && <label className="choice-line"><input type="checkbox" name="active" defaultChecked={member.active}/> Mitgliedschaft aktiv</label>}
      <fieldset className="role-permissions"><legend>Freigaben</legend>
        {member.allowedPermissions.map(permission => <label className="choice-line" key={permission}>
          <input type="checkbox" name="permission" value={permission} defaultChecked={member.permissions.includes(permission)}/>
          {rights[permission]}
        </label>)}
      </fieldset>
      {error && <p role="alert" className="notice">{error}</p>}
      <button className="primary" disabled={busy}>Berechtigungen speichern</button>
    </form>
  </details>;
}

export function InitialAdminSetup({ companyUrl, session, onChange }: { companyUrl: string; session: Session; onChange: () => void }) {
  const [show, setShow] = useState(false);
  const status = useLoad<{ state: 'active' | 'pending' | 'missing' | 'inactive' }>(show ? companyUrl + '/setup' : undefined);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const key = useRef(crypto.randomUUID());
  async function sendInvite(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(''); setBusy(true);
    try {
      await send(companyUrl + '/setup', Object.fromEntries(new FormData(event.currentTarget)), session.csrfToken, key.current);
      onChange();
    } catch (cause) { setError((cause as Error).message); }
    finally { setBusy(false); }
  }
  return <div className="initial-admin">
    <button className="text-button" type="button" aria-expanded={show} onClick={() => setShow(!show)}>
      {show ? 'Ersteinrichtung ausblenden' : 'Ersteinrichtung anzeigen'}
    </button>
    {show && <>
      {status.error && <p className="notice" role="alert">{status.error}</p>}
      {!status.data && !status.error && <p role="status">Ersteinrichtung wird geprüft …</p>}
      {status.data?.state === 'active' && <p>Eine Unternehmensverwaltung ist aktiv.</p>}
      {status.data?.state === 'pending' && <p>Die erste Einladung wartet auf Annahme.</p>}
      {status.data?.state === 'inactive' && <p>Für die Ersteinrichtung muss das Portal aktiv sein.</p>}
      {status.data?.state === 'missing' && <form onSubmit={sendInvite} onInput={() => {key.current = crypto.randomUUID();}}>
        <h3>Erste Administration einladen</h3>
        <label>Name der Administration<input name="name" required maxLength={120}/></label>
        <label>E-Mail-Adresse der Administration<input name="email" type="email" required maxLength={254}/></label>
        <button className="primary" disabled={busy}>Erste Einladung erstellen</button>
      </form>}
      {error && <p className="notice" role="alert">{error}</p>}
    </>}
  </div>;
}

export function AcceptInvitation({ endpoint, base, login, session, id }: {
  endpoint: string; base: string; login: string; session: Session; id: string;
}) {
  const cacheKey = 'taskservice-invitation:' + base + ':' + id;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    if (/^#[0-9a-f]{64}$/i.test(location.hash)) {
      sessionStorage.setItem(cacheKey, location.hash.slice(1));
      history.replaceState(null, '', location.pathname);
    }
  }, [cacheKey]);
  async function accept() {
    setError(''); setBusy(true);
    try {
      const member = await send<{ role: string }>(endpoint + '/team/invitations/' + encodeURIComponent(id) + '/accept',
        { token: sessionStorage.getItem(cacheKey) || '' }, session.csrfToken);
      sessionStorage.removeItem(cacheKey);
      location.replace(base + (member.role === 'technician' ? '/technik' : '/betrieb'));
    } catch (cause) { setError((cause as Error).message); }
    finally { setBusy(false); }
  }
  return <section className="panel"><div className="account-empty"><ShieldCheck aria-hidden/>
    <h1>Einladung zum Arbeitsbereich</h1><p>Melden Sie sich mit dem eingeladenen Konto an und bestätigen Sie die Einladung.</p>
    {session.signedIn ? <button className="primary" type="button" onClick={accept} disabled={busy}>Einladung annehmen</button>
      : <a className="primary" href={login}>Mit Arbeitskonto anmelden</a>}
    {error && <p className="notice" role="alert">{error}</p>}
  </div></section>;
}
