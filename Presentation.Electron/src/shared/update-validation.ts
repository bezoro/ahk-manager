import { array, bool, nullable, oneOf, shape, text } from './validation.js';

const finiteProgress = (value: unknown): boolean => typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 100;
export const updateState = shape({
  currentVersion: text(64), phase: oneOf('idle', 'checking', 'downloading', 'installing', 'error'),
  availableVersion: nullable(text(64)), lastCheckedAt: nullable(text(64)), error: nullable(text(4000)),
  accessConfigured: bool, progress: nullable(finiteProgress),
  releases: array(shape({ id: text(100), version: text(64), publishedAt: nullable(text(64)), notes: text(16000),
    commit: nullable(text(64)), installed: bool, canInstall: bool, cached: bool }), 1000),
});
