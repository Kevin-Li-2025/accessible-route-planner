import React from 'react';
import { render } from '@testing-library/react-native';

jest.mock('@/components/MapView/MapScreen', () => {
  const React = require('react');
  const { Text } = require('react-native');
  return () => React.createElement(Text, { testID: 'map-screen' }, 'MapScreen');
});

import MapTabPage from '@/app/(tabs)/map';

describe('Map tab (platform entry)', () => {
  it('renders the native map screen in the default test platform', () => {
    const { getByTestId } = render(<MapTabPage />);
    expect(getByTestId('map-screen')).toBeTruthy();
  });
});
