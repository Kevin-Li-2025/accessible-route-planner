import { api } from './api';

export type CoordinateRequest = {
  x: number;
  y: number;
};

export type RouteRequest = {
  start: CoordinateRequest;
  end: CoordinateRequest;
  preferences?: string[];
  safetyWeight?: number;
  profile?: string;
};

export type RouteResponse = {
  path?: any;
  distance?: number;
  estimatedTime?: number;
  safetyScore?: number;
  warnings?: string[];
  steps?: any[];
  performance?: RoutePerformanceDiagnostics | null;
  [key: string]: unknown;
};

export type RouteVariant = {
  kind: string;
  description: string;
  route: RouteResponse;
  metrics?: RouteTradeoffMetrics;
};

export type RoutePerformanceDiagnostics = {
  algorithm?: string;
  searchMilliseconds?: number;
  nodesExpanded?: number;
  edgesScanned?: number;
  edgesRelaxed?: number;
  edgesRejectedByFilter?: number;
  riskLookups?: number;
  riskCacheHits?: number;
  riskCacheMisses?: number;
  queuePushes?: number;
  usedAltHeuristic?: boolean;
  usedRelaxedAccessibilitySearch?: boolean;
  foundPath?: boolean;
};

export type RouteTradeoffMetrics = {
  kind?: string;
  distanceMetres?: number;
  estimatedTimeMinutes?: number;
  riskExposure?: number;
  accessibilityPenaltySeconds?: number;
  compositeCost?: number;
  fullSafetyCompositeCost?: number;
  paretoEfficient?: boolean;
};

export type RouteOptionSetDiagnostics = {
  algorithm?: string;
  candidateCount?: number;
  paretoEfficientCount?: number;
  recommendedRegretSeconds?: number;
  recommendedRiskRegret?: number;
  frontier?: RouteTradeoffMetrics[];
  recommendedPerformance?: RoutePerformanceDiagnostics | null;
};

export type SafePathOptionsResponse = {
  recommended: RouteResponse;
  variants: RouteVariant[];
  diagnostics?: RouteOptionSetDiagnostics;
};

export type RouteJobResult = {
  jobId: string;
  kind?: string | number;
  status: string | number;
  route?: RouteResponse | null;
  options?: SafePathOptionsResponse | null;
  error?: string | null;
};

export type RiskScoreResponse = {
  overallRisk?: number;
  hazardProximityRisk?: number;
  hazardDensityRisk?: number;
  infrastructureRisk?: number;
  crimeRisk?: number;
  lightingRisk?: number;
  surveillanceRisk?: number;
  nearbyHazardCount?: number;
  crimeCount?: number;
  nearbyHazards?: unknown[];
};

export type PredictiveRiskResult = {
  overallRisk?: number;
  hazardRisk?: number;
  timeOfDayRisk?: number;
  weatherRisk?: number;
  crimeRisk?: number;
  infrastructureRisk?: number;
  lightingRisk?: number;
  surveillanceRisk?: number;
  riskFactors?: string[];
};

export type RouteGraphCoverageStatus = Record<string, unknown>;

export type QueuedRouteResponse = {
  jobId?: string;
  status?: string | number;
  pollUrl?: string;
  route?: RouteResponse;
  options?: SafePathOptionsResponse;
  [key: string]: unknown;
};

const DEFAULT_JOB_POLL_ATTEMPTS = 18;
const DEFAULT_JOB_POLL_DELAY_MS = 500;
const QUICK_JOB_POLL_DELAYS_MS = [150, 250, 400];

function delay(ms: number) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function isCompleted(status: string | number | undefined) {
  return status === 2 || String(status ?? '').toLowerCase() === 'completed';
}

function isFailed(status: string | number | undefined) {
  return status === 3 || String(status ?? '').toLowerCase() === 'failed';
}

function hasJobId(value: unknown): value is QueuedRouteResponse & { jobId: string } {
  return Boolean(value && typeof value === 'object' && typeof (value as QueuedRouteResponse).jobId === 'string');
}

async function pollRouteJob(jobId: string, attempts = DEFAULT_JOB_POLL_ATTEMPTS): Promise<RouteJobResult> {
  let lastResult: RouteJobResult | null = null;

  for (let attempt = 0; attempt < attempts; attempt += 1) {
    const result = await api.get<RouteJobResult>(
      `/routing/jobs/${encodeURIComponent(jobId)}`,
      { skipAuth: true }
    );
    lastResult = result;

    if (isCompleted(result.status) || isFailed(result.status)) {
      return result;
    }

    await delay(QUICK_JOB_POLL_DELAYS_MS[attempt] ?? DEFAULT_JOB_POLL_DELAY_MS);
  }

  return lastResult ?? {
    jobId,
    status: 'pending',
    error: 'Route job did not complete before the client polling budget expired.',
  };
}

export const routingService = {
  async getSafePath(request: RouteRequest): Promise<RouteResponse | QueuedRouteResponse> {
    return api.post<RouteResponse | QueuedRouteResponse>('/routing/safe-path', request, {
      skipAuth: true,
    });
  },

  async submitSafePathJob(request: RouteRequest): Promise<QueuedRouteResponse> {
    return api.post<QueuedRouteResponse>('/routing/safe-path/async', request, {
      skipAuth: true,
    });
  },

  async getRouteJob(jobId: string): Promise<RouteJobResult> {
    return api.get<RouteJobResult>(
      `/routing/jobs/${encodeURIComponent(jobId)}`,
      { skipAuth: true }
    );
  },

  async getSafePathResolved(request: RouteRequest): Promise<RouteResponse> {
    const response = await this.getSafePath(request);

    if (!hasJobId(response)) {
      return response as RouteResponse;
    }

    const job = await pollRouteJob(response.jobId);
    if (isCompleted(job.status) && job.route) {
      return job.route;
    }

    throw new Error(job.error || 'Route job did not complete.');
  },

  async getSafePathOptions(request: RouteRequest): Promise<SafePathOptionsResponse | QueuedRouteResponse> {
    return api.post<SafePathOptionsResponse | QueuedRouteResponse>(
      '/routing/safe-path/options',
      request,
      { skipAuth: true }
    );
  },

  async getSafePathOptionsResolved(request: RouteRequest): Promise<SafePathOptionsResponse> {
    const response = await this.getSafePathOptions(request);

    if (!hasJobId(response)) {
      return response as SafePathOptionsResponse;
    }

    const job = await pollRouteJob(response.jobId);
    if (isCompleted(job.status) && job.options) {
      return job.options;
    }

    throw new Error(job.error || 'Route options job did not complete.');
  },

  async getRouteGraphStatus(): Promise<RouteGraphCoverageStatus> {
    return api.get<RouteGraphCoverageStatus>('/routing/route-graph/status', {
      skipAuth: true,
    });
  },

  async getRiskScore(latitude: number, longitude: number, radius = 500): Promise<RiskScoreResponse> {
    return api.get<RiskScoreResponse>(
      `/routing/risk-score?lat=${encodeURIComponent(String(latitude))}&lng=${encodeURIComponent(String(longitude))}&radius=${encodeURIComponent(String(radius))}`,
      { skipAuth: true }
    );
  },

  async getAiRiskScore(latitude: number, longitude: number, radius = 500): Promise<PredictiveRiskResult> {
    return api.get<PredictiveRiskResult>(
      `/routing/ai-risk-score?lat=${encodeURIComponent(String(latitude))}&lng=${encodeURIComponent(String(longitude))}&radius=${encodeURIComponent(String(radius))}`,
      { skipAuth: true }
    );
  },

  async getHazardBlendRisk(latitude: number, longitude: number, radius = 500): Promise<PredictiveRiskResult> {
    return api.get<PredictiveRiskResult>(
      `/routing/hazard-blend-risk?lat=${encodeURIComponent(String(latitude))}&lng=${encodeURIComponent(String(longitude))}&radius=${encodeURIComponent(String(radius))}`,
      { skipAuth: true }
    );
  },
};
