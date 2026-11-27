import { useState } from 'react';
import { Search } from 'lucide-react';
import { ApiError, send } from './api';
import { useLoad } from './useLoad';

type State = 'default' | 'enabled' | 'disabled';
type Feature = { key: string; label: string; effective: boolean; defaultEnabled: boolean; globallyDisabled: boolean; unlicensed: boolean; parent?: string; state: State };
type Registry = { revision: number; items: Feature[] };

export function CompanyFeatures({ companyPath, revision, csrfToken, onChange }: {
  companyPath: string; revision: number; csrfToken: string; onChange: () => void;
}) {
  const [query, setQuery] = useState('');
  const [saving, setSaving] = useState('');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const registry = useLoad<Registry>(companyPath + '/features', revision);
  const term = query.trim().toLocaleLowerCase('de');
  const items = registry.data?.items.filter(item => item.label.toLocaleLowerCase('de').includes(term));
  async function save(feature: Feature, state: State) {
    if (!registry.data || saving) return;
    setSaving(feature.key);
    setError('');
    setMessage('');
    try {
      await send(companyPath + '/commands', { action: 'feature', feature: { key: feature.key, state } },
        csrfToken, crypto.randomUUID(), registry.data.revision);
      setMessage(feature.label + ' wurde aktualisiert.');
      onChange();
    } catch (cause) {
      setError((cause as Error).message);
      if (cause instanceof ApiError && cause.status === 412) onChange();
    } finally { setSaving(''); }
  }

  return <section className="feature-settings">
    <h3>Funktionen und Leistungen</h3>
    <label className="feature-search"><Search size={17} aria-hidden/>
      <input type="search" aria-label="Funktion suchen" placeholder="Funktion suchen" value={query}
        onChange={event => setQuery(event.target.value)}/></label>
    {(registry.error || error) && <p role="alert" className="notice">{error || registry.error}</p>}
    <p role="status">{saving ? 'Wird gespeichert …' : message || (!registry.data && !registry.error ? 'Einstellungen werden geladen …' : '')}</p>
    {items?.length === 0 && <p>Keine passende Funktion gefunden.</p>}
    {items?.map(item => <div className="feature-setting" key={item.key}>
      <div><strong>{item.label}</strong><small>{item.effective ? 'Aktiv' :
        item.globallyDisabled ? 'Plattformweit gesperrt' : item.unlicensed ? 'Im Tarif ausgeschlossen' :
        item.parent && !registry.data?.items.find(other => other.key === item.parent)?.effective
          ? 'Gesperrt durch Kundenanfragen' : 'Inaktiv'}</small></div>
      <select aria-label={item.label + ' konfigurieren'} value={item.state} disabled={!!saving}
        onChange={event => void save(item, event.target.value as State)}>
        <option value="default">Standard ({item.defaultEnabled ? 'aktiv' : 'inaktiv'})</option>
        <option value="enabled">Aktivieren</option><option value="disabled">Deaktivieren</option>
      </select>
    </div>)}
  </section>;
}
