import type { StartupRepairPreview } from './contracts.js';
import { bool, nullable, reason, shapeFor, text, type Schema } from './validation.js';

export const startupRepairFingerprint: Schema<string> = (value): value is string =>
  typeof value === 'string' && value.length === 64 && /^[0-9a-f]{64}$/.test(value);

const previewShape = shapeFor<StartupRepairPreview>()({
  canRepair: bool,
  previousExecutable: nullable(text()),
  currentExecutable: nullable(text()),
  fingerprint: nullable(startupRepairFingerprint),
  reason,
  detail: nullable(text(4096)),
});

/** A usable review always binds both displayed locations to an exact snapshot. */
export const startupRepairPreview: Schema<StartupRepairPreview> = (
  value,
): value is StartupRepairPreview => {
  if (!previewShape(value)) return false;
  const preview = value;
  return preview.canRepair
    ? preview.previousExecutable !== null &&
        preview.previousExecutable.length > 0 &&
        preview.currentExecutable !== null &&
        preview.currentExecutable.length > 0 &&
        preview.fingerprint !== null
    : preview.fingerprint === null;
};
