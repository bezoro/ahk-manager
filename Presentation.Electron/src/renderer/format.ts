import type { ProcessSnapshot } from '../shared/contracts.js';
import { text } from './resources/pt-BR.js';
export function date(value: string | null): string {
  if (!value || !Number.isFinite(Date.parse(value))) return '—';
  return new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'medium' }).format(
    new Date(value),
  );
}
export function time(value: string | null): string {
  if (!value || !Number.isFinite(Date.parse(value))) return '—';
  return new Intl.DateTimeFormat('pt-BR', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(new Date(value));
}
export function duration(start: string, observation: string | null): string {
  const elapsed = Math.max(
    0,
    Math.floor(((observation ? Date.parse(observation) : Date.now()) - Date.parse(start)) / 1000),
  );
  if (!Number.isFinite(elapsed)) return '—';
  const hours = Math.floor(elapsed / 3600);
  return `${hours.toString().padStart(2, '0')}:${Math.floor((elapsed % 3600) / 60)
    .toString()
    .padStart(2, '0')}:${(elapsed % 60).toString().padStart(2, '0')}`;
}
export function origin(process: ProcessSnapshot): string {
  return process.origin === 'Manager'
    ? text.ownProcess
    : process.origin === 'Recovered'
      ? text.recoveredProcess
      : text.externalProcess;
}
