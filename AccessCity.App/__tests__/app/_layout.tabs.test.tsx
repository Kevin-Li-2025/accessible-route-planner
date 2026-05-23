import React from 'react';
import { render } from '@testing-library/react-native';
import { router } from 'expo-router';

const capturedScreens: Record<string, unknown>[] = [];

jest.mock('expo-router', () => {
  const React = require('react');
  function Tabs({ children }: { children: React.ReactNode }) {
    return <>{children}</>;
  }
  Tabs.Screen = (props: Record<string, unknown>) => {
    capturedScreens.push(props);
    return null;
  };
  return {
    router: {
      push: jest.fn(),
      replace: jest.fn(),
      back: jest.fn(),
    },
    Tabs,
    useRouter: () => ({
      push: jest.fn(),
      replace: jest.fn(),
      back: jest.fn(),
    }),
  };
});

jest.mock('@/components/haptic-tab', () => ({
  HapticTab: ({ children, ...rest }: { children?: React.ReactNode }) => {
    const { Pressable } = require('react-native');
    return <Pressable {...rest}>{children}</Pressable>;
  },
}));

jest.mock('@/components/ui/icon-symbol', () => ({
  IconSymbol: () => null,
}));

jest.mock('@/hooks/use-color-scheme', () => ({
  useColorScheme: () => 'light',
}));

import TabLayout from '@/app/(tabs)/_layout';

describe('TabLayout', () => {
  beforeEach(() => {
    capturedScreens.length = 0;
    jest.clearAllMocks();
  });

  it('registers report tabPress to open the user report flow', () => {
    render(<TabLayout />);

    const report = capturedScreens.find((p) => p.name === 'report/reportpage') as
      | { listeners?: { tabPress?: (e: { preventDefault: () => void }) => void } }
      | undefined;

    expect(report).toBeDefined();
    const e = { preventDefault: jest.fn() };
    report!.listeners!.tabPress!(e);
    expect(e.preventDefault).toHaveBeenCalled();
    expect(router.push).toHaveBeenCalledWith('/report/reportpage');
  });

  it('includes map and profile screens', () => {
    render(<TabLayout />);
    const names = capturedScreens.map((p) => p.name);
    expect(names).toContain('map');
    expect(names).toContain('profile');
    expect(names).toContain('hazard');
  });
});
