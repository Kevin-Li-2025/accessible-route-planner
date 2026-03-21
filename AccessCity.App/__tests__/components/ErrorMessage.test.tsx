import React from 'react';
import { render } from '@testing-library/react-native';
import { ErrorMessage } from '@/components/ErrorMessage';

describe('ErrorMessage', () => {
  it('renders nothing when not visible', () => {
    const { queryByText } = render(
      <ErrorMessage visible={false} message="Should not show" />,
    );
    expect(queryByText('Should not show')).toBeNull();
  });

  it('renders message when visible', () => {
    const { getByText } = render(<ErrorMessage visible message="Invalid login" />);
    expect(getByText('Invalid login')).toBeTruthy();
  });
});
