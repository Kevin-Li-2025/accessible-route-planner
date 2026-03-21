import React, { useEffect, useState, useRef } from 'react';
import MapView from 'react-native-maps';
import { StyleSheet, View, Text, TouchableOpacity, Alert } from 'react-native';
import * as Location from 'expo-location';
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';

import SearchBar from './SearchBar';
import RouteInfoCard from './RouteInfoCard';
import HazardPreviewCard from './HazardPreviewCard';
import HazardDetailsModal from './HazardDetailsModal';
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
  const mapRef = useRef<MapView | null>(null);
  const locationSubscriptionRef = useRef<Location.LocationSubscription | null>(null);
  const navigationModeRef = useRef(false);
  const routeStepsRef = useRef<VoiceStep[]>([]);
  const lastSpokenStepRef = useRef(-1);

  const [currentLocation, setCurrentLocation] = useState<Coordinate | null>(null);
  const [destinationText, setDestinationText] = useState('');
  const [destination, setDestination] = useState<Coordinate | null>(null);
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

  function mapBackendHazardToFrontend(item: any): Hazard {
    const rawType = String(item.type ?? item.hazardType ?? '').toLowerCase();
    const rawStatus = String(item.status ?? '').toLowerCase();

    return {
      // TODO: Replace fallback values once the backend response contract is finalized.
      // Right now this mapping is intentionally defensive because the backend field
      // names may still change during integration.
      id: item.id ?? Date.now().toString(),
      title: item.title ?? item.name ?? 'Hazard',
      type: rawType === 'lighting' ? 'lighting' : 'wheelchair',
      latitude: Number(item.latitude ?? item.lat ?? 0),
      longitude: Number(item.longitude ?? item.lng ?? 0),
      description: item.description ?? 'No description available.',
      status: rawStatus === 'acknowledged' ? 'Acknowledged' : 'Pending',
      locationText: item.locationText ?? item.location ?? 'Unknown location',
      reportedTime: item.reportedTime ?? item.reportedAt ?? 'Recently reported',
    };
  }

  async function loadHazards() {
    try {
      const data = await fetchHazardsApi();
      const rawHazards = Array.isArray(data) ? data : [];
      const mappedHazards = rawHazards.map(mapBackendHazardToFrontend);
      setHazards(mappedHazards);
    } catch (error) {
      console.error('Error fetching hazards:', error);
      Alert.alert('Hazard error', 'Could not load hazards from backend.');
    }
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
  }

  function handleOpenReportPage() {
    router.push('/report/reportpage');
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
  }, []);

  async function searchLocation(query: string) {
    try {
      const results = await api.get<any[]>(
        `/geocoding/search?query=${encodeURIComponent(query)}`,
        {
          // TODO: Change skipAuth to false if geocoding later becomes a protected endpoint.
          skipAuth: true,
        }
      );

      // TODO: Update this parsing logic if the backend later wraps the response,
      // for example: { data: [...] } or { results: [...] }.
      console.log('Geocoding results:', results);
      return Array.isArray(results) ? results : [];
    } catch (error) {
      console.error('Geocoding error:', error);
      Alert.alert('Search error', 'Could not search for this location.');
      return [];
    }
  }

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

    // TODO: Confirm the exact field names returned by the geocoding API.
    // Some backends may return latitude/longitude instead of lat/lon.
    const lat = parseFloat(String(firstResult.lat));
    const lon = parseFloat(String(firstResult.lon));

    if (Number.isNaN(lat) || Number.isNaN(lon)) {
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

    setDestination({
      latitude: lat,
      longitude: lon,
    });

    mapRef.current?.animateToRegion(
      {
        latitude: lat,
        longitude: lon,
        latitudeDelta: 0.02,
        longitudeDelta: 0.02,
      },
      700
    );
  }

  async function fetchRouteFromBackend() {
    if (!currentLocation || !destination) {
      Alert.alert('Missing data', 'Current location or destination is missing.');
      return false;
    }

    try {
      const data = await api.post<any>(
        '/routing/safe-path',
        {
          start: {
            x: currentLocation.longitude,
            y: currentLocation.latitude,
          },
          end: {
            x: destination.longitude,
            y: destination.latitude,
          },

          // TODO: Replace the hardcoded safetyWeight once the final filter-to-backend
          // mapping is agreed with the backend team.
          safetyWeight: 0.5,

          // TODO: Send routeFilters to the backend when the route API supports them.
          // Example future payload:
          // filters: {
          //   avoidSteepHills: routeFilters.avoidSteepHills,
          //   wheelchairAccessible: routeFilters.wheelchairAccessible,
          //   avoidReportedHazards: routeFilters.avoidReportedHazards,
          //   preferWellLitStreets: routeFilters.preferWellLitStreets,
          //   minSafetyScore: routeFilters.minSafetyScore,
          //   maxSafetyScore: routeFilters.maxSafetyScore,
          // }
        },
        {
          // TODO: Change skipAuth to false if the route API later requires login.
          skipAuth: true,
        }
      );

      console.log('Route API data:', data);

      // TODO: Simplify this once the backend response structure is finalized.
      // Right now multiple fallback paths are used to prevent integration breakage.
      const rawCoordinates =
        data?.path?.coordinates ||
        data?.route?.coordinates ||
        data?.geometry?.coordinates ||
        data?.coordinates ||
        [];

      const coords = Array.isArray(rawCoordinates)
        ? rawCoordinates
            .map((item: [number, number]) => ({
              latitude: Number(item?.[1]),
              longitude: Number(item?.[0]),
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

      // TODO: Confirm whether the backend will always return structured step data.
      // If yes, stepsFromCoordinates may be kept only as a fallback.
      const apiSteps = stepsFromApi(data?.steps);
      setRouteSteps(apiSteps.length > 0 ? apiSteps : stepsFromCoordinates(coords));
      setRouteCoordinates(coords);

      // TODO: Align these field names with the final backend contract.
      // Example possibilities:
      // durationSeconds / distanceMeters / score / safety_rating
      setTravelTime(
        data?.estimatedTime
          ? `${Math.round(data.estimatedTime / 60)} min`
          : 'Route ready'
      );
      setDistance(data?.distance ? `${Number(data.distance).toFixed(1)} m` : '--');
      setSafetyScore(
        typeof data?.safetyScore === 'number'
          ? `${(data.safetyScore * 100).toFixed(0)}%`
          : '--'
      );

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

    const success = await fetchRouteFromBackend();

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

  function handleStartNavigation() {
    if (!currentLocation) {
      Alert.alert('Location unavailable', 'Current location is not ready yet.');
      return;
    }

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

  function handleApplyFilters() {
    setFilterModalVisible(false);

    // TODO: After the backend supports route filters,
    // re-fetch the route here if a destination already exists.
    // Example:
    // if (destination) {
    //   handleStartRoute();
    // }

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
        latitude: 52.4862,
        longitude: -1.8904,
        latitudeDelta: 0.05,
        longitudeDelta: 0.05,
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
          />

          <TouchableOpacity
            style={styles.filterButton}
            onPress={() => setFilterModalVisible(true)}
          >
            <Ionicons name="options-outline" size={20} color="#FFFFFF" />
          </TouchableOpacity>

          <TouchableOpacity
            style={styles.reportButton}
            onPress={handleOpenReportPage}
          >
            <Ionicons name="warning-outline" size={22} color="#FFFFFF" />
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

  reportButton: {
    position: 'absolute',
    top: 122,
    right: 16,
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: '#EF4444',
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