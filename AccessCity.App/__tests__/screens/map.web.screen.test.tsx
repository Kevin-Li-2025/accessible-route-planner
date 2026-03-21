import React from 'react';
import { render } from '@testing-library/react-native';
import MapPageWeb from '@/app/(tabs)/map.web';

describe('MapPageWeb', () => {
  it('renders web-only stub copy', () => {
    const { getByText } = render(<MapPageWeb />);
    expect(
      getByText('Map is available in the iOS and Android app.'),
    ).toBeTruthy();
    expect(
      getByText('Open this project in Expo Go on a device to use navigation and voice guidance.'),
    ).toBeTruthy();
  });
});
