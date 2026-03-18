import * as Speech from 'expo-speech';

const LEAD_METRES = 48;
const RATE = 0.92;
const LANGUAGE = 'en-GB';

export interface VoiceStep {
  toLat: number;
  toLng: number;
  instruction: string;
}

function distMetres(
  lat1: number,
  lng1: number,
  lat2: number,
  lng2: number
): number {
  const R = 6371e3;
  const φ1 = (lat1 * Math.PI) / 180;
  const φ2 = (lat2 * Math.PI) / 180;
  const Δφ = ((lat2 - lat1) * Math.PI) / 180;
  const Δλ = ((lng2 - lng1) * Math.PI) / 180;
  const a =
    Math.sin(Δφ / 2) * Math.sin(Δφ / 2) +
    Math.cos(φ1) * Math.cos(φ2) * Math.sin(Δλ / 2) * Math.sin(Δλ / 2);
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return R * c;
}

export function runVoiceGuidance(
  lat: number,
  lng: number,
  steps: VoiceStep[],
  lastSpokenRef: { current: number }
): void {
  if (!steps.length) return;

  if (lastSpokenRef.current === -1) {
    Speech.stop();
    Speech.speak(steps[0].instruction.trim(), { rate: RATE, language: LANGUAGE });
    lastSpokenRef.current = 0;
    return;
  }

  for (let i = lastSpokenRef.current + 1; i < steps.length; i++) {
    const step = steps[i];
    const d = distMetres(lat, lng, step.toLat, step.toLng);
    if (d <= LEAD_METRES && step.instruction.trim()) {
      Speech.stop();
      Speech.speak(step.instruction.trim(), {
        rate: RATE,
        language: LANGUAGE,
      });
      lastSpokenRef.current = i;
      return;
    }
  }
}

export function stopVoiceGuidance(): void {
  Speech.stop();
}

export function stepsFromApi(raw: unknown): VoiceStep[] {
  const list = Array.isArray(raw) ? raw : [];
  const out: VoiceStep[] = [];

  for (const s of list) {
    const step = s as Record<string, unknown>;
    const to = step.to as Record<string, unknown> | undefined;
    const coords = to?.coordinates as [number, number] | undefined;
    const instr = typeof step.instruction === 'string' ? step.instruction : '';
    if (coords && coords.length >= 2 && instr) {
      out.push({
        toLng: Number(coords[0]),
        toLat: Number(coords[1]),
        instruction: instr,
      });
    }
  }

  return out;
}

export function stepsFromCoordinates(coords: { latitude: number; longitude: number }[]): VoiceStep[] {
  if (coords.length < 2) return [];

  const out: VoiceStep[] = [];
  const THRESHOLD_DEG = 22;

  for (let i = 1; i < coords.length - 1; i++) {
    const a = coords[i - 1];
    const b = coords[i];
    const c = coords[i + 1];
    const bear1 = Math.atan2(
      (b.longitude - a.longitude) * Math.cos((a.latitude * Math.PI) / 180),
      b.latitude - a.latitude
    );
    const bear2 = Math.atan2(
      (c.longitude - b.longitude) * Math.cos((b.latitude * Math.PI) / 180),
      c.latitude - b.latitude
    );
    let deg = ((bear2 - bear1) * 180) / Math.PI;
    if (deg > 180) deg -= 360;
    if (deg < -180) deg += 360;
    if (Math.abs(deg) >= THRESHOLD_DEG) {
      const instruction = deg > 0 ? 'Turn left.' : 'Turn right.';
      out.push({
        toLat: b.latitude,
        toLng: b.longitude,
        instruction,
      });
    }
  }

  const last = coords[coords.length - 1];
  out.push({
    toLat: last.latitude,
    toLng: last.longitude,
    instruction: 'Arrive at your destination.',
  });

  return out;
}
