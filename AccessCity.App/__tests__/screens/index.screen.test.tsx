import React from 'react';
import { render, fireEvent, waitFor, act } from '@testing-library/react-native';
import { router } from 'expo-router';
import AuthScreen from '@/app/index';
import { renderWithAuthProvider } from '../testUtils';
import { authService } from '@/services/auth.service';

jest.mock('@/hooks/use-form-animation', () => ({
  useFormAnimation: () => ({
    shake: jest.fn(),
    shakeStyle: {},
  }),
}));

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

describe('AuthScreen (index)', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  /** Landing screen delays opacity with reanimated; advance timers so the form is interactable. */
  function renderAuth() {
    jest.useFakeTimers();
    const utils = render(renderWithAuthProvider(<AuthScreen />));
    act(() => {
      jest.advanceTimersByTime(5000);
    });
    jest.useRealTimers();
    return utils;
  }

  it('renders email/password and primary submit after intro animation', () => {
    const { getByPlaceholderText, getByTestId } = renderAuth();
    expect(getByPlaceholderText('Email Address')).toBeTruthy();
    expect(getByPlaceholderText('Password')).toBeTruthy();
    expect(getByTestId('index-auth-submit')).toBeTruthy();
  });

  it('switches to sign up tab and shows full name field', async () => {
    const { getByText, findByPlaceholderText } = renderAuth();
    fireEvent.press(getByText('Sign Up'));
    expect(await findByPlaceholderText('Full Name')).toBeTruthy();
  });

  it('signs in successfully', async () => {
    jest.mocked(authService.login).mockResolvedValue({
      token: 't',
      refreshToken: 'r',
      email: 'x@test.com',
      fullName: 'X',
    });

    const { getByPlaceholderText, getByTestId } = renderAuth();

    fireEvent.changeText(getByPlaceholderText('Email Address'), 'x@test.com');
    fireEvent.changeText(getByPlaceholderText('Password'), 'Password123!');
    fireEvent.press(getByTestId('index-auth-submit'));

    await waitFor(() => {
      expect(router.replace).toHaveBeenCalledWith('/(tabs)/map');
    });
  });

  it('opens forgot flow and submits email', async () => {
    const { getByText, getByPlaceholderText, findByText } = renderAuth();

    fireEvent.press(getByText('Forgot Password?'));
    fireEvent.changeText(getByPlaceholderText('Email Address'), 'reset@test.com');
    fireEvent.press(getByText('Send Reset Link'));

    await waitFor(() => {
      expect(authService.forgotPassword).toHaveBeenCalledWith('reset@test.com');
    });
    expect(
      await findByText('If your email is registered, you will receive a reset token.'),
    ).toBeTruthy();
  });
});
