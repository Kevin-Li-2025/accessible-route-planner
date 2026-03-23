import * as Sentry from '@sentry/react-native';

const dsn = process.env.EXPO_PUBLIC_SENTRY_DSN;

if (typeof dsn === 'string' && dsn.trim().length > 0) {
  Sentry.init({
    dsn: dsn.trim(),
    debug: __DEV__,
    tracesSampleRate: 0.2,
    enableAutoSessionTracking: true,
  });
}

export { Sentry };
