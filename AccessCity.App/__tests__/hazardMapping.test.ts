import {
  formatHazardStatus,
  formatHazardTypeLabel,
  mapBackendHazardToApp,
} from '../services/hazardMapping';

describe('formatHazardTypeLabel', () => {
  it('returns default when type missing', () => {
    expect(formatHazardTypeLabel()).toBe('Hazard reported');
    expect(formatHazardTypeLabel('')).toBe('Hazard reported');
  });

  it('humanises snake_case', () => {
    expect(formatHazardTypeLabel('broken_pavement')).toBe('Broken Pavement');
  });
});

describe('formatHazardStatus', () => {
  it('maps numeric enum values', () => {
    expect(formatHazardStatus(0)).toBe('Reported');
    expect(formatHazardStatus(1)).toBe('UnderReview');
    expect(formatHazardStatus(2)).toBe('Resolved');
  });

  it('passes through non-empty string', () => {
    expect(formatHazardStatus('Custom')).toBe('Custom');
  });
});

describe('mapBackendHazardToApp', () => {
  it('returns null without coordinates', () => {
    expect(mapBackendHazardToApp({ id: '1', type: 'x', description: 'd' })).toBeNull();
    expect(
      mapBackendHazardToApp({
        id: '1',
        location: { coordinates: [-1.89] },
      }),
    ).toBeNull();
  });

  it('maps GeoJSON lon/lat to app fields', () => {
    const app = mapBackendHazardToApp({
      id: 'abc',
      type: 'steps',
      description: 'Steep stairs. Near entrance.',
      status: 0,
      location: { coordinates: [-1.895, 52.481] },
      reportedAt: '2025-01-15T12:00:00.000Z',
    });
    expect(app).not.toBeNull();
    expect(app!.longitude).toBe(-1.895);
    expect(app!.latitude).toBe(52.481);
    expect(app!.type).toBe('steps');
    expect(app!.status).toBe('Reported');
    expect(app!.title).toBe('Steep stairs');
  });
});
