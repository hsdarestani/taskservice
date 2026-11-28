import { useState } from 'react';
import { CalendarDays } from 'lucide-react';
import { useLoad } from './useLoad';

type Entry = { id: string; requestId: string; title: string; startsAt: string; endsAt: string; technician: string; arrivalStatus: string };
type Page = { first: string; until: string; items: Entry[]; next?: {after: string; cursor: string} };
const day = new Intl.DateTimeFormat('sv-SE', { timeZone: 'Europe/Berlin' });
const stamp = new Intl.DateTimeFormat('de-DE', { timeZone: 'Europe/Berlin', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
const arrival: Record<string, string> = { planned: 'Geplant', departed: 'Unterwegs', delayed: 'Verspätet', arrived: 'Vor Ort' };

export function DispatchCalendar({ api, detailBase, technician }: { api: string; detailBase: string; technician: boolean }) {
  const [date, setDate] = useState(day.format(new Date()));
  const [view, setView] = useState('day');
  const [cursor, setCursor] = useState('');
  const [reload, setReload] = useState(0);
  const page = useLoad<Page>(date ? api + '/dispatch?' + new URLSearchParams({date, view}) + cursor : undefined, reload);
  return <section className="panel dispatch-calendar">
    <div className="section-line"><h2><CalendarDays size={20} aria-hidden/> {technician ? 'Meine Termine' : 'Einsatzkalender'}</h2>
      <button onClick={() => setReload(r => r + 1)}>Aktualisieren</button></div>
    <div className="dispatch-controls">
      <label>Zeitraum<select value={view} onChange={e => { setView(e.target.value); setCursor(''); }}>
        <option value="day">Tag</option><option value="week">Woche</option><option value="month">Monat</option>
      </select></label>
      <label>Datum<input type="date" min="1900-01-01" max="2100-12-31" value={date}
        onChange={e => { setDate(e.target.value); setCursor(''); }}/></label>
    </div>
    {!date && <p>Bitte wählen Sie ein Datum.</p>}
    {date && !page.data && !page.error && <p role="status">Termine werden geladen …</p>}
    {page.error && <p className="notice" role="alert">{page.error}</p>}
    {page.data && !page.data.items.length && <p>Keine Termine in diesem Zeitraum.</p>}
    {!!page.data?.items.length && <ul>{page.data.items.map(entry => <li key={entry.id}>
      <div><time dateTime={entry.startsAt}>{stamp.format(new Date(entry.startsAt))}</time> – <time dateTime={entry.endsAt}>{stamp.format(new Date(entry.endsAt))}</time></div>
      <a href={detailBase + entry.requestId}>{entry.title}</a>
      <small>{entry.technician} · {arrival[entry.arrivalStatus] ?? entry.arrivalStatus}</small>
    </li>)}</ul>}
    {page.data?.next && <button onClick={() => setCursor('&' + new URLSearchParams(page.data!.next))}>Weitere Termine</button>}
    {cursor && <button onClick={() => setCursor('')}>Zur ersten Seite</button>}
  </section>;
}
