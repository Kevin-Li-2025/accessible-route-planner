import React from 'react';
import { render, fireEvent } from '@testing-library/react-native';
import RouteInfoCard from '@/components/MapView/RouteInfoCard';

describe('RouteInfoCard', () => {
  it('compact state shows Route action', () => {
    const onPressRoute = jest.fn();
    const onStartNavigation = jest.fn();

    const { getByText } = render(
      <RouteInfoCard
        visible={false}
        travelTime=""
        distance=""
        safetyScore=""
        onPressRoute={onPressRoute}
        onStartNavigation={onStartNavigation}
      />,
    );

    fireEvent.press(getByText('Route'));
    expect(onPressRoute).toHaveBeenCalled();
  });

  it('expanded state shows safety labels and start navigation', () => {
    const onPressRoute = jest.fn();
    const onStartNavigation = jest.fn();

    const { getByText } = render(
      <RouteInfoCard
        visible
        travelTime="12 min"
        distance="2.1 km"
        safetyScore="85%"
        performance={{
          searchMilliseconds: 0.42,
          nodesExpanded: 1234,
          riskLookups: 5678,
        }}
        onPressRoute={onPressRoute}
        onStartNavigation={onStartNavigation}
      />,
    );

    expect(getByText('Good')).toBeTruthy();
    expect(getByText('Low')).toBeTruthy();
    expect(getByText('Engine diagnostics')).toBeTruthy();
    expect(getByText('0.42 ms')).toBeTruthy();
    expect(getByText('1,234')).toBeTruthy();
    expect(getByText('5,678')).toBeTruthy();

    fireEvent.press(getByText('Start Navigation'));
    expect(onStartNavigation).toHaveBeenCalled();
  });
});
