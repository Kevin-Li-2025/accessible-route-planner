import React from 'react';
import { render, fireEvent, waitFor } from '@testing-library/react-native';
import { router } from 'expo-router';
import LoginScreen from '@/app/login';
import { renderWithAuthProvider } from '../testUtils';
import { authService } from '@/services/auth.service';

jest.mock('@/services/auth.service', () => ({
  authService: {
    clearSession: jest.fn(() => Promise.resolve()),
    getSession: jest.fn(() => Promise.resolve(null)),
    logout: jest.fn(() => Promise.resolve()),
    forgotPassword: jest.fn(() => Promise.resolve()),
    login: jest.fn(),
    register: jest.fn(),
  },
}));

describe('LoginScreen', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('shows validation when fields are empty', async () => {
    const { getByText, findByText } = render(renderWithAuthProvider(<LoginScreen />));

    fireEvent.press(getByText('Log In'));

    expect(await findByText('All fields are mandatory')).toBeTruthy();
  });

  it('calls sign in and navigates to map on success', async () => {
    jest.mocked(authService.login).mockResolvedValue({
      token: 'access',
      refreshToken: 'refresh',
      email: 'user@test.com',
      fullName: 'Test User',
    });

    const { getByPlaceholderText, getByText } = render(renderWithAuthProvider(<LoginScreen />));

    fireEvent.changeText(getByPlaceholderText('Email Address'), 'user@test.com');
    fireEvent.changeText(getByPlaceholderText('Password'), 'Password123!');
    fireEvent.press(getByText('Log In'));

    await waitFor(() => {
      expect(authService.login).toHaveBeenCalledWith({
        email: 'user@test.com',
        password: 'Password123!',
      });
      expect(router.replace).toHaveBeenCalledWith('/(tabs)/map');
    });
  });

  it('navigates to forgot password', () => {
    const { getByText } = render(renderWithAuthProvider(<LoginScreen />));
    fireEvent.press(getByText('Forgot Password?'));
    expect(router.push).toHaveBeenCalledWith('/forgot-password');
  });

  it('navigates to signup from footer', () => {
    const { getByText } = render(renderWithAuthProvider(<LoginScreen />));
    fireEvent.press(getByText('Sign Up'));
    expect(router.push).toHaveBeenCalledWith('/signup');
  });
});
