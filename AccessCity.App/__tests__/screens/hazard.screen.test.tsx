import React from 'react';
import { render, fireEvent, waitFor } from '@testing-library/react-native';
import HazardScreen from '@/app/(tabs)/hazard';
import { hazardsService } from '@/services/hazards.service';

jest.mock('@/services/hazards.service', () => ({
  hazardsService: {
    getHazards: jest.fn(() => Promise.resolve([])),
    getHazardById: jest.fn(() => Promise.resolve(null)),
  },
}));

describe('HazardScreen', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('loads hazards on focus and shows empty state', async () => {
    const { getByText, findByText } = render(<HazardScreen />);

    expect(getByText('Hazards')).toBeTruthy();

    await waitFor(() => {
      expect(hazardsService.getHazards).toHaveBeenCalled();
    });

    expect(await findByText('No hazards found for this status.')).toBeTruthy();
  });

  it('renders hazard cards when API returns data', async () => {
    jest.mocked(hazardsService.getHazards).mockResolvedValueOnce([
      {
        id: '1',
        title: 'Broken pavement',
        type: 'broken_pavement',
        latitude: 52.48,
        longitude: -1.89,
        description: 'Crack near curb.',
        status: 'Reported',
        locationText: 'Somewhere',
        reportedTime: 'Today',
      },
    ]);

    const { findByText } = render(<HazardScreen />);

    expect(await findByText('Broken pavement')).toBeTruthy();
  });

  it('filter pills switch reported / acknowledged / resolved', async () => {
    const { getByText } = render(<HazardScreen />);

    await waitFor(() => expect(hazardsService.getHazards).toHaveBeenCalled());

    fireEvent.press(getByText('Resolved'));

    await waitFor(() => {
      expect(hazardsService.getHazards).toHaveBeenCalledTimes(2);
    });
  });
});
