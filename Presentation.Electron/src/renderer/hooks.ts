import * as React from 'react';
import type { HostState, ThemeState } from '../shared/contracts.js';

const initial: HostState = { snapshot: null, connection: 'starting', busy: false, error: null };
const initialTheme: ThemeState = { dark: matchMedia('(prefers-color-scheme:dark)').matches, highContrast: matchMedia('(forced-colors:active)').matches, reducedMotion: matchMedia('(prefers-reduced-motion:reduce)').matches, accent: '', material: 'solid' };
export function useHost(): HostState {
  const [state, setState] = React.useState(initial);
  React.useEffect(() => { let alive = true; let eventSeen = false; const off = window.ahkManager.onState(value => { eventSeen = true; if (alive) setState(value); }); void window.ahkManager.getState().then(value => { if (alive && !eventSeen) setState(value); }).catch(error => { if (alive && !eventSeen) setState({ ...initial, connection: 'failed', error: String(error) }); }); return () => { alive = false; off(); }; }, []);
  return state;
}
export function useTheme(): ThemeState {
  const [theme, setTheme] = React.useState(initialTheme);
  React.useEffect(() => { let alive = true; let eventSeen = false; const off = window.ahkManager.onTheme(value => { eventSeen = true; if (alive) setTheme(value); }); void window.ahkManager.getTheme().then(value => { if (alive && !eventSeen) setTheme(value); }).catch(() => undefined); return () => { alive = false; off(); }; }, []);
  return theme;
}
export function useNarrow(): boolean {
  const [narrow, setNarrow] = React.useState(matchMedia('(max-width:900px)').matches);
  React.useEffect(() => { const media = matchMedia('(max-width:900px)'); const update = () => setNarrow(media.matches); media.addEventListener('change', update); return () => media.removeEventListener('change', update); }, []);
  return narrow;
}
