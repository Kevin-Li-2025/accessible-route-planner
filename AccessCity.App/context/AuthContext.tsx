import React, { createContext, useContext, useState, useEffect, ReactNode } from 'react';
import { User } from '../models/auth';
import { authService } from '../services/auth.service';

export interface AuthContextType {
  user: User | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  signIn: (email: string, password: string) => Promise<void>;
  signUp: (email: string, password: string, fullName: string) => Promise<void>;
  signOut: () => Promise<void>;
}

/** Exported for tests that need a custom provider without bootstrapping SecureStore. */
export const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    // Check for existing session on mount
    const loadSession = async () => {
      try {
        const session = await authService.getSession();
        if (session) {
          setUser(session.user);
        }
      } catch (e) {
        console.error('Failed to load session', e);
      } finally {
        setIsLoading(false);
      }
    };
    loadSession();
  }, []);

  const signIn = async (email: string, password: string) => {
    const response = await authService.login({ email, password });
    setUser({ email: response.email, fullName: response.fullName });
  };

  const signUp = async (email: string, password: string, fullName: string) => {
    const response = await authService.register({ email, password, fullName });
    setUser({ email: response.email, fullName: response.fullName });
  };

  const signOut = async () => {
    try {
      await authService.logout();
    } finally {
      setUser(null);
    }
  };

  return (
    <AuthContext.Provider
      value={{
        user,
        isAuthenticated: !!user,
        isLoading,
        signIn,
        signUp,
        signOut,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}
