import React from 'react';
import { render, fireEvent, waitFor } from '@testing-library/react-native';
import AdminHazardReport from '@/components/MapView/AdminHazardReport';
import { api } from '@/services/api';

jest.mock('@/services/api', () => ({
  api: {
    get: jest.fn(),
    post: jest.fn(),
    patch: jest.fn(),
  },
}));

jest.mock('@/services/aiAssist.service', () => ({
  aiAssistService: {
    getHazardEnrichment: jest.fn(),
  },
}));

import { aiAssistService } from '@/services/aiAssist.service';

const enrichment = {
  hazardId: 'h1',
  forRouteDecision: false,
  provider: 'local-rules',
  generatedAtUtc: new Date().toISOString(),
  text: {
    normalizedDescription: 'No lights.',
    suggestedType: 'low_lighting',
    suggestedSeverity: 'medium',
    confidence: 0.7,
    adminSummary: 'low_lighting report',
    tags: ['visibility'],
  },
  duplicateSuggestions: [],
  missingOsmAttributeCandidates: [
    {
      attribute: 'lit',
      value: 'no',
      confidence: 0.64,
      evidence: 'Report indicates missing or failed lighting.',
      source: 'user_report_text',
      canAutoApply: false,
    },
  ],
  guardrails: [],
};

describe('AdminHazardReport', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest.mocked(aiAssistService.getHazardEnrichment).mockResolvedValue(enrichment);
  });

  it('shows empty state when no pending hazards', async () => {
    jest.mocked(api.get).mockImplementation(async (path: string) => {
      if (path === '/hazards') return [];
      if (path === '/dashboard/summary') return { pendingAlerts: 0 };
      return {};
    });

    const { findByText } = render(<AdminHazardReport />);

    expect(await findByText('No pending reports')).toBeTruthy();
  });

  it('lists pending reports and opens details', async () => {
    jest.mocked(api.get).mockImplementation(async (path: string) => {
      if (path === '/hazards') {
        return [
          {
              id: 'h1',
              status: 'reported',
              type: 'Lighting',
              title: 'Dark alley',
              description: 'No lights.',
              locationName: 'Main St',
              reportedAt: new Date().toISOString(),
              reporterName: 'Alex',
          },
        ];
      }
      if (path === '/hazards/h1') {
        return {
          id: 'h1',
          category: 'Lighting',
          status: 'pending',
          title: 'Dark alley',
          description: 'No lights.',
          locationName: 'Main St',
          createdAt: new Date().toISOString(),
          reporter: { name: 'Alex', email: 'a@b.com', verified: false },
            };
          }
      if (path === '/dashboard/summary') return { pendingAlerts: 1 };
      return {};
    });

    const { findByText, getByText } = render(<AdminHazardReport />);

    expect(await findByText('Dark alley')).toBeTruthy();
    fireEvent.press(getByText('Dark alley'));

    expect(await findByText('Report Details')).toBeTruthy();
    expect(await findByText('Review signals')).toBeTruthy();
    expect(await findByText('Low Lighting')).toBeTruthy();
    expect(await findByText('Lit')).toBeTruthy();
    expect(await findByText('Review Report')).toBeTruthy();
  });

  it('submits approve decision and shows success', async () => {
    jest.mocked(api.get).mockImplementation(async (path: string) => {
      if (path === '/hazards') {
        return [
          {
            id: 'h1',
            status: 'underreview',
            type: 'Obstruction',
            title: 'Blocked path',
            locationName: 'Side Rd',
            reportedAt: new Date().toISOString(),
          },
        ];
      }
      if (path === '/hazards/h1') {
        return {
          id: 'h1',
          category: 'Obstruction',
          status: 'pending',
          title: 'Blocked path',
          description: 'Barrier.',
          locationName: 'Side Rd',
          createdAt: new Date().toISOString(),
        };
      }
      if (path === '/dashboard/summary') return { pendingAlerts: 1 };
      return {};
    });
    jest.mocked(api.patch).mockResolvedValue({});

    const { findByText, getByText } = render(<AdminHazardReport />);

    expect(await findByText('Blocked path')).toBeTruthy();
    fireEvent.press(getByText('Blocked path'));
    expect(await findByText('Review Report')).toBeTruthy();
    fireEvent.press(getByText('Review Report'));

    expect(await findByText('Approve Report')).toBeTruthy();
    fireEvent.press(getByText('Approve Report'));

    await waitFor(() => {
      expect(api.patch).toHaveBeenCalledWith('/hazards/h1', 1);
    });

    expect(await findByText('Report Approved Successfully')).toBeTruthy();
  });
});
