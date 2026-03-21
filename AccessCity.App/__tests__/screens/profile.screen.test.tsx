import React from 'react';
import { render, fireEvent, waitFor } from '@testing-library/react-native';
import { router } from 'expo-router';
import ProfileScreen from '@/app/(tabs)/profile';
import { createAuthWrapper } from '../testUtils';

describe('ProfileScreen', () => {
  const mockSignOut = jest.fn(() => Promise.resolve());

  it('renders user name and email', () => {
    const Wrapper = createAuthWrapper({
      user: { email: 'u@test.com', fullName: 'Unit Test' },
      isAuthenticated: true,
      isLoading: false,
      signOut: mockSignOut,
    });

    const { getByText } = render(<ProfileScreen />, { wrapper: Wrapper });

    expect(getByText('Unit Test')).toBeTruthy();
    expect(getByText('u@test.com')).toBeTruthy();
    expect(getByText('Edit Profile')).toBeTruthy();
  });

  it('logs out and navigates to login', async () => {
    const Wrapper = createAuthWrapper({
      user: { email: 'u@test.com', fullName: 'U' },
      isAuthenticated: true,
      isLoading: false,
      signOut: mockSignOut,
    });

    const { getByText } = render(<ProfileScreen />, { wrapper: Wrapper });
    fireEvent.press(getByText('Log Out'));

    await waitFor(() => {
      expect(mockSignOut).toHaveBeenCalled();
      expect(router.replace).toHaveBeenCalledWith('/login');
    });
  });
});
