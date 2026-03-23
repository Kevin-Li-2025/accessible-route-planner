import React from 'react';
import { render, fireEvent } from '@testing-library/react-native';
import * as Haptics from 'expo-haptics';
import { HapticTab } from '@/components/haptic-tab';

jest.mock('expo-haptics', () => ({
  impactAsync: jest.fn(() => Promise.resolve()),
  ImpactFeedbackStyle: { Light: 'Light' },
  notificationAsync: jest.fn(() => Promise.resolve()),
  NotificationFeedbackType: { Error: 1 },
}));

jest.mock('@react-navigation/elements', () => {
  const React = require('react');
  const { Pressable } = require('react-native');
  return {
    PlatformPressable: ({ onPressIn, children, ...rest }: any) => (
      <Pressable onPressIn={onPressIn} {...rest}>
        {children}
      </Pressable>
    ),
  };
});

describe('HapticTab', () => {
  const originalOs = process.env.EXPO_OS;

  afterEach(() => {
    process.env.EXPO_OS = originalOs;
  });

  it('fires light impact on iOS press in', () => {
    process.env.EXPO_OS = 'ios';

    const onPressIn = jest.fn();
    const { getByTestId } = render(
      <HapticTab testID="haptic-tab" onPressIn={onPressIn}>
        {null}
      </HapticTab>
    );

    fireEvent(getByTestId('haptic-tab'), 'pressIn');
    expect(Haptics.impactAsync).toHaveBeenCalledWith(Haptics.ImpactFeedbackStyle.Light);
    expect(onPressIn).toHaveBeenCalled();
  });
});
