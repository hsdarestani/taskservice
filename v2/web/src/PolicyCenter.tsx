import { useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError, send } from './api';
import { useLoad } from './useLoad';

export const plans: Record<string, string> = {
  essential: 'Essential', professional: 'Professional', enterprise: 'Enterprise', custom: 'Individuell',
};
type Snapshot = {
  revision: number; withheld: string[]; features: { key: string; label: string }[];
  history: { revision: number; recordedAt: string; withheld: string[] }[];
};
export function PolicyCenter({ csrfToken }: { csrfToken: string }) {
  const [scope, setScope] = useState('global');
  return <section className="panel">
    <label>Geltungsbereich<select value={scope} onChange={e => setScope(e.target.value)}>
      <option value="global">Gesamte Plattform</option>
      {Object.entries(plans).map(([code, name]) => <option key={code} value={code}>Tarif · {name}</option>)}
    </select></label>
    <PolicyScope key={scope} scope={scope} csrfToken={csrfToken}/>
  </section>;
}
function PolicyScope({ scope, csrfToken }: { scope: string; csrfToken: string }) {
  const path = '/api/v1/platform/policies/' + scope;
  const [refresh, setRefresh] = useState(0);
  const snapshot = useLoad<Snapshot>(path, refresh);
  const [draft, setDraft] = useState<string[]>();
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState('');
  const [error, setError] = useState('');
  const withheld = draft ?? snapshot.data?.withheld ?? [];
  const visible = snapshot.data?.features.filter(f => f.label.toLocaleLowerCase('de').includes(query.trim().toLocaleLowerCase('de'))) ?? [];
  function change(keys: string[], block: boolean) {
    setResult('');
    setDraft(block ? [...new Set([...withheld, ...keys])] : withheld.filter(key => !keys.includes(key)));
  }
  async function save(event: FormEvent) {
    event.preventDefault();
    if (!snapshot.data || !draft || busy) return;
    setBusy(true); setError('');
    try {
      await send(path, { withheld: draft }, csrfToken, crypto.randomUUID(), snapshot.data.revision);
      setResult('Konfiguration gespeichert.');
      setDraft(undefined); setRefresh(x => x + 1);
    } catch (cause) {
      setError((cause as Error).message);
      if (cause instanceof ApiError && cause.status === 412) { setDraft(undefined); setRefresh(x => x + 1); }
    } finally { setBusy(false); }
  }
  return <form className="policy-center" onSubmit={save}>
    <h2>{scope === 'global' ? 'Plattformweite Sperren' : 'Leistungsumfang des Tarifs'}</h2>
    <p>{scope === 'global' ? 'Gesperrte Funktionen sind für alle Unternehmen sofort nach dem Speichern inaktiv.'
      : 'Ausgeschlossene Funktionen sind für Unternehmen mit diesem Tarif gesperrt.'}</p>
    {(error || snapshot.error) && <p role="alert">{error || snapshot.error}</p>}
    {!snapshot.data && !snapshot.error && <p role="status">Konfiguration wird geladen …</p>}
    {snapshot.data && <>
      <label>Funktionen durchsuchen<input type="search" value={query} onChange={e => setQuery(e.target.value)}/></label>
      <fieldset disabled={busy}><legend>{scope === 'global' ? 'Gesperrte Funktionen' : 'Ausgeschlossene Funktionen'}</legend>
        <div className="policy-bulk">
          <button type="button" disabled={!visible.length} onClick={() => change(visible.map(f => f.key), true)}>Treffer auswählen</button>
          <button type="button" disabled={!visible.length} onClick={() => change(visible.map(f => f.key), false)}>Auswahl der Treffer aufheben</button>
        </div>
        {visible.map(f => <label className="policy-choice" key={f.key}>
          <input type="checkbox" checked={withheld.includes(f.key)} onChange={e => change([f.key], e.target.checked)}/>{f.label}
        </label>)}
        {!visible.length && <p>Keine passende Funktion gefunden.</p>}
      </fieldset>
      <p>{withheld.length} von {snapshot.data.features.length} Funktionen ausgewählt</p>
      <button type="submit" className="primary" disabled={!draft || busy}>{busy ? 'Wird gespeichert …' : 'Konfiguration speichern'}</button>
      <details><summary>Änderungsverlauf</summary>
        {snapshot.data.history.length === 0 && <p>Noch keine Änderungen.</p>}
        {snapshot.data.history.map(change => <p key={change.revision}>
          <strong>Version {change.revision}</strong> · {new Date(change.recordedAt).toLocaleString('de-DE', {timeZone:'Europe/Berlin'})}<br/>
          {change.withheld.map(key => snapshot.data!.features.find(f => f.key === key)?.label ?? key).join(', ') || 'Keine Sperren'}
        </p>)}
      </details>
    </>}
    <p role="status">{result}</p>
  </form>;
}
