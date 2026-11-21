import { useEffect, useState } from 'react';
import { get } from './api';

export function useLoad<T>(url: string | undefined, revision = 0) {
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
