import { useState } from 'react';
import type { FormEvent } from 'react';
import { get, send } from './api';
import type { Session } from './api';

const unavailable = 'Diese Funktion ist in der Demoversion noch nicht verfügbar.';

export function NativeLogin({ session }: { session: Session }) {
  const query = new URLSearchParams(location.search);
  const workforce = query.get('mode') === 'workforce';
  const requested = query.get('returnTo') || '/';
  const destination = /^\/(?:[a-zA-Z0-9-]+\/?)*$/.test(requested) ? requested : '/';
  const [mode, setMode] = useState<'login' | 'register' | 'recovery'>('login');
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const values = new FormData(form);
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const fields = { email: values.get('email'), password: values.get('password'),
        workforce, code: values.get('code') || null };
      if (mode === 'register') {
        await send('/api/v1/auth/register', fields, session.csrfToken);
        form.reset();
        setMode('login');
        setNotice('Wenn das Konto neu ist, kannst du dich jetzt mit deinem Passwort anmelden.');
      } else {
        await send('/api/v1/auth/login', fields, session.csrfToken);
        location.assign(destination);
      }
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function showRecovery() {
    setMode('recovery');
    setError('');
    setNotice('');
    try {
      const recovery = await get<{ message?: string; url?: string }>('/api/v1/auth/recovery');
      if (recovery.url) location.assign(recovery.url);
      else setNotice(recovery.message || unavailable);
    } catch {
      setNotice(unavailable);
    }
  }

  return <section className="panel native-login">
    <h1>{mode === 'recovery' ? 'Passwort vergessen?' : mode === 'register' ? 'Kundenkonto erstellen'
      : workforce ? 'Im Arbeitsbereich anmelden' : 'Im Kundenkonto anmelden'}</h1>
    {mode === 'register' && <p>Verwende für die Demo eine Testadresse und keine echten Kundendaten.</p>}
    {notice && <p role="status">{notice}</p>}
    {error && <p role="alert" className="notice">{error}</p>}
    {mode !== 'recovery' && <form className="stack" key={mode} onSubmit={submit}>
      <label>E-Mail-Adresse<input required name="email" type="email" maxLength={254} autoComplete="username"/></label>
      <label>Passwort<input required name="password" type="password" minLength={mode === 'register' ? 12 : undefined}
        maxLength={128} autoComplete={mode === 'register' ? 'new-password' : 'current-password'}/></label>
      {mode === 'register' && <p className="muted">Mindestens 12 Zeichen. Verwende ein Passwort, das du für kein anderes Konto nutzt.</p>}
      {workforce && <label>Code aus der Authenticator-App (falls eingerichtet)
        <input name="code" inputMode="numeric" pattern="[0-9]{6}" maxLength={6} autoComplete="one-time-code"/></label>}
      <button type="submit" className="primary" disabled={busy}>
        {mode === 'register' ? 'Konto erstellen' : 'Anmelden'}</button>
    </form>}
    <div className="native-login-links">
      {mode !== 'login' && <button className="text-button" onClick={() => {
        setMode('login'); setNotice(''); setError('');
      }}>Zur Anmeldung</button>}
      {mode === 'login' && !workforce && <button className="text-button" onClick={() => {
        setMode('register'); setNotice(''); setError('');
      }}>Kundenkonto erstellen</button>}
      {mode === 'login' && <button className="text-button" onClick={showRecovery}>Passwort vergessen?</button>}
    </div>
  </section>;
}
