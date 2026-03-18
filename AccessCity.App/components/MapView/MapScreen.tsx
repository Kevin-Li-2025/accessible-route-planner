import React, { useEffect, useState, useRef } from 'react';
import MapView from 'react-native-maps';
import { StyleSheet, View, Text, TouchableOpacity, Alert } from 'react-native';
import * as Location from 'expo-location';
import { Ionicons } from '@expo/vector-icons';
import { router, useGlobalSearchParams } from 'expo-router';

import SearchBar from './SearchBar';
import RouteInfoCard from './RouteInfoCard';
import HazardPreviewCard from './HazardPreviewCard';
import HazardDetailsModal from './HazardDetailsModal';
import FilterModal from './FilterModal';
import ReportHazardModal from './ReportHazardModal';
import MapCanvas from './MapCanvas';
import { api } from '../../services/api';

import {
  Coordinate,
  Hazard,
  ReportHazardType,
  RouteFilters,
} from './MapTypes';

async function fetchHazardsApi() {
  return api.get<any[]>('/hazards', { skipAuth: true });
}

async function submitHazardReportApi(payload: {
  type: string;
  description: string;
  latitude: number;
  longitude: number;
}) {
  return api.post('/hazards', payload, { skipAuth: true });
}

export default function MapScreen() {
  const mapRef = useRef<MapView | null>(null);
  const locationSubscriptionRef = useRef<Location.LocationSubscription | null>(null);
  const navigationModeRef = useRef(false);

  const [currentLocation, setCurrentLocation] = useState<Coordinate | null>(null);
  const [destinationText, setDestinationText] = useState('');
  const [destination, setDestination] = useState<Coordinate | null>(null);
  const [routeCoordinates, setRouteCoordinates] = useState<Coordinate[]>([]);
  const [travelTime, setTravelTime] = useState('');
  const [distance, setDistance] = useState('');
  const [safetyScore, setSafetyScore] = useState('');

  const [hazards, setHazards] = useState<Hazard[]>([]);

  const [reportModalVisible, setReportModalVisible] = useState(false);
  const [selectedReportType, setSelectedReportType] =
    useState<ReportHazardType | null>(null);
  const [reportStep, setReportStep] = useState<1 | 2 | 3>(1);
  const [reportDescription, setReportDescription] = useState('');

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
  const [heading, setHeading] = useState(0);

  const { openReportModal: openReportModalParam } =
    useGlobalSearchParams<{ openReportModal?: string }>();

  useEffect(() => {
    navigationModeRef.current = navigationMode;
  }, [navigationMode]);

  function mapBackendHazardToFrontend(item: any): Hazard {
    const rawType = String(item.type ?? item.hazardType ?? '').toLowerCase();
    const rawStatus = String(item.status ?? '').toLowerCase();

    return {
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

    setDestination(null);
    setRouteCoordinates([]);
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

  useEffect(() => {
    if (openReportModalParam) {
      setReportModalVisible(true);
      setReportStep(1);
      setSelectedReportType(null);
      setReportDescription('');
    }
  }, [openReportModalParam]);

  async function searchLocation(query: string) {
    try {
      const results = await api.get<any[]>(
        `/geocoding/search?query=${encodeURIComponent(query)}`,
        { skipAuth: true }
      );
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

    const lat = parseFloat(String(firstResult.lat));
    const lon = parseFloat(String(firstResult.lon));

    if (Number.isNaN(lat) || Number.isNaN(lon)) {
      Alert.alert('Search error', 'Invalid coordinates returned from geocoding.');
      return;
    }

    // 先清掉旧路线和旧导航状态，但保留新的搜索文字
    setNavigationMode(false);
    navigationModeRef.current = false;
    setRouteCoordinates([]);
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
      const data = await api.post<any>('/routing/safe-path', {
        start: {
          x: currentLocation.longitude,
          y: currentLocation.latitude,
        },
        end: {
          x: destination.longitude,
          y: destination.latitude,
        },
        safetyWeight: 0.5,
      });

      console.log('Route API data:', data);

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

      setRouteCoordinates(coords);
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

    setNavigationMode(true);
    navigationModeRef.current = true;

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
    Alert.alert('Filters applied', 'Your route preferences have been updated.');
  }

  function closeReportModal() {
    setReportModalVisible(false);
    setReportStep(1);
    setSelectedReportType(null);
    setReportDescription('');

    router.setParams({
      openReportModal: undefined,
    });
  }

  function handleNextFromReportModal() {
    if (!selectedReportType) {
      Alert.alert('Missing type', 'Please select a hazard type.');
      return;
    }

    setReportStep(2);
  }

  function handleBackToStep1() {
    setReportStep(1);
  }

  async function handleSubmitReport() {
    if (!selectedReportType) {
      Alert.alert('Missing type', 'Please select a hazard type.');
      return;
    }

    if (!currentLocation) {
      Alert.alert('Location unavailable', 'Current location is not ready yet.');
      return;
    }

    try {
      await submitHazardReportApi({
        type: selectedReportType,
        description: reportDescription.trim(),
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
      });

      await loadHazards();
      setReportStep(3);
    } catch (error) {
      console.error('Error submitting hazard report:', error);
      Alert.alert('Submit error', 'Could not submit hazard report.');
    }
  }

  function handleDoneFromSuccess() {
    closeReportModal();
  }

  function handleHazardPress(hazard: Hazard) {
    if (navigationMode) return;
    setSelectedHazard(hazard);
    setHazardPreviewVisible(true);
    setHazardDetailsVisible(false);
  }

  function openHazardDetails() {
    if (!selectedHazard) return;
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
            travelTime={travelTime}
            distance={distance}
            safetyScore={safetyScore}
            onPressRoute={handleStartRoute}
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

      {!navigationMode && (
        <>
          <HazardPreviewCard
            visible={hazardPreviewVisible && !hazardDetailsVisible}
            hazard={selectedHazard}
            onClose={closeHazardPreview}
            onOpenDetails={openHazardDetails}
          />

          <FilterModal
            visible={filterModalVisible}
            routeFilters={routeFilters}
            onClose={() => setFilterModalVisible(false)}
            onToggleFilter={toggleFilter}
            onAdjustMinSafety={adjustMinSafety}
            onAdjustMaxSafety={adjustMaxSafety}
            onApply={handleApplyFilters}
            onReset={handleResetFilters}
          />

          <HazardDetailsModal
            visible={hazardDetailsVisible}
            hazard={selectedHazard}
            onClose={closeHazardDetails}
          />
        </>
      )}

      <ReportHazardModal
        visible={reportModalVisible}
        reportStep={reportStep}
        selectedReportType={selectedReportType}
        reportDescription={reportDescription}
        onClose={closeReportModal}
        onSelectType={setSelectedReportType}
        onNext={handleNextFromReportModal}
        onBack={handleBackToStep1}
        onSubmit={handleSubmitReport}
        onDone={handleDoneFromSuccess}
        onChangeDescription={setReportDescription}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },

  filterButton: {
    position: 'absolute',
    top: 60,
    right: 16,
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: '#0F3D91',
    justifyContent: 'center',
    alignItems: 'center',
    elevation: 4,
    zIndex: 20,
  },

  routeInfoWrapper: {
    position: 'absolute',
    left: 16,
    right: 16,
    bottom: 118,
    zIndex: 20,
    elevation: 20,
  },

  locateButton: {
    position: 'absolute',
    top: 128,
    right: 16,
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    elevation: 4,
    zIndex: 20,
  },

  locateButtonNavigationMode: {
    top: 150,
    backgroundColor: '#09122C',
  },

  navigationBanner: {
    position: 'absolute',
    top: 58,
    left: 16,
    right: 16,
    minHeight: 116,
    backgroundColor: '#0B7A75',
    borderRadius: 28,
    paddingHorizontal: 18,
    paddingVertical: 18,
    flexDirection: 'row',
    alignItems: 'center',
    elevation: 8,
    zIndex: 30,
  },

  navigationInstructionIcon: {
    width: 52,
    height: 52,
    borderRadius: 26,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 14,
  },

  navigationTextContainer: {
    flex: 1,
    justifyContent: 'center',
  },

  navigationSmallText: {
    color: '#D8FFFA',
    fontSize: 16,
    fontWeight: '600',
    marginBottom: 4,
    textTransform: 'lowercase',
  },

  navigationLargeText: {
    color: '#FFFFFF',
    fontSize: 28,
    fontWeight: '800',
    lineHeight: 34,
  },

  navigationRightBadge: {
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    marginLeft: 12,
  },

  navigationBottomPanel: {
    position: 'absolute',
    left: 16,
    right: 16,
    bottom: 100,
    backgroundColor: '#111111',
    borderRadius: 28,
    paddingHorizontal: 20,
    paddingVertical: 18,
    flexDirection: 'row',
    alignItems: 'center',
    elevation: 10,
    zIndex: 35,
  },

  navigationBottomLeft: {
    flex: 1,
    paddingRight: 16,
  },

  navigationBottomTime: {
    color: '#FFFFFF',
    fontSize: 34,
    fontWeight: '800',
    marginBottom: 4,
  },

  navigationBottomMeta: {
    color: '#D1D5DB',
    fontSize: 18,
    fontWeight: '500',
    marginBottom: 4,
  },

  navigationBottomSafety: {
    color: '#9CA3AF',
    fontSize: 15,
    fontWeight: '500',
  },

  navigationExitButton: {
    minWidth: 110,
    backgroundColor: '#EF4444',
    borderRadius: 24,
    paddingVertical: 18,
    paddingHorizontal: 24,
    alignItems: 'center',
    justifyContent: 'center',
  },

  navigationExitButtonText: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '800',
  },
});