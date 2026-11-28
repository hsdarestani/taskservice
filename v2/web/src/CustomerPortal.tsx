import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ArrowRight, Camera, Check, CheckCircle2, ClipboardList, Grid2X2, Home, MapPin, Search, ShieldCheck, Zap, Plug, Lightbulb, Cable, ScanLine, Network, Sun, Wrench, UserRound, X } from 'lucide-react';
import { send } from './api';
import { emptyForm } from './ServiceFormEditor';
import type { ServiceForm } from './ServiceFormEditor';
import type { Company, Session } from './api';

export type ServiceCategory = {
  id: string;
  label: string;
  title: string;
  searchTerms: string;
  group: string;
  image: string;
  featured: boolean;
  form?: ServiceForm;
  formVersion?: string;
};

const symbols: Record<string, typeof Plug> = {
  installation: Plug, fault: Zap, inspection: ScanLine, lighting: Lightbulb,
  network: Network, solar: Sun, maintenance: Wrench, wallbox: Plug,
};
function ServiceSymbol({ id }: { id: string }) {
  const Icon = symbols[id] || Cable;
  return <Icon className="electrical-symbol" aria-hidden/>;
}

export function Wordmark({ href }: { href: string }) {
  return <a className="wordmark" href={href} aria-label="TaskService – Startseite">taskservice<span>.</span></a>;
}

export function CustomerHeader({ base, company, onRequest, query, onQuery }: {
  base: string; company: Company; onRequest?: () => void; query?: string; onQuery?: (value: string) => void;
}) {
  return <>
    {onRequest && <div className="offer-strip">Elektroservice von {company.name}</div>}
    <header className="customer-header">
      <div className="customer-header-inner">
        <Wordmark href={base}/>
        <p className="customer-company"><MapPin size={17} aria-hidden/>{company.name}</p>
        {onQuery && <label className="service-search">
          <Search size={20} aria-hidden/><span className="sr-only">Service suchen</span>
          <input type="search" value={query} onChange={event => onQuery(event.target.value)}
            placeholder="Service suchen, z. B. Steckdose" aria-controls="service-results"/>
        </label>}
        <nav aria-label="Kundenmenü" className="customer-links">
          <a href={base + '#services'}>Services</a><a href={base + '/konto'}>Mein Konto</a>
          {onRequest && <button type="button" className="primary" onClick={onRequest}>Anfrage stellen</button>}
        </nav>
      </div>
    </header>
  </>;
}

export function CustomerNav({ base, account = false, allowRequest, onRequest }: {
  base: string; account?: boolean; allowRequest: boolean; onRequest?: () => void;
}) {
  return <nav className="consumer-nav" aria-label="Mobile Kundennavigation">
    <a href={base} aria-current={account ? undefined : 'page'}><Home aria-hidden/>Startseite</a>
    <a href={base + '#services'}><Grid2X2 aria-hidden/>Services</a>
    {allowRequest && (onRequest
      ? <button type="button" onClick={onRequest}><ClipboardList aria-hidden/>Neue Anfrage</button>
      : <a href={base + '?anfrage=neu#services'}><ClipboardList aria-hidden/>Neue Anfrage</a>)}
    <a href={base + '/konto'} aria-current={account ? 'page' : undefined}><UserRound aria-hidden/>Mein Konto</a>
  </nav>;
}

export function CustomerHome({ base, api, company, session, categories, catalogError }: {
  base: string; api: string; company: Company; session: Session; categories?: ServiceCategory[]; catalogError: string;
}) {
  const [query, setQuery] = useState('');
  const [request, setRequest] = useState<string | null>(
    new URLSearchParams(location.search).get('anfrage') === 'neu' ? '' : null);
  const term = query.trim().toLocaleLowerCase('de');
  const visible = (categories ?? []).filter(service =>
    `${service.label} ${service.title} ${service.searchTerms}`.toLocaleLowerCase('de').includes(term));
  const accepting = company.intakeAvailable && !!categories?.length;
  const start = () => setRequest('');
  const open = (id: string) => setRequest(id);
  function close() {
    if (new URLSearchParams(location.search).get('anfrage') === 'neu')
      history.replaceState(null, '', location.pathname + location.hash);
    setRequest(null);
  }
  return <div className="consumer">
    <CustomerHeader base={base} company={company} onRequest={accepting ? start : undefined} query={query} onQuery={setQuery}/>
    <main id="inhalt" className="consumer-main">
      <section className="discovery" aria-labelledby="services-title">
        <div className="discovery-hero">
          <div className="discovery-hero-copy">
            <h1 id="services-title">Elektroservice anfragen</h1>
            <p>Beschreibe dein Vorhaben und sende Fotos direkt an {company.name}. Termine und Rückfragen findest du in deinem Kundenkonto.</p>
            <div className="discovery-hero-actions">
              {accepting && <button className="primary" type="button" onClick={start}>Anfrage erstellen</button>}
              <a className="discovery-explore-link" href="#services"><Grid2X2 size={17} aria-hidden/> Services entdecken</a>
            </div>
            <div className="discovery-trust"><span><CheckCircle2 size={16} aria-hidden/> Unkompliziert</span>
              <span><ShieldCheck size={16} aria-hidden/> Direkt zum Servicebetrieb</span></div>
          </div>
          <div className="discovery-hero-image"><img src="/services/electric.webp" alt="" width="700" height="660"/>
            <div className="discovery-hero-image-note"><CheckCircle2 size={20} aria-hidden/>
              <span><strong>Alles an einem Ort</strong><small>Deine Anfragen jederzeit im Blick</small></span></div>
          </div>
        </div>
        <div className="service-picker" id="services">
          <div className="service-picker-header">
            <div><p className="eyebrow">UNSERE LEISTUNGEN</p>
              <h2>{term ? 'Suchergebnisse' : 'Wobei können wir helfen?'}</h2>
              <p>Wähle deinen Bereich. Die Details kannst du im nächsten Schritt angeben.</p></div>
            {term && <button className="text-button" onClick={() => setQuery('')}>Suche zurücksetzen</button>}
          </div>
          <div id="service-results">
            {!categories && !catalogError && <p role="status">Services werden geladen …</p>}
            {catalogError && <p className="notice" role="alert">{catalogError}</p>}
            {categories && <p className="sr-only" role="status">{visible.length} Services verfügbar.</p>}
            {categories && categories.length === 0 && <p>Dieser Betrieb nimmt derzeit keine Online-Anfragen an.</p>}
            {categories && categories.length > 0 && visible.length === 0 && <p>Zu „{query.trim()}“ wurde kein Service gefunden.</p>}
            <div className="service-grid">{visible.map(service =>
              <button key={service.id} className="service-tile" onClick={() => open(service.id)} type="button">
                <ServiceSymbol id={service.id}/>
                <span>{service.label}</span><ArrowRight className="service-tile-arrow" size={18} aria-hidden/>
              </button>
            )}</div>
          </div>
        </div>
      </section>
      <section className="customer-benefits" aria-label="So funktioniert deine Anfrage">
        <div><Check aria-hidden/><strong>Direkt anfragen</strong><p>{session.native ? 'Starte mit einem Demo-Kundenkonto.' : 'Starte als Gast, auch ohne Kundenkonto.'}</p></div>
        <div><Camera aria-hidden/><strong>Fotos hinzufügen</strong><p>Zeige dem Servicebetrieb, worum es geht.</p></div>
        <div><ClipboardList aria-hidden/><strong>Anfragen im Blick</strong><p>Verfolge Termine und Nachrichten im Kundenkonto.</p></div>
      </section>
    </main>
    <footer className="customer-footer"><Wordmark href={base}/><a href={base + '/konto'}>Mein Konto</a>
      {company.privacyNoticeUrl && <a href={company.privacyNoticeUrl}>Datenschutz</a>}</footer>
    <CustomerNav base={base} allowRequest={accepting} onRequest={start}/>
    {accepting && request !== null && <RequestWizard categories={categories ?? []} initial={request} api={api} base={base}
      session={session} privacyNoticeUrl={company.privacyNoticeUrl} onClose={close}/>}
  </div>;
}

const stepTitles = ['Was muss erledigt werden?', 'Erzähl uns mehr darüber', 'Wie können wir dich erreichen?'];
const mimeTypes = ['image/jpeg', 'image/png', 'image/webp'];
const fileHelp = 'Bis zu 3 Fotos im Format JPG, PNG oder WebP, je maximal 350 KB.';

function photoBody(file: File) {
  return new Promise<{ mime: string; content: string }>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve({ mime: file.type, content: String(reader.result).split(',')[1] });
    reader.onerror = () => reject(new Error('Ein Foto konnte nicht gelesen werden.'));
    reader.readAsDataURL(file);
  });
}

function RequestWizard({ categories, initial, api, session, base, privacyNoticeUrl, onClose }: {
  categories: ServiceCategory[]; initial: string; api: string; base: string; session: Session; privacyNoticeUrl?: string; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const [step, setStep] = useState(0);
  const [category, setCategory] = useState(initial);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  const [email, setEmail] = useState(session.email || '');
  const [answers, setAnswers] = useState<Record<string,string>>({});
  const service = categories.find(s => s.id === category);
  const form = service?.form ?? emptyForm;
  const fields = form.fields.filter(f => !f.whenYes || answers[f.whenYes] === 'yes');
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState('');
  const key = useRef(crypto.randomUUID());

  useEffect(() => {
    const node = dialog.current!;
    const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    node.showModal();
    return () => {
      if (node.open) node.close();
      trigger?.focus();
    };
  }, []);

  useEffect(() => {
    heading.current?.focus();
    dialog.current?.scrollTo(0, 0);
  }, [step, result]);

  function edit() {
    key.current = crypto.randomUUID();
    setError('');
  }
  function addPhotos(chosen: FileList | null) {
    const next = [...files, ...Array.from(chosen ?? [])];
    if (next.length > 3 || next.some(file => !mimeTypes.includes(file.type) || file.size < 1 || file.size > 350000)) {
      setError(fileHelp);
      return;
    }
    setFiles(next);
    edit();
  }
  async function advance(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError('');
    if (step === 0 && (!categories.some(service => service.id === category) || title.trim().length < 3)) {
      setError('Gib einen kurzen Titel ein und wähle einen Service.');
      return;
    }
    if (step === 1 && files.length < form.minimumPhotos) {
      setError('Bitte füge mindestens ' + form.minimumPhotos + ' Foto(s) hinzu.');
      return;
    }
    if (step === 1 && description.trim().length < 10) {
      setError('Beschreibe dein Anliegen mit mindestens 10 Zeichen.');
      return;
    }
    if (step < 2) {
      setStep(step + 1);
      return;
    }
    if (name.trim().length < 2 || (phone.match(/[0-9]/g)?.length ?? 0) < 5 || !/^[+0-9()/ .-]+$/.test(phone)) {
      setError('Prüfe deinen Namen und deine Telefonnummer.');
      return;
    }
    setBusy(true);
    try {
      const photos = await Promise.all(files.map(photoBody));
      const submitted = await send<{ id: string }>(api + '/requests',
        { title, category, description, name, phone, email, photos, formVersion: service?.formVersion, answers: Object.fromEntries(fields.filter(f => answers[f.key]).map(f => [f.key, answers[f.key]])) }, session.csrfToken, key.current);
      setResult(submitted.id);
    } catch (cause) {
      setError((cause as Error).message);
    } finally { setBusy(false); }
  }

  return <dialog ref={dialog} className="request-dialog" aria-labelledby="wizard-title"
    onCancel={event => { event.preventDefault(); if (!busy) onClose(); }}>
    <div className="wizard-top">
      {step > 0 && !result && <button type="button" className="text-button" disabled={busy}
        onClick={() => {setStep(step - 1); setError('');}}>Zurück</button>}
      <button type="button" className="icon-button" aria-label="Schließen" onClick={onClose} disabled={busy}><X aria-hidden/></button>
    </div>
    {session.native && !session.signedIn ? <section className="wizard-success">
      <h2 id="wizard-title" ref={heading} tabIndex={-1}>Mit einem Kundenkonto anfragen</h2>
      <p>Erstelle ein Konto mit Testdaten, um deine Anfrage später wiederzufinden.</p>
      <a className="primary" href={'/api/v1/login?returnTo=' + encodeURIComponent(base)}>Zur Anmeldung</a>
    </section> : result ? <section className="wizard-success">
      <span className="success-icon"><Check aria-hidden/></span>
      <h2 id="wizard-title" ref={heading} tabIndex={-1}>Deine Anfrage ist eingegangen.</h2>
      <p>{session.native ? 'Du findest deine Anfrage in deinem Kundenkonto.'
        : 'Bestätige deine Anfrage über den Link in deiner E-Mail. Danach findest du sie in deinem Kundenkonto.'}</p>
      <a className="primary" href={base + '/konto'}>Zu meinem Konto</a>
    </section> : <>
      <p className="wizard-position">Schritt {step + 1} von 3</p>
      <progress className="wizard-progress" value={step + 1} max={3} aria-label="Fortschritt der Anfrage"/>
      <form className="wizard-form" onSubmit={advance} onInput={edit}>
        <div className="wizard-content">
          <h2 id="wizard-title" ref={heading} tabIndex={-1}>{stepTitles[step]}</h2>
          {step === 0 && <>
            <p>Beschreibe kurz, wobei du Hilfe brauchst.</p>
            <label>Titel deiner Anfrage<input name="title" value={title} required minLength={3} maxLength={160}
              onChange={event => setTitle(event.target.value)} placeholder="z. B. Steckdose versetzen"/></label>
            <fieldset className="category-options"><legend>Welche Art von Service?</legend>
              <div>{categories.map(service => <label key={service.id}>
                <input type="radio" name="category" value={service.id} checked={category === service.id} required
                  onChange={() => { setCategory(service.id); setAnswers({}); }}/><span>{service.label}</span>
              </label>)}</div>
            </fieldset>
          </>}
          {step === 1 && <>
            <p>Was soll gemacht werden? Gibt es etwas, das der Servicebetrieb wissen sollte?</p>
            <label>Dein Anliegen<textarea name="description" value={description} required minLength={10} maxLength={4000}
              rows={5} onChange={event => setDescription(event.target.value)}/></label>
            {form.instructions && <p className="notice">{form.instructions}</p>}
            {fields.map(f => <label key={f.key}>{f.label}{f.required ? ' (Pflichtfeld)' : ''}
              {f.kind === 'yesno' ? <select value={answers[f.key] || ''} required={f.required}
                onChange={e => setAnswers({...answers, [f.key]: e.target.value})}>
                <option value="">Bitte wählen</option><option value="yes">Ja</option><option value="no">Nein</option>
              </select> : <input value={answers[f.key] || ''} required={f.required} maxLength={500}
                onChange={e => setAnswers({...answers, [f.key]: e.target.value})}/>}</label>)}
            <label className="photo-drop">
              <span className="photo-drop-icon"><Camera size={26} aria-hidden/></span>
              <strong>Fotos hinzufügen {form.minimumPhotos ? '(mindestens ' + form.minimumPhotos + ')' : '(optional)'}</strong>
              <small>{fileHelp}</small>
              <span className="photo-drop-action">Fotos auswählen</span>
              <input className="photo-drop-native" type="file" aria-label="Fotos auswählen" accept="image/jpeg,image/png,image/webp" multiple
                onChange={event => { addPhotos(event.target.files); event.target.value = ''; }}/>
            </label>
            {files.length > 0 && <ul className="file-list">{files.map((file, index) => <li key={index}>
              <span>{file.name}<small>{Math.ceil(file.size / 1024)} KB</small></span>
              <button type="button" className="text-button" onClick={() => {setFiles(files.filter((_, i) => i !== index)); edit();}}
                aria-label={`Foto ${index + 1} entfernen`}>Entfernen</button>
            </li>)}</ul>}
          </>}
          {step === 2 && <>
            <p>{session.native ? 'Verwende ausschließlich Testdaten. Die Anfrage wird deinem Kundenkonto zugeordnet.'
              : 'Du kannst die Anfrage ohne Anmeldung absenden. Zur Bestätigung erhältst du eine E-Mail.'}</p>
            <label>Vor- und Nachname<input autoComplete="name" value={name} required minLength={2} maxLength={120}
              onChange={event => setName(event.target.value)}/></label>
            <label>Telefonnummer<input autoComplete="tel" type="tel" value={phone} required maxLength={50}
              onChange={event => setPhone(event.target.value)}/></label>
            <label>E-Mail-Adresse<input autoComplete="email" type="email" value={email} required maxLength={254} readOnly={session.native}
              onChange={event => setEmail(event.target.value)}/></label>
            {privacyNoticeUrl && <p><a href={privacyNoticeUrl} target="_blank" rel="noopener noreferrer">Datenschutzhinweise lesen</a></p>}
            <label className="contact-confirmation"><input type="checkbox" required/>
              Der Servicebetrieb darf mich zu dieser Anfrage kontaktieren.
            </label>
          </>}
          {error && <p className="notice" role="alert">{error}</p>}
        </div>
        <div className="wizard-footer">
          <span>{['Service auswählen', 'Anliegen beschreiben', 'Kontaktdaten angeben'][step]}</span>
          <button className="primary" disabled={busy || (step === 0 && !categories.length)} type="submit">
            {busy ? 'Wird gesendet …' : step === 2 ? 'Anfrage absenden' : 'Weiter'}
          </button>
        </div>
      </form>
    </>}
  </dialog>;
}
