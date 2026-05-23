import React from 'react';
import { render, waitFor } from '@testing-library/react-native';

jest.mock('@/components/MapView', () => function MockMapView(props: any) {
  const { Text, View } = require('react-native');
  return (
    <View>
      <Text>web-map</Text>
      <Text>{props.markers.length} markers</Text>
    </View>
  );
});

jest.mock('@/services/hazards.service', () => ({
  hazardsService: {
    getHazards: jest.fn(),
  },
}));

import MapPageWeb from '@/app/(tabs)/map.web';
import { hazardsService } from '@/services/hazards.service';

describe('MapPageWeb', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('loads backend hazards into the web map', async () => {
    jest.mocked(hazardsService.getHazards).mockResolvedValue([
      {
        id: 'h1',
        title: 'Blocked pavement',
        type: 'obstruction',
        latitude: 52.4862,
        longitude: -1.8904,
        description: 'Path blocked',
        status: 'Reported',
        locationText: 'Birmingham',
        reportedTime: 'Today',
      },
    ]);

    const { getByText } = render(<MapPageWeb />);

    expect(getByText('web-map')).toBeTruthy();
    await waitFor(() => expect(getByText('1 markers')).toBeTruthy());
    expect(getByText('1 live hazards')).toBeTruthy();
  });
});
