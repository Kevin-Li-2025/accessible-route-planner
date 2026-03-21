import React from 'react';
import { render, fireEvent, waitFor } from '@testing-library/react-native';
import { router } from 'expo-router';
import SignupScreen from '@/app/signup';
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

describe('SignupScreen', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('shows validation when mandatory fields missing', async () => {
    const { getByText, findByText } = render(renderWithAuthProvider(<SignupScreen />));
    fireEvent.press(getByText('Sign Up'));
    expect(await findByText('Please fill in all mandatory fields')).toBeTruthy();
  });

  it('shows error when passwords do not match', async () => {
    const { getByPlaceholderText, getByText, findByText } = render(
      renderWithAuthProvider(<SignupScreen />),
    );

    fireEvent.changeText(getByPlaceholderText('First Name'), 'A');
    fireEvent.changeText(getByPlaceholderText('Last Name'), 'B');
    fireEvent.changeText(getByPlaceholderText('Email Address'), 'a@b.com');
    fireEvent.changeText(getByPlaceholderText('Password'), 'onepass1');
    fireEvent.changeText(getByPlaceholderText('Confirm Password'), 'otherpass1');
    fireEvent.press(getByText('Sign Up'));

    expect(await findByText('Passwords do not match')).toBeTruthy();
  });

  it('registers and navigates to map', async () => {
    jest.mocked(authService.register).mockResolvedValue({
      token: 't',
      refreshToken: 'r',
      email: 'new@test.com',
      fullName: 'New User',
    });

    const { getByPlaceholderText, getByText } = render(renderWithAuthProvider(<SignupScreen />));

    fireEvent.changeText(getByPlaceholderText('First Name'), 'New');
    fireEvent.changeText(getByPlaceholderText('Last Name'), 'User');
    fireEvent.changeText(getByPlaceholderText('Email Address'), 'new@test.com');
    fireEvent.changeText(getByPlaceholderText('Password'), 'Password123!');
    fireEvent.changeText(getByPlaceholderText('Confirm Password'), 'Password123!');
    fireEvent.press(getByText('Sign Up'));

    await waitFor(() => {
      expect(authService.register).toHaveBeenCalledWith({
        email: 'new@test.com',
        password: 'Password123!',
        fullName: 'New User',
      });
      expect(router.replace).toHaveBeenCalledWith('/(tabs)/map');
    });
  });

  it('footer navigates to login', () => {
    const { getByText } = render(renderWithAuthProvider(<SignupScreen />));
    fireEvent.press(getByText('Log In'));
    expect(router.push).toHaveBeenCalledWith('/login');
  });
});
