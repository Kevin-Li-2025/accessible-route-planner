import React from 'react';
import { render } from '@testing-library/react-native';
import { ThemedText } from '@/components/themed-text';

jest.mock('@/hooks/use-theme-color', () => ({
  useThemeColor: () => '#111111',
}));

describe('ThemedText', () => {
  it('renders default body text', () => {
    const { getByText } = render(<ThemedText>Hello AccessCity</ThemedText>);
    expect(getByText('Hello AccessCity')).toBeTruthy();
  });

  it('applies title style type', () => {
    const { getByText } = render(<ThemedText type="title">Title</ThemedText>);
    const el = getByText('Title');
    expect(el).toBeTruthy();
  });
});
