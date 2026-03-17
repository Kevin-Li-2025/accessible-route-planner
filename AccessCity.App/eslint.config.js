// https://docs.expo.dev/guides/using-eslint/
const { defineConfig } = require('eslint/config');
const expoConfig = require('eslint-config-expo/flat');

const expoCoreModules = [
  'expo-linear-gradient',
  'expo-location',
  'react-native-maps',
  'maplibre-gl',
  'maplibre-gl/dist/maplibre-gl.css',
];

module.exports = defineConfig([
  expoConfig,
  {
    files: ['**/*.{js,jsx,ts,tsx}'],
    settings: {
      'import/core-modules': expoCoreModules,
    },
  },
  {
    files: ['components/MapView/index.tsx'],
    rules: {
      '@typescript-eslint/no-require-imports': 'off',
    },
  },
  {
    ignores: ['dist/*'],
  },
]);
