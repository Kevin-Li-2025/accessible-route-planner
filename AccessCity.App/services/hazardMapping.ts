/**
 * Maps backend hazard DTOs to in-app models. Kept free of fetch/SecureStore for straightforward unit tests.
 */

export type BackendHazard = {
  id: string | number;
  type?: string;
  description?: string;
  location?: {
    coordinates?: [number, number];
  };
  status?: number | string;
  reportedAt?: string;
};

export type AppHazard = {
  id: string | number;
  title: string;
  type: string;
  latitude: number;
  longitude: number;
  description: string;
  status: string;
  locationText: string;
  reportedTime: string;
};

export function formatHazardTypeLabel(type?: string): string {
  if (!type) return 'Hazard reported';

  return type
    .replace(/[_-]+/g, ' ')
    .replace(/\b\w/g, (char) => char.toUpperCase());
}

export function formatHazardStatus(status?: number | string): string {
  if (status === 0) return 'Reported';
  if (status === 1) return 'UnderReview';
  if (status === 2) return 'Resolved';
  if (typeof status === 'string' && status.trim()) return status;
  return 'Reported';
}

export function mapBackendHazardToApp(hazard: BackendHazard): AppHazard | null {
  const coordinates = hazard.location?.coordinates;

  if (!coordinates || coordinates.length < 2) {
    return null;
  }

  const description = hazard.description?.trim() || 'Hazard reported';

  return {
    id: hazard.id,
    title: description.split('.')[0]?.trim() || formatHazardTypeLabel(hazard.type),
    type: hazard.type || 'other',
    latitude: coordinates[1],
    longitude: coordinates[0],
    description,
    status: formatHazardStatus(hazard.status),
    locationText: 'Hazard reported',
    reportedTime: hazard.reportedAt
      ? new Date(hazard.reportedAt).toLocaleDateString()
      : 'Recently',
  };
}
