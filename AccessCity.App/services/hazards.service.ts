import { api } from './api';

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

type CreateHazardRequest = {
  latitude: number;
  longitude: number;
  type: string;
  description: string;
};

function formatHazardTypeLabel(type?: string) {
  if (!type) return 'Hazard reported';

  return type
    .replace(/[_-]+/g, ' ')
    .replace(/\b\w/g, (char) => char.toUpperCase());
}

function formatHazardStatus(status?: number | string) {
  if (status === 0) return 'Reported';
  if (status === 1) return 'UnderReview';
  if (status === 2) return 'Resolved';
  if (typeof status === 'string' && status.trim()) return status;
  return 'Reported';
}

function mapHazard(hazard: BackendHazard): AppHazard | null {
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

export const hazardsService = {
  async getHazards(status?: string): Promise<AppHazard[]> {
    const query = status ? `?status=${status}` : '';
    const data = await api.get<BackendHazard[]>(`/hazards${query}`);

    if (!Array.isArray(data)) {
      return [];
    }

    return data
      .map(mapHazard)
      .filter((hazard): hazard is AppHazard => hazard !== null);
  },

  async reportHazard(request: CreateHazardRequest): Promise<BackendHazard> {
    return api.post<BackendHazard>('/hazards', {
      type: request.type,
      description: request.description,
      photoUrl: '',
      location: {
        type: 'Point',
        coordinates: [request.longitude, request.latitude],
      },
    });
  },

  async getHazardById(id: string | number): Promise<AppHazard | null> {
    try {
      // The individual get endpoint may not require auth, similar to the list endpoint
      const data = await api.get<BackendHazard>(`/hazards/${id}`, { skipAuth: true });
      return data ? mapHazard(data) : null;
    } catch (error) {
      console.error(`Failed to fetch hazard ${id}:`, error);
      return null;
    }
  },
};
