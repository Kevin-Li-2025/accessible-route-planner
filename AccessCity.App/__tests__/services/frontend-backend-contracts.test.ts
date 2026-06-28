import { accountService } from '@/services/account.service';
import { aiAssistService } from '@/services/aiAssist.service';
import { authService } from '@/services/auth.service';
import { geocodingService } from '@/services/geocoding.service';
import { hazardsService } from '@/services/hazards.service';
import { routingService } from '@/services/routing.service';
import { spatialService } from '@/services/spatial.service';
import {
  adminAccessibilityService,
  adminOsmService,
  dashboardService,
  integrationsService,
  tileProfileService,
} from '@/services/system.service';
import { api } from '@/services/api';
import { getItemAsync, setItemAsync } from '@/services/sessionStorage';

jest.mock('@/services/api', () => ({
  API_URL: 'http://api.test/api/v1',
  api: {
    get: jest.fn(),
    post: jest.fn(),
    put: jest.fn(),
    patch: jest.fn(),
    request: jest.fn(),
  },
}));

jest.mock('@/services/sessionStorage', () => ({
  TOKEN_KEY: 'ac_access_token',
  REFRESH_TOKEN_KEY: 'ac_refresh_token',
  USER_KEY: 'ac_user_data',
  getItemAsync: jest.fn(),
  setItemAsync: jest.fn(),
  deleteItemAsync: jest.fn(),
}));

const validBackendHazard = {
  id: 'hazard-1',
  type: 'blocked_pavement',
  description: 'Blocked pavement near curb.',
  status: 0,
  location: { coordinates: [-1.895, 52.481] },
  reportedAt: '2026-05-26T09:00:00.000Z',
};

const routeRequest = {
  start: { x: -1.9, y: 52.48 },
  end: { x: -1.885, y: 52.486 },
  profile: 'manual-wheelchair',
  safetyWeight: 0.75,
};

describe('frontend-backend service contracts', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest.mocked(getItemAsync).mockResolvedValue(null);
    jest.mocked(setItemAsync).mockResolvedValue(undefined);
  });

  it('wires auth, OAuth, and account endpoints to the backend contract', async () => {
    jest.mocked(api.post)
      .mockResolvedValueOnce({
        token: 'access',
        refreshToken: 'refresh',
        email: 'user@example.com',
        fullName: 'Test User',
      })
      .mockResolvedValueOnce({
        token: 'access',
        refreshToken: 'refresh',
        email: 'new@example.com',
        fullName: 'New User',
      })
      .mockResolvedValueOnce({ message: 'sent' })
      .mockResolvedValueOnce({ message: 'reset' })
      .mockResolvedValueOnce({ id: 'support-1', status: 'received' });
    jest.mocked(api.get)
      .mockResolvedValueOnce([{ provider: 'google', displayName: 'Google', configured: true }])
      .mockResolvedValueOnce({ provider: 'google', authorizationUrl: 'https://auth.example.com' })
      .mockResolvedValueOnce({
        email: 'user@example.com',
        fullName: 'Test User',
        accessibilityPreferences: {
          mobilityDevice: 'Manual wheelchair',
          avoidStairs: true,
          avoidSteepIncline: true,
          preferCurbRamps: true,
          preferSmoothSurface: true,
          maxDetourToleranceMinutes: 30,
        },
      })
      .mockResolvedValueOnce({
        hazardAlerts: true,
        routeWarnings: true,
        reportUpdates: true,
        weeklySummary: false,
      });
    jest.mocked(api.put)
      .mockResolvedValueOnce({
        email: 'user@example.com',
        fullName: 'Updated User',
        accessibilityPreferences: {
          mobilityDevice: 'Manual wheelchair',
          avoidStairs: true,
          avoidSteepIncline: true,
          preferCurbRamps: true,
          preferSmoothSurface: true,
          maxDetourToleranceMinutes: 30,
        },
      })
      .mockResolvedValueOnce({
        hazardAlerts: false,
        routeWarnings: true,
        reportUpdates: true,
        weeklySummary: true,
      });

    await authService.login({ email: 'user@example.com', password: 'Password12' });
    await authService.register({
      email: 'new@example.com',
      fullName: 'New User',
      password: 'Password12',
    });
    await authService.forgotPassword('user@example.com');
    await authService.resetPassword({
      email: 'user@example.com',
      token: 'reset-token',
      newPassword: 'NewPassword12',
    });
    await authService.getOAuthProviders();
    await authService.createOAuthAuthorizeUrl('google', 'accesscity://oauth/callback');
    await accountService.getProfile();
    await accountService.updateProfile({ fullName: 'Updated User' });
    await accountService.getNotificationSettings();
    await accountService.updateNotificationSettings({
      hazardAlerts: false,
      routeWarnings: true,
      reportUpdates: true,
      weeklySummary: true,
    });
    await accountService.submitSupportContact({
      subject: 'Need help',
      message: 'Route did not look right.',
      category: 'routing',
    });

    expect(api.post).toHaveBeenNthCalledWith(1, '/auth/login', {
      email: 'user@example.com',
      password: 'Password12',
    }, { skipAuth: true });
    expect(api.post).toHaveBeenNthCalledWith(2, '/auth/register', {
      email: 'new@example.com',
      fullName: 'New User',
      password: 'Password12',
    }, { skipAuth: true });
    expect(api.post).toHaveBeenNthCalledWith(
      3,
      '/auth/forgot-password',
      { email: 'user@example.com' },
      { skipAuth: true }
    );
    expect(api.post).toHaveBeenNthCalledWith(
      4,
      '/auth/reset-password',
      { email: 'user@example.com', token: 'reset-token', newPassword: 'NewPassword12' },
      { skipAuth: true }
    );
    expect(api.get).toHaveBeenNthCalledWith(1, '/auth/oauth/providers', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(
      2,
      '/auth/oauth/google/authorize?redirectUri=accesscity%3A%2F%2Foauth%2Fcallback',
      { skipAuth: true }
    );
    expect(api.get).toHaveBeenNthCalledWith(3, '/account/profile');
    expect(api.put).toHaveBeenNthCalledWith(1, '/account/profile', { fullName: 'Updated User' });
    expect(api.get).toHaveBeenNthCalledWith(4, '/account/notifications');
    expect(api.put).toHaveBeenNthCalledWith(2, '/account/notifications', {
      hazardAlerts: false,
      routeWarnings: true,
      reportUpdates: true,
      weeklySummary: true,
    });
    expect(api.post).toHaveBeenNthCalledWith(5, '/account/support/contact', {
      subject: 'Need help',
      message: 'Route did not look right.',
      category: 'routing',
    });
  });

  it('wires route, risk, geocoding, and hazard endpoints to live backend paths', async () => {
    jest.mocked(api.post)
      .mockResolvedValueOnce({ path: [], safetyScore: 92 })
      .mockResolvedValueOnce({ jobId: 'route/job 1', status: 'pending' })
      .mockResolvedValueOnce({ recommended: { path: [] }, variants: [] })
      .mockResolvedValueOnce(validBackendHazard as never);
    jest.mocked(api.get)
      .mockResolvedValueOnce({ jobId: 'route/job 1', status: 'completed', route: { path: [] } })
      .mockResolvedValueOnce({ coverage: 'warm' })
      .mockResolvedValueOnce({ overallRisk: 0.25 })
      .mockResolvedValueOnce({ overallRisk: 0.35 })
      .mockResolvedValueOnce({ overallRisk: 0.2 })
      .mockResolvedValueOnce([{ display_name: 'Birmingham', lat: 52.48, lon: -1.89 }])
      .mockResolvedValueOnce({ result: { display_name: 'Bournbrook Road', lat: 52.48, lon: -1.89 } })
      .mockResolvedValueOnce([validBackendHazard])
      .mockResolvedValueOnce({
        items: [validBackendHazard],
        nextCursor: 'next cursor',
        limit: 10,
        hasMore: true,
      })
      .mockResolvedValueOnce(validBackendHazard);
    jest.mocked(api.patch).mockResolvedValueOnce({} as never);

    await routingService.getSafePath(routeRequest);
    await routingService.submitSafePathJob(routeRequest);
    await routingService.getRouteJob('route/job 1');
    await routingService.getSafePathOptions(routeRequest);
    await routingService.getRouteGraphStatus();
    await routingService.getRiskScore(52.48, -1.89, 750);
    await routingService.getAiRiskScore(52.48, -1.89, 750);
    await routingService.getHazardBlendRisk(52.48, -1.89, 750);
    await geocodingService.search('Birmingham New Street');
    await geocodingService.reverse(52.48, -1.89);
    await hazardsService.getHazards({
      minLat: 52.4,
      minLng: -1.95,
      maxLat: 52.5,
      maxLng: -1.85,
      status: 'Reported',
    });
    await hazardsService.getHazardsPage({
      status: 'Reported',
      cursor: 'next cursor',
      limit: 10,
      query: 'blocked pavement',
    });
    await hazardsService.reportHazard({
      latitude: 52.48,
      longitude: -1.89,
      type: 'blocked_pavement',
      description: 'Blocked pavement',
    });
    await hazardsService.getHazardById('hazard/1');
    await hazardsService.updateHazardStatus('hazard/1', 'Acknowledged');

    expect(api.post).toHaveBeenNthCalledWith(1, '/routing/safe-path', routeRequest, { skipAuth: true });
    expect(api.post).toHaveBeenNthCalledWith(2, '/routing/safe-path/async', routeRequest, { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(1, '/routing/jobs/route%2Fjob%201', { skipAuth: true });
    expect(api.post).toHaveBeenNthCalledWith(3, '/routing/safe-path/options', routeRequest, { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(2, '/routing/route-graph/status', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(3, '/routing/risk-score?lat=52.48&lng=-1.89&radius=750', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(4, '/routing/ai-risk-score?lat=52.48&lng=-1.89&radius=750', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(5, '/routing/hazard-blend-risk?lat=52.48&lng=-1.89&radius=750', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(6, '/geocoding/search?query=Birmingham%20New%20Street', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(7, '/geocoding/reverse?lat=52.48&lon=-1.89', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(
      8,
      '/hazards?minLat=52.4&minLng=-1.95&maxLat=52.5&maxLng=-1.85&status=Reported'
    );
    expect(api.get).toHaveBeenNthCalledWith(
      9,
      '/hazards/page?status=Reported&cursor=next%20cursor&limit=10&query=blocked%20pavement',
      { skipAuth: true }
    );
    expect(api.post).toHaveBeenNthCalledWith(4, '/hazards', {
      type: 'blocked_pavement',
      description: 'Blocked pavement',
      photoUrl: '',
      location: { x: -1.89, y: 52.48 },
    });
    expect(api.get).toHaveBeenNthCalledWith(10, '/hazards/hazard/1', { skipAuth: true });
    expect(api.patch).toHaveBeenCalledWith('/hazards/hazard/1', 'Acknowledged');
  });

  it('uploads hazard photos through the backend multipart endpoint', async () => {
    jest.mocked(getItemAsync).mockResolvedValue('access-token');
    const originalFetch = global.fetch;
    const blob = new Blob(['photo'], { type: 'image/jpeg' });
    const uploadResponse = {
      hazardId: 'hazard-1',
      photoUrl: '/api/v1/hazards/photos/photo.jpg',
      sizeBytes: 5,
      contentType: 'image/jpeg',
    };
    global.fetch = jest.fn()
      .mockResolvedValueOnce({ blob: jest.fn().mockResolvedValue(blob) })
      .mockResolvedValueOnce({
        ok: true,
        json: jest.fn().mockResolvedValue(uploadResponse),
      }) as never;

    const result = await hazardsService.uploadHazardPhoto('hazard/1', {
      uri: 'file:///tmp/photo.jpg',
      name: 'photo.jpg',
      type: 'image/jpeg',
    });

    expect(result).toEqual(uploadResponse);
    expect(global.fetch).toHaveBeenNthCalledWith(1, 'file:///tmp/photo.jpg');
    expect(global.fetch).toHaveBeenNthCalledWith(
      2,
      'http://api.test/api/v1/hazards/hazard%2F1/photo',
      expect.objectContaining({
        method: 'POST',
        body: expect.any(FormData),
      })
    );

    const uploadOptions = jest.mocked(global.fetch).mock.calls[1][1] as RequestInit;
    expect(uploadOptions.headers).toBeInstanceOf(Headers);
    expect((uploadOptions.headers as Headers).get('Authorization')).toBe('Bearer access-token');
    global.fetch = originalFetch;
  });

  it('wires spatial, offline map, and safe haven endpoints', async () => {
    jest.mocked(api.get)
      .mockResolvedValueOnce([{ id: 'poi-1', name: 'Station' }])
      .mockResolvedValueOnce({ type: 'FeatureCollection', layer: 'hazards', features: [] })
      .mockResolvedValueOnce({ assetId: 42, surface: 'smooth' })
      .mockResolvedValueOnce([{ id: 'verification-1' }])
      .mockResolvedValueOnce({ places: [], googlePlacesConfigured: false, radiusMetres: 500 })
      .mockResolvedValueOnce({ area: 'birmingham' });
    jest.mocked(api.post).mockResolvedValueOnce({ id: 'verification-2' });

    await spatialService.getPointsOfInterest(52.48, -1.89, 1200);
    await spatialService.getMapOverlay('infrastructure');
    await spatialService.getAccessibilityProfile(42);
    await spatialService.submitAccessibilityVerification(42, { surface: 'smooth' });
    await spatialService.getAccessibilityVerifications(42);
    await spatialService.getNearbySafeHavens(52.48, -1.89, 500);
    await spatialService.getOfflineMapBundle(52.4, -1.95, 52.5, -1.85);

    expect(api.get).toHaveBeenNthCalledWith(1, '/spatial/poi?lat=52.48&lng=-1.89&radius=1200', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(2, '/spatial/map-overlay?layerName=infrastructure', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(3, '/spatial/infrastructure/42/accessibility-profile', { skipAuth: true });
    expect(api.post).toHaveBeenCalledWith('/spatial/infrastructure/42/accessibility-verifications', { surface: 'smooth' });
    expect(api.get).toHaveBeenNthCalledWith(4, '/spatial/infrastructure/42/accessibility-verifications');
    expect(api.get).toHaveBeenNthCalledWith(5, '/safe-haven/nearby?lat=52.48&lng=-1.89&radius=500', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(6, '/OfflineMap/bundle?minLat=52.4&minLng=-1.95&maxLat=52.5&maxLng=-1.85');
  });

  it('wires ops/admin dashboards and management endpoints', async () => {
    jest.mocked(api.get)
      .mockResolvedValueOnce({ totalHazards: 10, activeUsers: 2 })
      .mockResolvedValueOnce({ type: 'FeatureCollection', features: [] })
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce({ overpassEndpoint: 'https://overpass-api.de/api/interpreter' })
      .mockResolvedValueOnce({ jobId: 'job/1', status: 'Queued' })
      .mockResolvedValueOnce({ z: 14, x: 8172, y: 5444, cacheHit: true });
    jest.mocked(api.post)
      .mockResolvedValueOnce({ imported: 1 })
      .mockResolvedValueOnce({ jobId: 'job/1', status: 'Queued' })
      .mockResolvedValueOnce({ qualityGatePassed: true, routes: [] })
      .mockResolvedValueOnce({ status: 'applied' })
      .mockResolvedValueOnce({ status: 'rejected' });

    await dashboardService.getSummary();
    await dashboardService.getHeatMap();
    await dashboardService.getInfrastructureFeed(15);
    await integrationsService.getStatus();
    await adminOsmService.runImportNow();
    await adminOsmService.queueImportJob();
    await adminOsmService.getImportJob('job/1');
    await adminOsmService.profileRouteGraph({ hotReadsPerRoute: 2, routes: [] });
    await tileProfileService.getProfile(14, 8172, 5444);
    await adminAccessibilityService.applyVerification('submission/1', { notes: 'Looks valid' });
    await adminAccessibilityService.rejectVerification('submission/2', { notes: 'Photo unclear' });

    expect(api.get).toHaveBeenNthCalledWith(1, '/dashboard/summary', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(2, '/dashboard/heat-map', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(3, '/dashboard/infrastructure-feed?limit=15', { skipAuth: true });
    expect(api.get).toHaveBeenNthCalledWith(4, '/integrations/status', { skipAuth: true });
    expect(api.post).toHaveBeenNthCalledWith(1, '/admin/osm/import', {});
    expect(api.post).toHaveBeenNthCalledWith(2, '/admin/osm/import-jobs', {});
    expect(api.get).toHaveBeenNthCalledWith(5, '/admin/osm/import-jobs/job%2F1');
    expect(api.post).toHaveBeenNthCalledWith(3, '/admin/osm/route-graph/profile', { hotReadsPerRoute: 2, routes: [] });
    expect(api.get).toHaveBeenNthCalledWith(6, '/tiles/14/8172/5444/profile');
    expect(api.post).toHaveBeenNthCalledWith(4, '/admin/accessibility-verifications/submission%2F1/apply', { notes: 'Looks valid' });
    expect(api.post).toHaveBeenNthCalledWith(5, '/admin/accessibility-verifications/submission%2F2/reject', { notes: 'Photo unclear' });
  });

  it('wires AI-assist endpoints only as review/explanation helpers, not route decision makers', async () => {
    jest.mocked(api.post)
      .mockResolvedValueOnce({ forRouteDecision: false, provider: 'local', guardrails: [] })
      .mockResolvedValueOnce({ forRouteDecision: false, provider: 'local', guardrails: [] })
      .mockResolvedValueOnce({ forRouteDecision: false, provider: 'local', reasons: [] })
      .mockResolvedValueOnce({ forRouteDecision: false, provider: 'local', guardrails: [] });
    jest.mocked(api.get)
      .mockResolvedValueOnce({ hazardId: 'hazard-1', forRouteDecision: false, provider: 'local' })
      .mockResolvedValueOnce({ infrastructureAssetId: 42, forRouteDecision: false, provider: 'local' });

    await aiAssistService.previewHazardReportDraft({
      latitude: 52.48,
      longitude: -1.89,
      type: 'blocked_pavement',
      description: 'Blocked pavement near curb',
    });
    await aiAssistService.getHazardEnrichment('hazard/1');
    await aiAssistService.analyzeHazardPhoto('hazard/1', { photoUrl: '/photos/1.jpg' });
    await aiAssistService.explainRoute(routeRequest, { path: [] });
    await aiAssistService.getAccessibilityReview(42);
    await aiAssistService.generateAccessibilityCandidates(42, {
      observationText: 'Curb ramp seems present.',
      includeDraftVerification: true,
    });

    expect(api.post).toHaveBeenNthCalledWith(1, '/ai-assist/hazards/report-draft', {
      latitude: 52.48,
      longitude: -1.89,
      type: 'blocked_pavement',
      description: 'Blocked pavement near curb',
    });
    expect(api.get).toHaveBeenNthCalledWith(1, '/ai-assist/hazards/hazard%2F1/enrichment');
    expect(api.post).toHaveBeenNthCalledWith(2, '/ai-assist/hazards/hazard%2F1/photo-analysis', {
      photoUrl: '/photos/1.jpg',
    });
    expect(api.post).toHaveBeenNthCalledWith(
      3,
      '/ai-assist/route-explanation',
      { routeRequest, route: { path: [] } },
      { skipAuth: true }
    );
    expect(api.get).toHaveBeenNthCalledWith(2, '/ai-assist/infrastructure/42/accessibility-review');
    expect(api.post).toHaveBeenNthCalledWith(4, '/ai-assist/infrastructure/42/accessibility-candidates', {
      observationText: 'Curb ramp seems present.',
      includeDraftVerification: true,
    });
  });
});
