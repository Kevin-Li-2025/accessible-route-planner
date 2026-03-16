import React, { useEffect, useState } from 'react';
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

import { hazards } from './mapData';
import {
  Coordinate,
  Hazard,
  ReportHazardType,
  RouteFilters,
} from './MapTypes';

export default function MapScreen() {
  const [currentLocation, setCurrentLocation] = useState<Coordinate | null>(null);
  const [destinationText, setDestinationText] = useState('');
  const [destination, setDestination] = useState<Coordinate | null>(null);
  const [routeCoordinates, setRouteCoordinates] = useState<Coordinate[]>([]);
  const [travelTime, setTravelTime] = useState('');
  const [distance, setDistance] = useState('');
  const [safetyScore, setSafetyScore] = useState('');

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

  const { openReportModal: openReportModalParam } =
    useGlobalSearchParams<{ openReportModal?: string }>();

  useEffect(() => {
    getCurrentLocation();
  }, []);

  useEffect(() => {
    if (openReportModalParam) {
      setReportModalVisible(true);
      setReportStep(1);
      setSelectedReportType(null);
      setReportDescription('');
    }
  }, [openReportModalParam]);

  async function getCurrentLocation() {
    try {
      const { status } = await Location.requestForegroundPermissionsAsync();

      if (status !== 'granted') {
        Alert.alert('Permission denied', 'Location permission is required.');
        return;
      }

      const location = await Location.getCurrentPositionAsync({});
      setCurrentLocation({
        latitude: location.coords.latitude,
        longitude: location.coords.longitude,
      });
    } catch (error) {
      console.error('Error getting location:', error);
      Alert.alert('Error', 'Could not get current location.');
    }
  }

  function handleSetDestination() {
    if (!destinationText.trim()) {
      Alert.alert('Missing destination', 'Please enter a destination.');
      return;
    }

    setDestination({
      latitude: 52.4862,
      longitude: -1.8904,
    });
  }

  async function fetchRouteFromBackend() {
    if (!currentLocation || !destination) {
      Alert.alert('Missing data', 'Current location or destination is missing.');
      return;
    }

    try {
      const response = await fetch('https://your-backend-api.com/route', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          startLatitude: currentLocation.latitude,
          startLongitude: currentLocation.longitude,
          endLatitude: destination.latitude,
          endLongitude: destination.longitude,
          filters: routeFilters,
        }),
      });

      if (!response.ok) {
        throw new Error('Failed to fetch route');
      }

      const data = await response.json();

      setRouteCoordinates(data.routeCoordinates || []);
      setTravelTime(data.travelTime || '');
      setDistance(data.distance || '');
      setSafetyScore(data.safetyScore || '');
    } catch (error) {
      console.error('Error fetching route:', error);
      Alert.alert('Route error', 'Could not load the route from backend.');
    }
  }

  async function handleStartRoute() {
    if (!destination) {
      Alert.alert('No destination', 'Please set a destination first.');
      return;
    }

    await fetchRouteFromBackend();
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

  function handleSubmitReport() {
    setReportStep(3);
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

  const initialRegion = currentLocation
    ? {
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
        latitudeDelta: 0.02,
        longitudeDelta: 0.02,
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
        initialRegion={initialRegion}
        currentLocation={currentLocation}
        destination={destination}
        hazards={hazards}
        routeCoordinates={routeCoordinates}
        onHazardPress={handleHazardPress}
      />

      <SearchBar
        value={destinationText}
        onChangeText={setDestinationText}
        onSubmitEditing={handleSetDestination}
      />

      <TouchableOpacity
        style={styles.filterButton}
        onPress={() => setFilterModalVisible(true)}
      >
        <Ionicons name="options-outline" size={20} color="#FFFFFF" />
      </TouchableOpacity>

      <TouchableOpacity style={styles.routeButton} onPress={handleStartRoute}>
        <Text style={styles.routeButtonText}>Start Route</Text>
      </TouchableOpacity>

      <RouteInfoCard
        travelTime={travelTime}
        distance={distance}
        safetyScore={safetyScore}
      />

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
    bottom: 110,
    left: 16,
    right: 16,
    backgroundColor: '#1D4ED8',
    borderRadius: 14,
    paddingVertical: 14,
    alignItems: 'center',
    elevation: 4,
  },
  routeButtonText: {
    color: '#fff',
    fontSize: 16,
    fontWeight: '700',
  },
});
