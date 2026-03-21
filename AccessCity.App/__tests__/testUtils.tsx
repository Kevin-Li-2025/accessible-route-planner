import React from 'react';
import { AuthContext, AuthProvider, type AuthContextType } from '@/context/AuthContext';

export function renderWithAuthProvider(ui: React.ReactElement) {
  return <AuthProvider>{ui}</AuthProvider>;
}

export function createAuthWrapper(partial: Partial<AuthContextType>) {
  const defaults: AuthContextType = {
    user: null,
    isAuthenticated: false,
    isLoading: false,
    signIn: jest.fn(),
    signUp: jest.fn(),
    signOut: jest.fn(),
    ...partial,
  };

  return function Wrapper({ children }: { children: React.ReactNode }) {
    return <AuthContext.Provider value={defaults}>{children}</AuthContext.Provider>;
  };
}
