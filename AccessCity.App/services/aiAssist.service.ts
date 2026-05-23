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

export const aiAssistService = {
  async getHazardEnrichment(hazardId: string): Promise<HazardAiEnrichment> {
    return api.get<HazardAiEnrichment>(
      `/ai-assist/hazards/${encodeURIComponent(hazardId)}/enrichment`
    );
  },
};
