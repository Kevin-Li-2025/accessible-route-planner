import { api } from './api';

export type MissingOsmAttributeCandidate = {
  attribute: string;
  value: string;
  confidence: number;
  evidence: string;
  source: string;
  canAutoApply: boolean;
};

export type DuplicateHazardSuggestion = {
  hazardId: string;
  distanceMetres: number;
  confidence: number;
  reason: string;
};

export type HazardAiEnrichment = {
  hazardId: string;
  forRouteDecision: boolean;
  provider: string;
  generatedAtUtc: string;
  text: {
    normalizedDescription: string;
    suggestedType: string;
    suggestedSeverity: string;
    confidence: number;
    adminSummary: string;
    tags: string[];
  };
  duplicateSuggestions: DuplicateHazardSuggestion[];
  missingOsmAttributeCandidates: MissingOsmAttributeCandidate[];
  guardrails: string[];
};

export type HazardReportDraftAiRequest = {
  latitude: number;
  longitude: number;
  type: string;
  description: string;
  photoAttached?: boolean;
  photoUrl?: string | null;
};

export type HazardReportDraftAiResult = {
  forRouteDecision: boolean;
  provider: string;
  generatedAtUtc: string;
  text: HazardAiEnrichment['text'];
  duplicateSuggestions: DuplicateHazardSuggestion[];
  missingOsmAttributeCandidates: MissingOsmAttributeCandidate[];
  shouldReviewExistingReport: boolean;
  suggestedDescriptionChips: string[];
  guardrails: string[];
};

export type HazardPhotoAiAnalysisRequest = {
  photoUrl?: string | null;
  observationText?: string | null;
  includeDraftVerification?: boolean;
};

export type HazardPhotoAiAnalysisResult = {
  hazardId: string;
  linkedInfrastructureAssetId?: number | null;
  forRouteDecision: boolean;
  provider: string;
  model: string;
  generatedAtUtc: string;
  photoUrl: string;
  reviewStatus: string;
  adminSummary: string;
  attributeCandidates: MissingOsmAttributeCandidate[];
  draftVerification?: Record<string, unknown> | null;
  reviewSubmission?: Record<string, unknown> | null;
  guardrails: string[];
  limitations: string[];
};

export type RouteExplanationResponse = {
  forRouteDecision: boolean;
  provider: string;
  explanation: string;
  reasons: string[];
  limitations: string[];
  generatedAtUtc: string;
};

export type AccessibilityAiReviewResult = {
  infrastructureAssetId: number;
  forRouteDecision: boolean;
  provider: string;
  generatedAtUtc: string;
  adminSummary: string;
  missingAttributeCandidates: MissingOsmAttributeCandidate[];
  verificationChecklist: string[];
  guardrails: string[];
};

export type AccessibilityAiInferenceRequest = {
  observationText: string;
  photos?: Array<Record<string, unknown>>;
  includeDraftVerification?: boolean;
};

export type AccessibilityAiInferenceResult = {
  infrastructureAssetId: number;
  forRouteDecision: boolean;
  provider: string;
  model: string;
  generatedAtUtc: string;
  adminSummary: string;
  attributeCandidates: MissingOsmAttributeCandidate[];
  draftVerification?: Record<string, unknown> | null;
  guardrails: string[];
  limitations: string[];
};

export const aiAssistService = {
  async previewHazardReportDraft(
    request: HazardReportDraftAiRequest
  ): Promise<HazardReportDraftAiResult> {
    return api.post<HazardReportDraftAiResult>(
      '/ai-assist/hazards/report-draft',
      request
    );
  },

  async getHazardEnrichment(hazardId: string): Promise<HazardAiEnrichment> {
    return api.get<HazardAiEnrichment>(
      `/ai-assist/hazards/${encodeURIComponent(hazardId)}/enrichment`
    );
  },

  async analyzeHazardPhoto(
    hazardId: string | number,
    request: HazardPhotoAiAnalysisRequest = {}
  ): Promise<HazardPhotoAiAnalysisResult> {
    return api.post<HazardPhotoAiAnalysisResult>(
      `/ai-assist/hazards/${encodeURIComponent(String(hazardId))}/photo-analysis`,
      request
    );
  },

  async explainRoute(routeRequest: unknown, route: unknown): Promise<RouteExplanationResponse> {
    return api.post<RouteExplanationResponse>(
      '/ai-assist/route-explanation',
      {
        routeRequest,
        route,
      },
      { skipAuth: true }
    );
  },

  async getAccessibilityReview(assetId: number): Promise<AccessibilityAiReviewResult> {
    return api.get<AccessibilityAiReviewResult>(
      `/ai-assist/infrastructure/${encodeURIComponent(String(assetId))}/accessibility-review`
    );
  },

  async generateAccessibilityCandidates(
    assetId: number,
    request: AccessibilityAiInferenceRequest
  ): Promise<AccessibilityAiInferenceResult> {
    return api.post<AccessibilityAiInferenceResult>(
      `/ai-assist/infrastructure/${encodeURIComponent(String(assetId))}/accessibility-candidates`,
      request
    );
  },
};
