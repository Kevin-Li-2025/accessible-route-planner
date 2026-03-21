import { api } from './api';
import {
  type AppHazard,
  type BackendHazard,
  mapBackendHazardToApp,
} from './hazardMapping';

export type { AppHazard, BackendHazard } from './hazardMapping';

type CreateHazardRequest = {
  latitude: number;
  longitude: number;
  type: string;
  description: string;
};

export const hazardsService = {
  async getHazards(status?: string): Promise<AppHazard[]> {
    const query = status ? `?status=${status}` : '';
    const data = await api.get<BackendHazard[]>(`/hazards${query}`);

    if (!Array.isArray(data)) {
      return [];
    }

    return data
      .map(mapBackendHazardToApp)
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
      const data = await api.get<BackendHazard>(`/hazards/${id}`, { skipAuth: true });
      return data ? mapBackendHazardToApp(data) : null;
    } catch (error) {
      console.error(`Failed to fetch hazard ${id}:`, error);
      return null;
    }
  },
};
