import { useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError, send } from './api';

export type IntakeField = { key: string; label: string; kind: 'text' | 'yesno'; required: boolean; whenYes: string | null };
export type ServiceForm = { instructions: string; minimumPhotos: number; fields: IntakeField[] };
export const emptyForm: ServiceForm = { instructions: '', minimumPhotos: 0, fields: [] };

export function ServiceFormEditor({ service, label, initial, companyPath, revision, csrfToken, refresh }: {
  service: string; label: string; initial: ServiceForm; companyPath: string; revision: number; csrfToken: string; refresh: () => void;
}) {
  const [form, setForm] = useState(initial);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  function updateFields(fields: IntakeField[]) {
    const switches = new Set<string>();
    const normalized = fields.map(field => {
      const row = field.whenYes && !switches.has(field.whenYes) ? { ...field, whenYes: null } : field;
      if (row.kind === 'yesno' && !row.whenYes) switches.add(row.key);
      return row;
    });
    setForm({ ...form, fields: normalized });
  }
  function patch(key: string, values: Partial<IntakeField>) {
    updateFields(form.fields.map(f => f.key === key ? { ...f, ...values } : f));
  }
  async function save(e: FormEvent) {
    e.preventDefault(); setError(''); setBusy(true);
    try {
      await send(companyPath + '/commands', { action: 'form', form: {service, form} }, csrfToken, crypto.randomUUID(), revision);
      refresh();
    } catch (cause) {
      setError((cause as Error).message);
      if (cause instanceof ApiError && cause.status === 412) refresh();
    } finally { setBusy(false); }
  }
  return <form className="service-form-editor" onSubmit={save}>
    <h4>Anfrageformular · {label}</h4>
    <label>Hinweis für Kunden<textarea value={form.instructions} maxLength={500} rows={2}
      onChange={e => setForm({...form, instructions: e.target.value})}/></label>
    <label>Mindestanzahl Fotos<select value={form.minimumPhotos} onChange={e => setForm({...form, minimumPhotos: Number(e.target.value)})}>
      {[0,1,2,3].map(n => <option key={n} value={n}>{n}</option>)}</select></label>
    {form.fields.map((f, i) => <fieldset key={f.key}>
      <legend>Feld {i + 1}</legend>
      <label>Frage<input value={f.label} required maxLength={100} onChange={e => patch(f.key, {label: e.target.value})}/></label>
      <label>Antwortformat<select value={f.kind} onChange={e => patch(f.key, {kind: e.target.value as IntakeField['kind']})}>
        <option value="text">Kurztext</option><option value="yesno">Ja oder Nein</option></select></label>
      <label>Anzeigen<select value={f.whenYes ?? ''} onChange={e => patch(f.key, {whenYes: e.target.value || null})}>
        <option value="">Immer</option>{form.fields.slice(0, i).filter(previous => previous.kind === 'yesno' && !previous.whenYes)
          .map(previous => <option key={previous.key} value={previous.key}>Bei Ja · {previous.label}</option>)}</select></label>
      <label><input type="checkbox" checked={f.required} onChange={e => patch(f.key, {required: e.target.checked})}/> Pflichtfeld</label>
      <button type="button" onClick={() => updateFields(form.fields.filter(row => row.key !== f.key))}>Feld entfernen</button>
    </fieldset>)}
    <div className="policy-bulk">
      <button type="button" disabled={form.fields.length >= 8 || busy} onClick={() => updateFields([...form.fields,
        {key: crypto.randomUUID(), label: '', kind: 'text', required: false, whenYes: null}])}>Feld hinzufügen</button>
      <button className="primary" disabled={busy}>{busy ? 'Wird gespeichert …' : 'Formular speichern'}</button>
    </div>
    {error && <p role="alert">{error}</p>}
  </form>;
}
