import { hazardsService } from '@/services/hazards.service';
import { api } from '@/services/api';

jest.mock('@/services/api', () => ({
  api: {
    get: jest.fn(),
    post: jest.fn(),
    patch: jest.fn(),
  },
}));

const validBackendHazard = {
  id: '1',
  type: 'steps',
  description: 'Steep stairs.',
  status: 0,
  location: { coordinates: [-1.895, 52.481] },
  reportedAt: '2025-01-15T12:00:00.000Z',
};

describe('hazardsService', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('getHazards maps backend items to app model', async () => {
    jest.mocked(api.get).mockResolvedValue([validBackendHazard]);

    const list = await hazardsService.getHazards();

    expect(list).toHaveLength(1);
    expect(list[0].latitude).toBe(52.481);
    expect(list[0].longitude).toBe(-1.895);
  });

  it('getHazards returns empty array when response is not an array', async () => {
    jest.mocked(api.get).mockResolvedValue({} as never);

    await expect(hazardsService.getHazards()).resolves.toEqual([]);
  });

  it('getHazards requests bounded list and maps response', async () => {
    jest.mocked(api.get).mockResolvedValue([validBackendHazard]);

    const list = await hazardsService.getHazards({
      minLat: 52.4,
      minLng: -1.95,
      maxLat: 52.5,
      maxLng: -1.85,
      status: 'Reported',
    });

    expect(api.get).toHaveBeenCalledWith(
      '/hazards?minLat=52.4&minLng=-1.95&maxLat=52.5&maxLng=-1.85&status=Reported'
    );
    expect(list).toHaveLength(1);
  });

  it('getHazardsPage requests bounded page and maps response', async () => {
    jest.mocked(api.get).mockResolvedValue({
      items: [validBackendHazard],
      nextCursor: 'next-page',
      limit: 25,
      hasMore: true,
    } as never);

    const page = await hazardsService.getHazardsPage({
      minLat: 52.4,
      minLng: -1.95,
      maxLat: 52.5,
      maxLng: -1.85,
      status: 'Reported',
      cursor: 'current cursor',
      limit: 25,
      query: 'blocked pavement',
    });

    expect(api.get).toHaveBeenCalledWith(
      '/hazards/page?minLat=52.4&minLng=-1.95&maxLat=52.5&maxLng=-1.85&status=Reported&cursor=current%20cursor&limit=25&query=blocked%20pavement',
      { skipAuth: true }
    );
    expect(page.items).toHaveLength(1);
    expect(page.nextCursor).toBe('next-page');
    expect(page.hasMore).toBe(true);
  });

  it('reportHazard posts backend coordinate location', async () => {
    jest.mocked(api.post).mockResolvedValue(validBackendHazard as never);

    await hazardsService.reportHazard({
      latitude: 52.48,
      longitude: -1.89,
      type: 'broken_pavement',
      description: 'Crack near curb',
    });

    expect(api.post).toHaveBeenCalledWith('/hazards', {
      type: 'broken_pavement',
      description: 'Crack near curb',
      photoUrl: '',
      location: {
        x: -1.89,
        y: 52.48,
      },
    });
  });

  it('getHazardById returns null when request fails', async () => {
    jest.mocked(api.get).mockRejectedValue(new Error('network'));

    await expect(hazardsService.getHazardById('99')).resolves.toBeNull();
  });

  it('getHazardById maps single hazard with skipAuth', async () => {
    jest.mocked(api.get).mockResolvedValue(validBackendHazard as never);

    const hazard = await hazardsService.getHazardById('1');

    expect(api.get).toHaveBeenCalledWith('/hazards/1', { skipAuth: true });
    expect(hazard).not.toBeNull();
    expect(hazard!.id).toBe('1');
  });

  it('updateHazardStatus patches backend status endpoint', async () => {
    jest.mocked(api.patch).mockResolvedValue({} as never);

    await hazardsService.updateHazardStatus('1', 1);

    expect(api.patch).toHaveBeenCalledWith('/hazards/1', 1);
  });
});
