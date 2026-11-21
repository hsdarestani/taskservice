import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Camera, Check, ClipboardList, Grid2X2, Home, MapPin, Search, UserRound, X } from 'lucide-react';
import { send } from './api';
import type { Company, Session } from './api';

export type ServiceCategory = {
  id: string;
  label: string;
  title: string;
  searchTerms: string;
  group: string;
  image: string;
  featured: boolean;
};

export function Wordmark({ href }: { href: string }) {
  return <a className="wordmark" href={href} aria-label="TaskService – Startseite">taskservice<span>.</span></a>;
}

export function CustomerHeader({ base, company, onRequest, query, onQuery }: {
  base: string; company: Company; onRequest?: () => void; query?: string; onQuery?: (value: string) => void;
}) {
  return <>
    {onRequest && <div className="offer-strip">Dein Anliegen. Unser Service. Einfach und direkt.</div>}
    <header className="customer-header">
      <div className="customer-header-inner">
        <Wordmark href={base}/>
        <p className="customer-company"><MapPin size={17} aria-hidden/>{company.name}</p>
        {onQuery && <label className="service-search">
          <Search size={20} aria-hidden/><span className="sr-only">Service suchen</span>
          <input type="search" value={query} onChange={event => onQuery(event.target.value)}
            placeholder="Was suchst du? Z. B. Reinigung" aria-controls="service-results"/>
        </label>}
        <nav aria-label="Kundenmenü" className="customer-links">
          <a href={base + '#services'}>Services</a><a href={base + '/konto'}>Mein Konto</a>
          {onRequest && <button type="button" className="primary" onClick={onRequest}>Anfrage stellen</button>}
        </nav>
      </div>
    </header>
  </>;
}

export function CustomerNav({ base, account = false, onRequest }: {
  base: string; account?: boolean; onRequest?: () => void;
}) {
  return <nav className="consumer-nav" aria-label="Mobile Kundennavigation">
    <a href={base} aria-current={account ? undefined : 'page'}><Home aria-hidden/>Startseite</a>
    <a href={base + '#services'}><Grid2X2 aria-hidden/>Services</a>
    {onRequest
      ? <button type="button" onClick={onRequest}><ClipboardList aria-hidden/>Neue Anfrage</button>
      : <a href={base + '?anfrage=neu#services'}><ClipboardList aria-hidden/>Neue Anfrage</a>}
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
  const featured = visible.filter(service => service.featured)
    .sort((a, b) => Number(b.id === "cleaning") - Number(a.id === "cleaning"));
  const start = () => setRequest('');
  const open = (id: string) => setRequest(id);
  function close() {
    if (new URLSearchParams(location.search).get('anfrage') === 'neu')
      history.replaceState(null, '', location.pathname + location.hash);
    setRequest(null);
  }
  return <div className="consumer">
    <CustomerHeader base={base} company={company} onRequest={start} query={query} onQuery={setQuery}/>
    <main id="inhalt" className="consumer-main">
      <div className="discovery">
        <section id="services" className="discovery-services" aria-labelledby="services-title">
          <h1 id="services-title">Services für dein Zuhause</h1>
          <div className="service-picker" id="service-results">
            <div className="section-line">
              <h2>{term ? 'Suchergebnisse' : 'Was brauchst du?'}</h2>
              {term && <button className="text-button" onClick={() => setQuery('')}>Suche zurücksetzen</button>}
            </div>
            {!categories && !catalogError && <p role="status">Services werden geladen …</p>}
            {catalogError && <p className="notice" role="alert">{catalogError}</p>}
            {categories && <p className="sr-only" role="status">{visible.length} Services verfügbar.</p>}
            {categories && visible.length === 0 && <p>Zu „{query.trim()}“ wurde kein Service gefunden.</p>}
            <div className="service-grid">{visible.map(service =>
              <button key={service.id} className="service-tile" onClick={() => open(service.id)} type="button">
                <img src={service.image} alt="" width="420" height="270"/>
                <span>{service.label}</span>
              </button>
            )}</div>
          </div>
        </section>
        <section className="service-promo" aria-labelledby="promo-title">
          <div className="service-promo-copy">
            <h2 id="promo-title">Du beschreibst es.<br/>Wir kümmern uns.</h2>
            <p>Erzähl uns, wobei du Hilfe brauchst. Deine Anfrage geht direkt an den Servicebetrieb.</p>
            <button className="promo-button" type="button" onClick={start}>Anfrage erstellen</button>
          </div>
          <img src="/services/hero.webp" alt="" width="700" height="660"/>
        </section>
      </div>
      {featured.length > 0 && <section className="featured-services" aria-labelledby="featured-title">
        <h2 id="featured-title">Weitere Services entdecken</h2>
        <div className="feature-grid">{featured.map(service =>
          <button className="feature-card" key={service.id} onClick={() => open(service.id)} type="button">
            <img src={service.image} alt="" width="420" height="270" loading="lazy"/>
            <span><strong>{service.title}</strong><small>{service.group}</small></span>
          </button>
        )}</div>
      </section>}
      <section className="customer-benefits" aria-label="So funktioniert deine Anfrage">
        <div><Check aria-hidden/><strong>Direkt anfragen</strong><p>Starte als Gast, auch ohne Kundenkonto.</p></div>
        <div><Camera aria-hidden/><strong>Fotos hinzufügen</strong><p>Zeige dem Servicebetrieb, worum es geht.</p></div>
        <div><ClipboardList aria-hidden/><strong>Anfragen im Blick</strong><p>Verfolge Termine und Nachrichten im Kundenkonto.</p></div>
      </section>
    </main>
    <footer className="customer-footer"><Wordmark href={base}/><a href={base + '/konto'}>Mein Konto</a></footer>
    <CustomerNav base={base} onRequest={start}/>
    {request !== null && <RequestWizard categories={categories ?? []} initial={request} api={api} base={base}
      session={session} onClose={close}/>}
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

function RequestWizard({ categories, initial, api, session, base, onClose }: {
  categories: ServiceCategory[]; initial: string; api: string; base: string; session: Session; onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const [step, setStep] = useState(0);
  const [category, setCategory] = useState(initial);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [name, setName] = useState('');
  const [phone, setPhone] = useState('');
  const [email, setEmail] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState('');
  const key = useRef(crypto.randomUUID());

  useEffect(() => {
    const node = dialog.current!;
    node.showModal();
    return () => { if (node.open) node.close(); };
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
        { title, category, description, name, phone, email, photos }, session.csrfToken, key.current);
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
    {result ? <section className="wizard-success">
      <span className="success-icon"><Check aria-hidden/></span>
      <h2 id="wizard-title" ref={heading} tabIndex={-1}>Deine Anfrage ist eingegangen.</h2>
      <p>Bestätige deine Anfrage über den Link in deiner E-Mail. Danach findest du sie in deinem Kundenkonto.</p>
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
              onChange={event => setTitle(event.target.value)} placeholder="z. B. Wasserhahn reparieren"/></label>
            <fieldset className="category-options"><legend>Welche Art von Service?</legend>
              <div>{categories.map(service => <label key={service.id}>
                <input type="radio" name="category" value={service.id} checked={category === service.id} required
                  onChange={() => setCategory(service.id)}/><span>{service.label}</span>
              </label>)}</div>
            </fieldset>
          </>}
          {step === 1 && <>
            <p>Was soll gemacht werden? Gibt es etwas, das der Servicebetrieb wissen sollte?</p>
            <label>Dein Anliegen<textarea name="description" value={description} required minLength={10} maxLength={4000}
              rows={5} onChange={event => setDescription(event.target.value)}/></label>
            <label className="photo-drop">
              <Camera aria-hidden/><strong>Fotos hinzufügen (optional)</strong><small>{fileHelp}</small>
              <input type="file" aria-label="Fotos auswählen" accept="image/jpeg,image/png,image/webp" multiple
                onChange={event => { addPhotos(event.target.files); event.target.value = ''; }}/>
            </label>
            {files.length > 0 && <ul className="file-list">{files.map((file, index) => <li key={index}>
              <span>{file.name}<small>{Math.ceil(file.size / 1024)} KB</small></span>
              <button type="button" className="text-button" onClick={() => {setFiles(files.filter((_, i) => i !== index)); edit();}}
                aria-label={`Foto ${index + 1} entfernen`}>Entfernen</button>
            </li>)}</ul>}
          </>}
          {step === 2 && <>
            <p>Du kannst die Anfrage ohne Anmeldung absenden. Zur Bestätigung erhältst du eine E-Mail.</p>
            <label>Vor- und Nachname<input autoComplete="name" value={name} required minLength={2} maxLength={120}
              onChange={event => setName(event.target.value)}/></label>
            <label>Telefonnummer<input autoComplete="tel" type="tel" value={phone} required maxLength={50}
              onChange={event => setPhone(event.target.value)}/></label>
            <label>E-Mail-Adresse<input autoComplete="email" type="email" value={email} required maxLength={254}
              onChange={event => setEmail(event.target.value)}/></label>
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
