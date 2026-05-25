import { api } from './api';
import { API_URL } from './api';
import { TOKEN_KEY, getItemAsync } from './sessionStorage';
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

export type HazardPhotoUpload = {
  uri: string;
  name: string;
  type: string;
};

export type HazardPhotoUploadResponse = {
  hazardId: string;
  photoUrl: string;
  sizeBytes: number;
  contentType: string;
};

type BackendHazardsPage = {
  items?: BackendHazard[];
  nextCursor?: string | null;
  limit?: number;
  hasMore?: boolean;
};

export type HazardBoundsRequest = {
  minLat?: number;
  minLng?: number;
  maxLat?: number;
  maxLng?: number;
};

export type HazardPageRequest = {
  status?: string;
  cursor?: string | null;
  limit?: number;
  query?: string;
} & HazardBoundsRequest;

export type HazardListRequest = {
  status?: string;
} & HazardBoundsRequest;

export type HazardPage = {
  items: AppHazard[];
  nextCursor: string | null;
  limit: number;
  hasMore: boolean;
};

function appendQueryNumber(query: string[], key: string, value: number | undefined) {
  if (typeof value === 'number' && Number.isFinite(value)) {
    query.push(`${key}=${encodeURIComponent(String(value))}`);
  }
}

function buildHazardQuery(request: HazardListRequest | HazardPageRequest = {}) {
  const query: string[] = [];

  appendQueryNumber(query, 'minLat', request.minLat);
  appendQueryNumber(query, 'minLng', request.minLng);
  appendQueryNumber(query, 'maxLat', request.maxLat);
  appendQueryNumber(query, 'maxLng', request.maxLng);

  if (request.status) {
    query.push(`status=${encodeURIComponent(request.status)}`);
  }

  if ('cursor' in request && request.cursor) {
    query.push(`cursor=${encodeURIComponent(request.cursor)}`);
  }

  if ('limit' in request && request.limit) {
    query.push(`limit=${encodeURIComponent(String(request.limit))}`);
  }

  if ('query' in request && request.query?.trim()) {
    query.push(`query=${encodeURIComponent(request.query.trim())}`);
  }

  return query.length ? `?${query.join('&')}` : '';
}

export const hazardsService = {
  async getHazards(request: string | HazardListRequest = {}): Promise<AppHazard[]> {
    const normalizedRequest = typeof request === 'string' ? { status: request } : request;
    const query = buildHazardQuery(normalizedRequest);
    const data = await api.get<BackendHazard[]>(`/hazards${query}`);

    if (!Array.isArray(data)) {
      return [];
    }

    return data
      .map(mapBackendHazardToApp)
      .filter((hazard): hazard is AppHazard => hazard !== null);
  },

  async getHazardsPage(request: HazardPageRequest = {}): Promise<HazardPage> {
    const query = buildHazardQuery(request);
    const data = await api.get<BackendHazardsPage>(`/hazards/page${query}`, { skipAuth: true });
    const rawItems = Array.isArray(data?.items) ? data.items : [];

    return {
      items: rawItems
        .map(mapBackendHazardToApp)
        .filter((hazard): hazard is AppHazard => hazard !== null),
      nextCursor: data?.nextCursor ?? null,
      limit: typeof data?.limit === 'number' ? data.limit : request.limit ?? 25,
      hasMore: Boolean(data?.hasMore && data?.nextCursor),
    };
  },

  async reportHazard(request: CreateHazardRequest): Promise<BackendHazard> {
    return api.post<BackendHazard>('/hazards', {
      type: request.type,
      description: request.description,
      photoUrl: '',
      location: {
        x: request.longitude,
        y: request.latitude,
      },
    });
  },

  async uploadHazardPhoto(id: string | number, photo: HazardPhotoUpload): Promise<HazardPhotoUploadResponse> {
    const token = await getItemAsync(TOKEN_KEY);
    const formData = new FormData();
    const fileName = photo.name || `hazard-${Date.now()}.jpg`;
    const contentType = photo.type || 'image/jpeg';

    if (typeof window !== 'undefined') {
      const blob = await fetch(photo.uri).then((response) => response.blob());
      formData.append('file', blob, fileName);
    } else {
      formData.append('file', {
        uri: photo.uri,
        name: fileName,
        type: contentType,
      } as unknown as Blob);
    }

    const headers = new Headers();
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }

    const response = await fetch(`${API_URL}/hazards/${encodeURIComponent(String(id))}/photo`, {
      method: 'POST',
      headers,
      body: formData,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(text || `Photo upload failed: ${response.status}`);
    }

    return response.json();
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

  async updateHazardStatus(id: string | number, status: number | string): Promise<void> {
    await api.patch(`/hazards/${id}`, status);
  },
};
