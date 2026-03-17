import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import * as Location from 'expo-location';
import { Ionicons } from '@expo/vector-icons';
import { router, useGlobalSearchParams } from 'expo-router';

import MapView from '.';
import FilterModal from './FilterModal';
import HazardDetailsModal from './HazardDetailsModal';
import HazardPreviewCard from './HazardPreviewCard';
import ReportHazardModal from './ReportHazardModal';
import RouteInfoCard from './RouteInfoCard';
import SearchBar from './SearchBar';
import { api } from '../../services/api';
import { hazardsService } from '../../services/hazards.service';
import { Coordinate, Hazard, ReportHazardType, RouteFilters } from './MapTypes';

type GeocodingResult = {
  lat: string;
  lon: string;
};

type RouteGeometry = {
  type?: string;
  coordinates?: [number, number][];
};

type RouteResponse = {
  path?: RouteGeometry | null;
  Path?: RouteGeometry | null;
  distance?: number;
  Distance?: number;
  estimatedTime?: number;
  EstimatedTime?: number;
  safetyScore?: number;
  SafetyScore?: number;
};

const reportTypeToBackendType: Record<ReportHazardType, string> = {
  broken_street_light: 'broken_street_light',
  blocked_pavement: 'blocked_pavement',
  parked_car_blocking_dropped_kerb: 'parked_car_blocking_dropped_kerb',
  road_obstruction: 'road_obstruction',
  unsafe_crossing: 'unsafe_crossing',
  other: 'other',
};

export default function MapScreen() {
  const [currentLocation, setCurrentLocation] = useState<Coordinate | null>(null);
  const [destinationText, setDestinationText] = useState('');
  const [destination, setDestination] = useState<Coordinate | null>(null);
  const [routeGeoJSON, setRouteGeoJSON] = useState<RouteGeometry | null>(null);
  const [routeStats, setRouteStats] = useState<{
    travelTime: string;
    distance: string;
    safetyScore: string;
  } | null>(null);

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

  const [hazards, setHazards] = useState<Hazard[]>([]);
  const [selectedHazard, setSelectedHazard] = useState<Hazard | null>(null);
  const [lastSearchedText, setLastSearchedText] = useState('');
  const [isLoading, setIsLoading] = useState(false);
  const [hazardPreviewVisible, setHazardPreviewVisible] = useState(false);
  const [hazardDetailsVisible, setHazardDetailsVisible] = useState(false);

  const { openReportModal: openReportModalParam } =
    useGlobalSearchParams<{ openReportModal?: string }>();

  useEffect(() => {
    void getCurrentLocation();
    void fetchHazards();
  }, []);

  useEffect(() => {
    if (!openReportModalParam) return;

    setReportModalVisible(true);
    setReportStep(1);
    setSelectedReportType(null);
    setReportDescription('');
  }, [openReportModalParam]);

  async function fetchHazards() {
    try {
      setHazards(await hazardsService.getHazards());
    } catch (error) {
      console.error('Failed to fetch hazards:', error);
    }
  }

  async function getCurrentLocation() {
    try {
      const { status } = await Location.requestForegroundPermissionsAsync();

      if (status !== 'granted') {
        return;
      }

      const location = await Location.getCurrentPositionAsync({});
      setCurrentLocation({
        latitude: location.coords.latitude,
        longitude: location.coords.longitude,
      });
    } catch (error) {
      console.error('Error getting location:', error);
    }
  }

  async function handleSearch(query = destinationText): Promise<Coordinate | null> {
    const trimmedQuery = query.trim();

    if (!trimmedQuery) {
      return null;
    }

    try {
      const results = await api.get<GeocodingResult[]>(
        `/geocoding/search?query=${encodeURIComponent(trimmedQuery)}`
      );

      if (!results.length) {
        Alert.alert('Not found', 'Could not find that location.');
        return null;
      }

      const firstResult = results[0];
      const nextDestination = {
        latitude: Number.parseFloat(firstResult.lat),
        longitude: Number.parseFloat(firstResult.lon),
      };

      if (
        Number.isNaN(nextDestination.latitude)
        || Number.isNaN(nextDestination.longitude)
      ) {
        throw new Error('Invalid coordinates returned from geocoder.');
      }

      setDestination(nextDestination);
      setLastSearchedText(trimmedQuery);
      return nextDestination;
    } catch (error) {
      console.error('Search failed:', error);
      const message = error instanceof Error ? error.message : 'Unknown error';
      Alert.alert('Search error', `Could not find that location: ${message}`);
      return null;
    }
  }

  async function handleStartRoute(overrideDestination?: Coordinate) {
    const trimmedDestination = destinationText.trim();
    const currentDestination = overrideDestination ?? destination;
    const shouldSearchAgain = Boolean(
      trimmedDestination
      && (!currentDestination || trimmedDestination !== lastSearchedText)
    );

    if (!trimmedDestination && !currentDestination) {
      Alert.alert(
        'Set destination',
        'Type a place (for example University of Birmingham) and tap Start Navigation.'
      );
      return;
    }

    setIsLoading(true);

    try {
      let finalDestination = currentDestination;

      if (shouldSearchAgain) {
        finalDestination = await handleSearch(trimmedDestination);
      }

      if (!finalDestination) {
        return;
      }

      const start = currentLocation ?? { latitude: 52.4814, longitude: -1.9003 };
      const preferences: string[] = [];

      if (routeFilters.wheelchairAccessible) preferences.push('wheelchair');
      if (routeFilters.preferWellLitStreets) preferences.push('low-light-penalty');
      if (routeFilters.avoidReportedHazards) preferences.push('avoid-reported-hazards');
      if (routeFilters.avoidSteepHills) preferences.push('avoid-steep-hills');

      const data = await api.post<RouteResponse>('/routing/safe-path', {
        start: { x: start.longitude, y: start.latitude },
        end: { x: finalDestination.longitude, y: finalDestination.latitude },
        preferences,
        safetyWeight: routeFilters.avoidReportedHazards ? 0.9 : 0.6,
      });

      const routePath = data.path ?? data.Path ?? null;
      const normalizedRoute = routePath?.coordinates
        ? { type: 'LineString', coordinates: routePath.coordinates }
        : routePath;

      setRouteGeoJSON(normalizedRoute);

      const routeDistance = data.distance ?? data.Distance ?? 0;
      const routeTime = data.estimatedTime ?? data.EstimatedTime ?? 0;
      const rawSafetyScore = data.safetyScore ?? data.SafetyScore ?? 0;
      const normalizedSafetyScore = rawSafetyScore <= 1
        ? rawSafetyScore * 100
        : rawSafetyScore;

      setRouteStats({
        travelTime: `${Math.round(routeTime / 60)} min`,
        distance: `${(routeDistance / 1000).toFixed(1)} km`,
        safetyScore: `${Math.round(normalizedSafetyScore)}%`,
      });
    } catch (error) {
      console.error('Routing failed:', error);
      const message = error instanceof Error ? error.message : 'Unknown error';
      Alert.alert('Routing Error', `Could not compute route: ${message}`);
    } finally {
      setIsLoading(false);
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
      Alert.alert('Location unavailable', 'Please enable location services to report a hazard.');
      return;
    }

    try {
      setIsLoading(true);
      await hazardsService.reportHazard({
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
        type: reportTypeToBackendType[selectedReportType],
        description: reportDescription.trim() || 'Hazard reported by user.',
      });
      await fetchHazards();
      setReportStep(3);
    } catch (error) {
      console.error('Failed to submit hazard report:', error);
      const message = error instanceof Error ? error.message : 'Unknown error';
      Alert.alert('Report failed', `Could not submit report: ${message}`);
    } finally {
      setIsLoading(false);
    }
  }

  function handleDoneFromSuccess() {
    closeReportModal();
  }

  function handleHazardPress(hazard: Hazard) {
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

  const hasRouteStats = Boolean(
    routeStats?.travelTime || routeStats?.distance || routeStats?.safetyScore
  );

  return (
    <View style={styles.container}>
      <MapView
        centerCoordinate={
          destination
            ? [destination.longitude, destination.latitude]
            : currentLocation
              ? [currentLocation.longitude, currentLocation.latitude]
              : [-1.8904, 52.4862]
        }
        markers={hazards}
        routeGeoJSON={routeGeoJSON}
        onMarkerPress={handleHazardPress}
      />

      <SearchBar
        value={destinationText}
        onChangeText={setDestinationText}
        onSubmitEditing={() => {
          void handleStartRoute();
        }}
      />

      <TouchableOpacity
        style={styles.filterButton}
        onPress={() => setFilterModalVisible(true)}
      >
        <Ionicons name="options-outline" size={20} color="#FFFFFF" />
      </TouchableOpacity>

      {hasRouteStats ? (
        <RouteInfoCard
          travelTime={routeStats?.travelTime ?? ''}
          distance={routeStats?.distance ?? ''}
          safetyScore={routeStats?.safetyScore ?? ''}
        />
      ) : (
        <TouchableOpacity
          style={[styles.routeButton, isLoading && styles.routeButtonDisabled]}
          onPress={() => {
            void handleStartRoute();
          }}
          disabled={isLoading}
        >
          {isLoading ? (
            <ActivityIndicator color="#FFFFFF" />
          ) : (
            <Text style={styles.routeButtonText}>Start Navigation</Text>
          )}
        </TouchableOpacity>
      )}

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
  },
  routeButton: {
    position: 'absolute',
    bottom: 40,
    left: 20,
    right: 20,
    backgroundColor: '#1D4ED8',
    borderRadius: 16,
    paddingVertical: 18,
    alignItems: 'center',
    elevation: 8,
  },
  routeButtonDisabled: {
    opacity: 0.7,
  },
  routeButtonText: {
    color: '#fff',
    fontSize: 18,
    fontWeight: '800',
  },
});
