import { useEffect, useState } from 'react';
import { ApiError, get } from './api';

export function useLoad<T>(url: string | undefined, revision = 0) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState('');
  const [status, setStatus] = useState<number>();
  useEffect(() => {
    let active = true;
    setData(undefined);
    setError('');
    setStatus(undefined);
    if (url) get<T>(url).then(x => { if (active) setData(x); })
      .catch(x => {
        if (!active) return;
        setError(x instanceof Error ? x.message : 'Die Anfrage konnte nicht verarbeitet werden.');
        setStatus(x instanceof ApiError ? x.status : undefined);
      });
    return () => { active = false; };
  }, [url, revision]);
  return { data, error, status };
}
