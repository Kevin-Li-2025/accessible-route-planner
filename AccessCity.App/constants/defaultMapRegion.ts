/**
 * Default map focus: Birmingham pilot (UK). Override at build time via Expo public env.
 */
const envLat = Number(process.env.EXPO_PUBLIC_DEFAULT_MAP_LAT);
const envLng = Number(process.env.EXPO_PUBLIC_DEFAULT_MAP_LNG);

export const DEFAULT_CITY_NAME = 'Birmingham, UK';

export const DEFAULT_MAP_CENTER = {
  latitude: Number.isFinite(envLat) ? envLat : 52.4862,
  longitude: Number.isFinite(envLng) ? envLng : -1.8904,
} as const;

/** Mapbox / some helpers use [lng, lat]. */
export const DEFAULT_MAP_CENTER_LNG_LAT: [number, number] = [
  DEFAULT_MAP_CENTER.longitude,
  DEFAULT_MAP_CENTER.latitude,
];

export const DEFAULT_MAP_DELTA = {
  latitudeDelta: 0.05,
  longitudeDelta: 0.05,
} as const;
