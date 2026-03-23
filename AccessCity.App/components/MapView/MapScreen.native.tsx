import React, { useEffect, useState, useRef } from 'react';
import MapView from 'react-native-maps';
import { StyleSheet, View, Text, TouchableOpacity, Alert } from 'react-native';
import * as Location from 'expo-location';
import { Ionicons } from '@expo/vector-icons';
import { router, useGlobalSearchParams } from 'expo-router';

import SearchBar, { type SearchSuggestion } from './SearchBar';
import RouteInfoCard from './RouteInfoCard';
import HazardPreviewCard from './HazardPreviewCard';
import HazardDetailsModal from './HazardDetailsModal';
import {
  DEFAULT_MAP_CENTER,
  DEFAULT_MAP_DELTA,
} from '../../constants/defaultMapRegion';
import FilterModal from './FilterModal';
import MapCanvas from './MapCanvas';
import {
  runVoiceGuidance,
  stopVoiceGuidance,
  stepsFromApi,
  stepsFromCoordinates,
  type VoiceStep,
} from './voiceGuidance';
import { api } from '../../services/api';
import { hazardsService } from '../../services/hazards.service';

import {
  Coordinate,
  Hazard,
  RouteFilters,
} from './MapTypes';

/**
 * Maps UI `RouteFilters` to `RouteRequest` fields on the API
 * (`preferences`, `profile`, `safetyWeight` — see AccessCity.API.Models.RouteRequest).
 */
function buildRouteRequestOptions(routeFilters: RouteFilters): {
  profile: string;
  preferences: string[];
  safetyWeight: number;
} {
  const preferences: string[] = [];

  if (routeFilters.avoidSteepHills) {
    preferences.push('avoid-steep-hills');
  }
  if (routeFilters.avoidReportedHazards) {
    preferences.push('avoid-reported-hazards');
  }
  if (routeFilters.preferWellLitStreets) {
    preferences.push('low-light-penalty');
  }

  const profile = routeFilters.wheelchairAccessible ? 'manual-wheelchair' : 'standard';

  const mid = (routeFilters.minSafetyScore + routeFilters.maxSafetyScore) / 2;
  const safetyWeight = Math.min(1, Math.max(0, mid / 100));

  return { profile, preferences, safetyWeight };
}

async function fetchHazardsApi() {
  try {
    const [reported, acknowledged] = await Promise.all([
      api.get<any[]>('/hazards?status=Reported', { skipAuth: true }),
      api.get<any[]>('/hazards?status=Acknowledged', { skipAuth: true })
    ]);
    const arr1 = Array.isArray(reported) ? reported : [];
    const arr2 = Array.isArray(acknowledged) ? acknowledged : [];
    return [...arr1, ...arr2];
  } catch (error) {
    console.error('Failed to fetch map hazards:', error);
    return [];
  }
}

export default function MapScreen() {
  type GeocodingResult = {
    place_id?: number | string;
    lat?: string | number;
    lon?: string | number;
    latitude?: string | number;
    longitude?: string | number;
    lng?: string | number;
    x?: string | number;
    y?: string | number;
    display_name?: string;
    name?: string;
  };

  type AutocompleteSuggestion = SearchSuggestion & {
    result: GeocodingResult;
  };

  /** Geocoding search may return a bare array or a wrapped payload depending on the API. */
  type GeocodingSearchResponse =
    | GeocodingResult[]
    | { data?: GeocodingResult[]; results?: GeocodingResult[] };

  const mapRef = useRef<MapView | null>(null);
  const locationSubscriptionRef = useRef<Location.LocationSubscription | null>(null);
  const navigationModeRef = useRef(false);
  const routeStepsRef = useRef<VoiceStep[]>([]);
  const lastSpokenStepRef = useRef(-1);
  const suggestionRequestIdRef = useRef(0);
  const skipNextSuggestionFetchRef = useRef(false);
  const routeCoordinatesRef = useRef<Coordinate[]>([]);
  const isReroutingRef = useRef(false);
  const { openReportModal } = useGlobalSearchParams<{ openReportModal?: string }>();

  const [currentLocation, setCurrentLocation] = useState<Coordinate | null>(null);
  const [destinationText, setDestinationText] = useState('');
  const [destination, setDestination] = useState<Coordinate | null>(null);
  const [searchSuggestions, setSearchSuggestions] = useState<AutocompleteSuggestion[]>([]);
  const [routeCoordinates, setRouteCoordinates] = useState<Coordinate[]>([]);
  const [routeSteps, setRouteSteps] = useState<VoiceStep[]>([]);
  const [travelTime, setTravelTime] = useState('');
  const [distance, setDistance] = useState('');
  const [safetyScore, setSafetyScore] = useState('');

  const [hazards, setHazards] = useState<Hazard[]>([]);

  const [filterModalVisible, setFilterModalVisible] = useState(false);

  const [routeFilters, setRouteFilters] = useState<RouteFilters>({
    avoidSteepHills: false,
    wheelchairAccessible: false,
    avoidReportedHazards: false,
    preferWellLitStreets: false,
    minSafetyScore: 0,
    maxSafetyScore: 100,
  });

  const [selectedHazard, setSelectedHazard] = useState<Hazard | null>(null);
  const [hazardPreviewVisible, setHazardPreviewVisible] = useState(false);
  const [hazardDetailsVisible, setHazardDetailsVisible] = useState(false);

  const [navigationMode, setNavigationMode] = useState(false);
  const [routeInfoVisible, setRouteInfoVisible] = useState(false);
  const [heading, setHeading] = useState(0);

  useEffect(() => {
    navigationModeRef.current = navigationMode;
  }, [navigationMode]);

  useEffect(() => {
    routeStepsRef.current = routeSteps;
  }, [routeSteps]);

  useEffect(() => {
    routeCoordinatesRef.current = routeCoordinates;
  }, [routeCoordinates]);

  useEffect(() => {
    if (!openReportModal) return;
    router.push('/report/reportpage');
  }, [openReportModal]);

  function mapBackendHazardToFrontend(item: any): Hazard {
    const rawType = String(item.type ?? item.hazardType ?? item.category ?? '').toLowerCase();
    const rawStatus = String(item.status ?? '').toLowerCase();

    const longitude = Number(
      item.longitude ??
      item.lng ??
      item.lon ??
      item.x ??
      item.location?.x ??
      item.coordinates?.[0] ??
      0
    );

    const latitude = Number(
      item.latitude ??
      item.lat ??
      item.y ??
      item.location?.y ??
      item.coordinates?.[1] ??
      0
    );

    return {
      id: String(item.id ?? `${latitude}-${longitude}-${Date.now()}`),
      title: item.title ?? item.name ?? item.type ?? item.hazardType ?? 'Hazard',

      type:
        rawType.includes('light')
          ? 'lighting'
          : 'wheelchair',

      latitude,
      longitude,

      description: item.description ?? 'No description available.',

      status:
        rawStatus === 'resolved' || rawStatus === 'acknowledged'
          ? 'Acknowledged'
          : 'Pending',

      locationText:
        item.locationText ??
        item.locationName ??
        item.address ??
        item.location ??
        (latitude && longitude ? `${latitude}, ${longitude}` : 'Unknown location'),

      reportedTime:
        item.reportedTime ??
        item.reportedAt ??
        item.createdAt ??
        'Recently reported',
    };
  }

  async function loadHazards() {
    try {
      const data = await fetchHazardsApi();
      const rawHazards = Array.isArray(data) ? data : [];

      const mappedHazards = rawHazards
        .map(mapBackendHazardToFrontend)
        .filter(
          (item) =>
            !Number.isNaN(item.latitude) &&
            !Number.isNaN(item.longitude) &&
            item.latitude !== 0 &&
            item.longitude !== 0
        );

      setHazards(mappedHazards);
    } catch (error) {
      console.error('Error fetching hazards:', error);
      Alert.alert('Hazard error', 'Could not load hazards from backend.');
    }
  }

  function formatDistance(distanceInMeters: number) {
    if (!Number.isFinite(distanceInMeters)) return '--';

    if (distanceInMeters >= 1000) {
      return `${(distanceInMeters / 1000).toFixed(1)} km`;
    }

    return `${Math.round(distanceInMeters)} m`;
  }

  function formatTravelTimeFromBackend(data: any) {
    const rawSeconds =
      Number(data?.estimatedTime) ||
      Number(data?.durationSeconds) ||
      Number(data?.duration) ||
      0;

    if (!Number.isFinite(rawSeconds) || rawSeconds <= 0) {
      return 'Route ready';
    }

    return `${Math.max(1, Math.round(rawSeconds / 60))} min`;
  }

  function formatSafetyScoreFromBackend(data: any) {
    const rawScore =
      data?.safetyScore ??
      data?.score ??
      data?.safety_rating;

    const numericScore = Number(rawScore);

    if (!Number.isFinite(numericScore)) return '--';

    if (numericScore <= 1) {
      return `${(numericScore * 100).toFixed(0)}%`;
    }

    return `${Math.round(numericScore)}%`;
  }

  function getBearing(start: Coordinate, end: Coordinate) {
    const startLat = (start.latitude * Math.PI) / 180;
    const startLng = (start.longitude * Math.PI) / 180;
    const endLat = (end.latitude * Math.PI) / 180;
    const endLng = (end.longitude * Math.PI) / 180;

    const dLng = endLng - startLng;

    const y = Math.sin(dLng) * Math.cos(endLat);
    const x =
      Math.cos(startLat) * Math.sin(endLat) -
      Math.sin(startLat) * Math.cos(endLat) * Math.cos(dLng);

    const bearing = (Math.atan2(y, x) * 180) / Math.PI;
    return (bearing + 360) % 360;
  }

  function getDistanceInMeters(a: Coordinate, b: Coordinate) {
    const toRad = (value: number) => (value * Math.PI) / 180;

    const earthRadius = 6371000;
    const dLat = toRad(b.latitude - a.latitude);
    const dLng = toRad(b.longitude - a.longitude);

    const lat1 = toRad(a.latitude);
    const lat2 = toRad(b.latitude);

    const haversine =
      Math.sin(dLat / 2) * Math.sin(dLat / 2) +
      Math.cos(lat1) * Math.cos(lat2) *
      Math.sin(dLng / 2) * Math.sin(dLng / 2);

    const c = 2 * Math.atan2(Math.sqrt(haversine), Math.sqrt(1 - haversine));
    return earthRadius * c;
  }

  function getMinDistanceToRoute(current: Coordinate, coords: Coordinate[]) {
    if (coords.length === 0) return Number.MAX_VALUE;

    let minDistance = Number.MAX_VALUE;

    coords.forEach((point) => {
      const distanceValue = getDistanceInMeters(current, point);
      if (distanceValue < minDistance) {
        minDistance = distanceValue;
      }
    });

    return minDistance;
  }

  function getNavigationHeading(current: Coordinate) {
    if (routeCoordinates.length < 2) {
      return heading >= 0 ? heading : 0;
    }

    let closestIndex = 0;
    let closestDistance = Number.MAX_VALUE;

    routeCoordinates.forEach((point, index) => {
      const latDiff = point.latitude - current.latitude;
      const lngDiff = point.longitude - current.longitude;
      const distanceValue = latDiff * latDiff + lngDiff * lngDiff;

      if (distanceValue < closestDistance) {
        closestDistance = distanceValue;
        closestIndex = index;
      }
    });

    const nextIndex =
      closestIndex < routeCoordinates.length - 1 ? closestIndex + 1 : closestIndex;

    if (nextIndex === closestIndex) {
      return heading >= 0 ? heading : 0;
    }

    return getBearing(routeCoordinates[closestIndex], routeCoordinates[nextIndex]);
  }

  function getEstimatedArrivalTime() {
    if (!travelTime || !travelTime.includes('min')) return '--';

    const minutes = parseInt(travelTime.replace(/\D/g, ''), 10);

    if (Number.isNaN(minutes)) return '--';

    const now = new Date();
    now.setMinutes(now.getMinutes() + minutes);

    const hours = now.getHours();
    const mins = now.getMinutes().toString().padStart(2, '0');

    return `${hours}:${mins}`;
  }

  function resetRouteState(clearSearchText = false) {
    setNavigationMode(false);
    navigationModeRef.current = false;
    setRouteInfoVisible(false);

    setDestination(null);
    setRouteCoordinates([]);
    setRouteSteps([]);
    setTravelTime('');
    setDistance('');
    setSafetyScore('');

    if (clearSearchText) {
      setDestinationText('');
    }

    setHazardPreviewVisible(false);
    setHazardDetailsVisible(false);
    setSelectedHazard(null);

    if (currentLocation) {
      mapRef.current?.animateCamera(
        {
          center: currentLocation,
          pitch: 0,
          heading: 0,
          zoom: 15,
          altitude: 1200,
        },
        { duration: 500 }
      );
    }
  }

  function handleClearSearch() {
    resetRouteState(true);
    setSearchSuggestions([]);
  }

  useEffect(() => {
    let isMounted = true;

    async function startLocationTracking() {
      try {
        const { status } = await Location.requestForegroundPermissionsAsync();

        if (status !== 'granted') {
          Alert.alert('Permission denied', 'Location permission is required.');
          return;
        }

        const location = await Location.getCurrentPositionAsync({
          accuracy: Location.Accuracy.High,
        });

        const firstCoordinate = {
          latitude: location.coords.latitude,
          longitude: location.coords.longitude,
        };

        if (!isMounted) return;

        setCurrentLocation(firstCoordinate);

        if (
          typeof location.coords.heading === 'number' &&
          location.coords.heading >= 0
        ) {
          setHeading(location.coords.heading);
        }

        mapRef.current?.animateToRegion(
          {
            latitude: firstCoordinate.latitude,
            longitude: firstCoordinate.longitude,
            latitudeDelta: 0.02,
            longitudeDelta: 0.02,
          },
          800
        );

        const subscription = await Location.watchPositionAsync(
          {
            accuracy: Location.Accuracy.BestForNavigation,
            timeInterval: 1000,
            distanceInterval: 1,
          },
          (updatedLocation) => {
            const newCoordinate = {
              latitude: updatedLocation.coords.latitude,
              longitude: updatedLocation.coords.longitude,
            };

            console.log(
              'LIVE LOCATION:',
              newCoordinate,
              'accuracy:',
              updatedLocation.coords.accuracy
            );

            setCurrentLocation(newCoordinate);

            if (
              typeof updatedLocation.coords.heading === 'number' &&
              updatedLocation.coords.heading >= 0
            ) {
              setHeading(updatedLocation.coords.heading);
            }

            if (navigationModeRef.current) {
              const nextHeading = getNavigationHeading(newCoordinate);

              mapRef.current?.animateCamera(
                {
                  center: newCoordinate,
                  pitch: 60,
                  heading: nextHeading,
                  zoom: 18,
                  altitude: 400,
                },
                { duration: 700 }
              );

              runVoiceGuidance(
                newCoordinate.latitude,
                newCoordinate.longitude,
                routeStepsRef.current,
                lastSpokenStepRef
              );

              void maybeReroute(newCoordinate);
            }
          }
        );

        locationSubscriptionRef.current = subscription;
      } catch (error) {
        console.error('Error getting location:', error);
        Alert.alert('Error', 'Could not get current location.');
      }
    }

    startLocationTracking();
    loadHazards();

    return () => {
      isMounted = false;
      locationSubscriptionRef.current?.remove();
      locationSubscriptionRef.current = null;
    };
    // Intentionally run once on mount: re-subscribing GPS on every render would leak / thrash; callbacks use refs where needed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function parseGeocodingResult(result: GeocodingResult) {
    const latRaw = result.lat ?? result.latitude ?? result.y;
    const lonRaw = result.lon ?? result.lng ?? result.longitude ?? result.x;

    const lat = parseFloat(String(latRaw));
    const lon = parseFloat(String(lonRaw));

    if (Number.isNaN(lat) || Number.isNaN(lon)) {
      return null;
    }

    const displayName = String(result.display_name ?? result.name ?? '').trim();
    const displayParts = displayName
      .split(',')
      .map((part) => part.trim())
      .filter(Boolean);

    return {
      id: String(result.place_id ?? displayName ?? `${lat},${lon}`),
      latitude: lat,
      longitude: lon,
      title: displayParts[0] || displayName || 'Selected destination',
      subtitle:
        displayParts.length > 1
          ? displayParts.slice(1).join(', ')
          : undefined,
      displayName: displayName || 'Selected destination',
    };
  }

  function applyDestinationSelection(selectedResult: GeocodingResult) {
    const parsedResult = parseGeocodingResult(selectedResult);

    if (!parsedResult) {
      Alert.alert('Search error', 'Invalid coordinates returned from geocoding.');
      return;
    }

    setNavigationMode(false);
    navigationModeRef.current = false;
    setRouteInfoVisible(false);
    setRouteCoordinates([]);
    setRouteSteps([]);
    setTravelTime('');
    setDistance('');
    setSafetyScore('');
    setSearchSuggestions([]);
    stopVoiceGuidance();
    lastSpokenStepRef.current = -1;

    skipNextSuggestionFetchRef.current = true;
    setDestinationText(parsedResult.displayName);
    setDestination({
      latitude: parsedResult.latitude,
      longitude: parsedResult.longitude,
    });

    mapRef.current?.animateToRegion(
      {
        latitude: parsedResult.latitude,
        longitude: parsedResult.longitude,
        latitudeDelta: 0.02,
        longitudeDelta: 0.02,
      },
      700
    );
  }

  async function searchLocation(
    query: string,
    showErrorAlert = true
  ): Promise<GeocodingResult[]> {
    try {
      const raw = await api.get<GeocodingSearchResponse>(
        `/geocoding/search?query=${encodeURIComponent(query)}`,
        {
          // TODO: Change skipAuth to false if geocoding later becomes a protected endpoint.
          skipAuth: true,
        }
      );

      console.log('Geocoding results:', raw);

      if (Array.isArray(raw)) return raw;
      if (Array.isArray(raw.data)) return raw.data;
      if (Array.isArray(raw.results)) return raw.results;

      return [];
    } catch (error) {
      console.error('Geocoding error:', error);
      if (showErrorAlert) {
        Alert.alert('Search error', 'Could not search for this location.');
      }
      return [];
    }
  }

  useEffect(() => {
    const trimmedText = destinationText.trim();

    if (!trimmedText) {
      setSearchSuggestions([]);
      return;
    }

    if (skipNextSuggestionFetchRef.current) {
      skipNextSuggestionFetchRef.current = false;
      setSearchSuggestions([]);
      return;
    }

    const timeoutId = setTimeout(async () => {
      const requestId = suggestionRequestIdRef.current + 1;
      suggestionRequestIdRef.current = requestId;

      const results = await searchLocation(trimmedText, false);

      if (suggestionRequestIdRef.current !== requestId) {
        return;
      }

      const suggestions = results
        .map((result: GeocodingResult) => {
          const parsedResult = parseGeocodingResult(result);
          if (!parsedResult) return null;

          const suggestion: AutocompleteSuggestion = {
            id: parsedResult.id,
            title: parsedResult.title,
            subtitle: parsedResult.subtitle,
            result,
          };

          return suggestion;
        })
        .filter((item): item is AutocompleteSuggestion => item !== null);

      setSearchSuggestions(suggestions);
    }, 350);

    return () => clearTimeout(timeoutId);
    // Only destinationText should retrigger debounced fetch; helpers are stable enough for this screen.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [destinationText]);

  async function handleSetDestination() {
    const trimmedText = destinationText.trim();

    if (!trimmedText) {
      Alert.alert('Missing destination', 'Please enter a destination.');
      return;
    }

    const results = await searchLocation(trimmedText);

    if (!results || results.length === 0) {
      Alert.alert('No results', 'No matching location was found.');
      return;
    }

    const firstResult = results[0];
    if (!firstResult || !parseGeocodingResult(firstResult)) {
      Alert.alert(
        firstResult ? 'Search error' : 'No results',
        firstResult
          ? 'Invalid coordinates returned from geocoding.'
          : 'No matching location was found.'
      );
      return;
    }

    applyDestinationSelection(firstResult);
  }

  function handleSuggestionPress(suggestion: SearchSuggestion) {
    const matchingSuggestion = searchSuggestions.find((item) => item.id === suggestion.id);

    if (!matchingSuggestion) {
      return;
    }

    applyDestinationSelection(matchingSuggestion.result);
  }

  async function fetchRouteFromBackend(startCoordinate?: Coordinate) {
    const routeStart = startCoordinate ?? currentLocation;

    if (!routeStart || !destination) {
      Alert.alert('Missing data', 'Current location or destination is missing.');
      return false;
    }

    try {
      const { profile, preferences, safetyWeight } = buildRouteRequestOptions(routeFilters);

      const data = await api.post<any>(
        '/routing/safe-path',
        {
          start: {
            x: routeStart.longitude,
            y: routeStart.latitude,
          },
          end: {
            x: destination.longitude,
            y: destination.latitude,
          },
          safetyWeight,
          profile,
          preferences,
        },
        {
          skipAuth: false,
        }
      );

      console.log('Route API data:', data);

      const rawCoordinates =
        data?.path?.coordinates ||
        data?.route?.coordinates ||
        data?.geometry?.coordinates ||
        data?.coordinates ||
        data?.path ||
        [];

      const coords = Array.isArray(rawCoordinates)
        ? rawCoordinates
            .map((item: any) => ({
              latitude: Number(item?.[1] ?? item?.latitude),
              longitude: Number(item?.[0] ?? item?.longitude),
            }))
            .filter(
              (item) =>
                !Number.isNaN(item.latitude) &&
                !Number.isNaN(item.longitude)
            )
        : [];

      if (coords.length < 2) {
        console.error('Invalid route coordinates:', rawCoordinates);
        Alert.alert('Route error', 'Backend returned an invalid route.');
        return false;
      }

      const apiSteps = stepsFromApi(data?.steps);
      setRouteSteps(apiSteps.length > 0 ? apiSteps : stepsFromCoordinates(coords));
      setRouteCoordinates(coords);

      const distanceValue = Number(
        data?.distance ??
        data?.distanceMeters ??
        data?.totalDistance ??
        0
      );

      setTravelTime(formatTravelTimeFromBackend(data));
      setDistance(Number.isFinite(distanceValue) && distanceValue > 0 ? formatDistance(distanceValue) : '--');
      setSafetyScore(formatSafetyScoreFromBackend(data));

      return true;
    } catch (error) {
      console.error('Error fetching route:', error);
      Alert.alert('Route error', 'Could not load the route from backend.');
      return false;
    }
  }

  async function handleStartRoute() {
    if (!destination) {
      Alert.alert('No destination', 'Please set a destination first.');
      return;
    }

    if (!currentLocation) {
      Alert.alert('Location unavailable', 'Current location is not ready yet.');
      return;
    }

    const success = await fetchRouteFromBackend(currentLocation);

    if (!success) return;

    lastSpokenStepRef.current = -1;
    setRouteInfoVisible(true);

    mapRef.current?.animateToRegion(
      {
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
        latitudeDelta: 0.02,
        longitudeDelta: 0.02,
      },
      500
    );
  }

  async function maybeReroute(current: Coordinate) {
    if (!navigationModeRef.current) return;
    if (!destination) return;
    if (isReroutingRef.current) return;

    const coords = routeCoordinatesRef.current;
    if (coords.length < 2) return;

    const minDistance = getMinDistanceToRoute(current, coords);

    if (minDistance < 20) return;

    try {
      isReroutingRef.current = true;
      console.log('Rerouting... current distance from route:', minDistance);

      const success = await fetchRouteFromBackend(current);

      if (!success) {
        console.warn('Reroute failed');
      }
    } finally {
      isReroutingRef.current = false;
    }
  }

  async function handleStartNavigation() {
    if (!currentLocation) {
      Alert.alert('Location unavailable', 'Current location is not ready yet.');
      return;
    }

    if (!destination) {
      Alert.alert('No destination', 'Please set a destination first.');
      return;
    }

    const success = await fetchRouteFromBackend(currentLocation);
    if (!success) return;

    setRouteInfoVisible(false);
    setNavigationMode(true);
    navigationModeRef.current = true;
    lastSpokenStepRef.current = -1;

    const nextHeading = getNavigationHeading(currentLocation);

    mapRef.current?.animateCamera(
      {
        center: currentLocation,
        pitch: 60,
        heading: nextHeading,
        zoom: 18,
        altitude: 400,
      },
      { duration: 800 }
    );
  }

  function handleExitNavigation() {
    setNavigationMode(false);
    navigationModeRef.current = false;
    setRouteInfoVisible(false);
    stopVoiceGuidance();
    lastSpokenStepRef.current = -1;

    if (currentLocation) {
      mapRef.current?.animateCamera(
        {
          center: currentLocation,
          pitch: 0,
          heading: 0,
          zoom: 15,
          altitude: 1200,
        },
        { duration: 800 }
      );
    }
  }

  function toggleFilter<K extends keyof RouteFilters>(key: K) {
    setRouteFilters((prev) => {
      if (typeof prev[key] !== 'boolean') return prev;
      return {
        ...prev,
        [key]: !prev[key],
      };
    });
  }

  function adjustMinSafety(delta: number) {
    setRouteFilters((prev) => {
      const newMin = Math.max(
        0,
        Math.min(prev.minSafetyScore + delta, prev.maxSafetyScore)
      );

      return {
        ...prev,
        minSafetyScore: newMin,
      };
    });
  }

  function adjustMaxSafety(delta: number) {
    setRouteFilters((prev) => {
      const newMax = Math.min(
        100,
        Math.max(prev.maxSafetyScore + delta, prev.minSafetyScore)
      );

      return {
        ...prev,
        maxSafetyScore: newMax,
      };
    });
  }

  function handleResetFilters() {
    setRouteFilters({
      avoidSteepHills: false,
      wheelchairAccessible: false,
      avoidReportedHazards: false,
      preferWellLitStreets: false,
      minSafetyScore: 0,
      maxSafetyScore: 100,
    });
  }

  async function handleApplyFilters() {
    setFilterModalVisible(false);

    if (destination && currentLocation) {
      await fetchRouteFromBackend();
      return;
    }

    Alert.alert('Filters applied', 'Your route preferences have been updated.');
  }

  function handleHazardPress(hazard: Hazard) {
    if (navigationMode) return;
    setSelectedHazard(hazard);
    setHazardPreviewVisible(true);
    setHazardDetailsVisible(false);
  }

  async function openHazardDetails() {
    if (!selectedHazard) return;

    try {
      const fullDetails = await hazardsService.getHazardById(selectedHazard.id);
      if (fullDetails) {
        setSelectedHazard({
          ...fullDetails,
          id: String(fullDetails.id)
        } as Hazard);
      }
    } catch (err) {
      console.error('Failed to fetch full hazard details:', err);
    }
    setHazardDetailsVisible(true);
  }

  function closeHazardDetails() {
    setHazardDetailsVisible(false);
  }

  function closeHazardPreview() {
    setHazardPreviewVisible(false);
  }

  const initialRegion = currentLocation
    ? {
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
        latitudeDelta: navigationMode ? 0.01 : 0.02,
        longitudeDelta: navigationMode ? 0.01 : 0.02,
      }
    : {
        latitude: DEFAULT_MAP_CENTER.latitude,
        longitude: DEFAULT_MAP_CENTER.longitude,
        latitudeDelta: DEFAULT_MAP_DELTA.latitudeDelta,
        longitudeDelta: DEFAULT_MAP_DELTA.longitudeDelta,
      };

  return (
    <View style={styles.container}>
      <MapCanvas
        mapRef={mapRef}
        initialRegion={initialRegion}
        currentLocation={currentLocation}
        destination={destination}
        hazards={hazards}
        routeCoordinates={routeCoordinates}
        navigationMode={navigationMode}
        onHazardPress={handleHazardPress}
      />

      {!navigationMode && (
        <>
          <SearchBar
            value={destinationText}
            onChangeText={setDestinationText}
            onSubmitEditing={handleSetDestination}
            onClear={handleClearSearch}
            suggestions={searchSuggestions}
            onSuggestionPress={handleSuggestionPress}
          />

          <TouchableOpacity
            style={styles.filterButton}
            onPress={() => setFilterModalVisible(true)}
          >
            <Ionicons name="options-outline" size={20} color="#FFFFFF" />
          </TouchableOpacity>
        </>
      )}

      <TouchableOpacity
        style={[
          styles.locateButton,
          navigationMode && styles.locateButtonNavigationMode,
        ]}
        onPress={() => {
          if (!currentLocation) {
            Alert.alert('Location unavailable', 'Current location is not ready yet.');
            return;
          }

          if (navigationMode) {
            const nextHeading = getNavigationHeading(currentLocation);

            mapRef.current?.animateCamera(
              {
                center: currentLocation,
                pitch: 60,
                heading: nextHeading,
                zoom: 18,
                altitude: 400,
              },
              { duration: 600 }
            );
            return;
          }

          mapRef.current?.animateToRegion(
            {
              latitude: currentLocation.latitude,
              longitude: currentLocation.longitude,
              latitudeDelta: 0.02,
              longitudeDelta: 0.02,
            },
            500
          );
        }}
      >
        <Ionicons
          name="locate"
          size={22}
          color={navigationMode ? '#FFFFFF' : '#0F3D91'}
        />
      </TouchableOpacity>

      {navigationMode && (
        <View style={styles.navigationBanner}>
          <View style={styles.navigationInstructionIcon}>
            <Ionicons name="arrow-up-outline" size={34} color="#FFFFFF" />
          </View>

          <View style={styles.navigationTextContainer}>
            <Text style={styles.navigationSmallText}>towards</Text>
            <Text style={styles.navigationLargeText} numberOfLines={2}>
              {destinationText || 'Selected destination'}
            </Text>
          </View>

          <View style={styles.navigationRightBadge}>
            <Ionicons name="sparkles" size={24} color="#2563EB" />
          </View>
        </View>
      )}

      {destination && !navigationMode && (
        <View style={styles.routeInfoWrapper}>
          <RouteInfoCard
            visible={routeInfoVisible}
            travelTime={travelTime}
            distance={distance}
            safetyScore={safetyScore}
            onPressRoute={handleStartRoute}
            onStartNavigation={handleStartNavigation}
          />
        </View>
      )}

      {navigationMode && (
        <View style={styles.navigationBottomPanel}>
          <View style={styles.navigationBottomLeft}>
            <Text style={styles.navigationBottomTime}>
              {travelTime || '—'}
            </Text>

            <Text style={styles.navigationBottomMeta}>
              {distance || '--'} {' • '} {getEstimatedArrivalTime()}
            </Text>

            <Text style={styles.navigationBottomSafety}>
              Safety: {safetyScore || '--'}
            </Text>
          </View>

          <TouchableOpacity
            style={styles.navigationExitButton}
            onPress={handleExitNavigation}
          >
            <Text style={styles.navigationExitButtonText}>Exit</Text>
          </TouchableOpacity>
        </View>
      )}

      <FilterModal
        visible={filterModalVisible}
        routeFilters={routeFilters}
        onClose={() => setFilterModalVisible(false)}
        onToggleFilter={toggleFilter}
        onAdjustMinSafety={adjustMinSafety}
        onAdjustMaxSafety={adjustMaxSafety}
        onReset={handleResetFilters}
        onApply={handleApplyFilters}
      />

      <HazardPreviewCard
        visible={hazardPreviewVisible}
        hazard={selectedHazard}
        onClose={closeHazardPreview}
        onOpenDetails={openHazardDetails}
      />

      <HazardDetailsModal
        visible={hazardDetailsVisible}
        hazard={selectedHazard}
        onClose={closeHazardDetails}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#F8FAFC',
  },

  filterButton: {
    position: 'absolute',
    top: 58,
    right: 16,
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: '#0F3D91',
    justifyContent: 'center',
    alignItems: 'center',
    elevation: 4,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.2,
    shadowRadius: 4,
    zIndex: 20,
  },

  locateButton: {
    position: 'absolute',
    bottom: 120,
    right: 20,
    width: 50,
    height: 50,
    borderRadius: 25,
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    elevation: 5,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.25,
    shadowRadius: 3.84,
    zIndex: 15,
  },

  locateButtonNavigationMode: {
    bottom: 150,
    backgroundColor: 'rgba(0,0,0,0.3)',
  },

  routeInfoWrapper: {
    position: 'absolute',
    bottom: 30,
    left: 16,
    right: 16,
    zIndex: 30,
  },

  navigationBanner: {
    position: 'absolute',
    top: 60,
    left: 16,
    right: 16,
    backgroundColor: '#0F3D91',
    borderRadius: 16,
    padding: 16,
    flexDirection: 'row',
    alignItems: 'center',
    elevation: 8,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.3,
    shadowRadius: 6,
    zIndex: 40,
  },

  navigationInstructionIcon: {
    width: 54,
    height: 54,
    borderRadius: 12,
    backgroundColor: 'rgba(255,255,255,0.15)',
    justifyContent: 'center',
    alignItems: 'center',
  },

  navigationTextContainer: {
    flex: 1,
    marginLeft: 16,
  },

  navigationSmallText: {
    color: 'rgba(255,255,255,0.7)',
    fontSize: 14,
    fontWeight: '500',
    textTransform: 'uppercase',
  },

  navigationLargeText: {
    color: '#FFFFFF',
    fontSize: 22,
    fontWeight: 'bold',
    marginTop: 2,
  },

  navigationRightBadge: {
    width: 44,
    height: 44,
    borderRadius: 22,
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
  },

  navigationBottomPanel: {
    position: 'absolute',
    bottom: 0,
    left: 0,
    right: 0,
    backgroundColor: '#FFFFFF',
    borderTopLeftRadius: 24,
    borderTopRightRadius: 24,
    padding: 24,
    paddingBottom: 40,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    elevation: 20,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: -4 },
    shadowOpacity: 0.1,
    shadowRadius: 10,
    zIndex: 50,
  },

  navigationBottomLeft: {
    flex: 1,
  },

  navigationBottomTime: {
    fontSize: 28,
    fontWeight: 'bold',
    color: '#10B981',
  },

  navigationBottomMeta: {
    fontSize: 16,
    color: '#64748B',
    marginTop: 4,
    fontWeight: '500',
  },

  navigationBottomSafety: {
    fontSize: 15,
    color: '#3B82F6',
    marginTop: 4,
    fontWeight: '600',
  },

  navigationExitButton: {
    backgroundColor: '#F1F5F9',
    paddingHorizontal: 24,
    paddingVertical: 14,
    borderRadius: 30,
    marginLeft: 16,
  },

  navigationExitButtonText: {
    color: '#EF4444',
    fontSize: 18,
    fontWeight: 'bold',
  },
});
