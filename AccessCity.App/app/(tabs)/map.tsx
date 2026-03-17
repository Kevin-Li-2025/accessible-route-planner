import React, { useEffect, useState } from 'react';
import {
  StyleSheet,
  View,
  Text,
  TextInput,
  TouchableOpacity,
  Alert,
  ActivityIndicator,
  Modal,
  Pressable,
  ScrollView,
} from 'react-native';
import * as Location from 'expo-location';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import { router, useGlobalSearchParams } from 'expo-router';
import MapView from '../../components/MapView';
import { api, API_URL } from '../../services/api';
import { Coordinate, Hazard, RouteFilters } from '../../models/spatial';


const reportHazardOptions = [
  { key: 'broken_street_light', label: 'Broken street light', icon: <Ionicons name="bulb-outline" size={28} color="#EAB308" />, iconBg: '#FEF3C7' },
  { key: 'blocked_pavement', label: 'Blocked pavement', icon: <Ionicons name="warning-outline" size={28} color="#F97316" />, iconBg: '#FEE2E2' },
  { key: 'parked_car_blocking_dropped_kerb', label: 'Parked car blocking dropped kerb', icon: <Ionicons name="car-outline" size={28} color="#2563EB" />, iconBg: '#DBEAFE' },
  { key: 'road_obstruction', label: 'Road obstruction', icon: <Ionicons name="warning-outline" size={28} color="#EF4444" />, iconBg: '#FCE7F3' },
  { key: 'unsafe_crossing', label: 'Unsafe crossing', icon: <MaterialCommunityIcons name="walk" size={28} color="#14B8A6" />, iconBg: '#DCFCE7' },
  { key: 'other', label: 'Other', icon: <Ionicons name="document-text-outline" size={28} color="#4B5563" />, iconBg: '#E5E7EB' },
];

export default function IntegratedMapScreen() {
  const [currentLocation, setCurrentLocation] = useState<Coordinate | null>(null);
  const [destinationText, setDestinationText] = useState('');
  const [destination, setDestination] = useState<Coordinate | null>(null);
  const [routeGeoJSON, setRouteGeoJSON] = useState<any>(null);
  const [routeStats, setRouteStats] = useState<{ time: string; distance: string; score: string } | null>(null);

  // Hazards state
  const [hazards, setHazards] = useState<Hazard[]>([]);

  const [reportModalVisible, setReportModalVisible] = useState(false);
  const [reportStep, setReportStep] = useState<1 | 2 | 3>(1);
  const [selectedReportType, setSelectedReportType] = useState<string | null>(null);
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
  const [lastSearchedText, setLastSearchedText] = useState("");
  const [isLoading, setIsLoading] = useState(false);
  const [hazardPreviewVisible, setHazardPreviewVisible] = useState(false);
  const [hazardDetailsVisible, setHazardDetailsVisible] = useState(false);

  useEffect(() => {
    getCurrentLocation();
    fetchHazards();
  }, []);

  async function fetchHazards() {
    try {
      const data: any = await api.get('/hazards');
      const mapped = data.map((h: any) => ({
        id: h.id,
        title: h.description.split('.')[0], // Use first sentence as title
        type: h.type,
        latitude: h.location.coordinates[1],
        longitude: h.location.coordinates[0],
        description: h.description,
        status: h.status === 0 ? 'Reported' : h.status === 1 ? 'UnderReview' : 'Resolved',
        locationText: 'Hazard reported',
        reportedTime: new Date(h.reportedAt).toLocaleDateString()
      }));
      setHazards(mapped);
    } catch (err) {
      console.error("Failed to fetch hazards:", err);
    }
  }

  const { openReportModal } = useGlobalSearchParams<{ openReportModal?: string }>();
  useEffect(() => {
    if (openReportModal) {
      setReportModalVisible(true);
      setReportStep(1);
    }
  }, [openReportModal]);

  async function getCurrentLocation() {
    const { status } = await Location.requestForegroundPermissionsAsync();
    if (status !== 'granted') return;
    const location = await Location.getCurrentPositionAsync({});
    setCurrentLocation({
      latitude: location.coords.latitude,
      longitude: location.coords.longitude,
    });
  }

  async function handleSearch(): Promise<Coordinate | null> {
    if (!destinationText.trim()) return null;
    
    try {
      const results: any = await api.get(`/geocoding/search?query=${encodeURIComponent(destinationText)}`);
      if (results && results.length > 0) {
        const first = results[0];
        const newDest: Coordinate = {
          latitude: parseFloat(first.lat),
          longitude: parseFloat(first.lon),
        };
        setDestination(newDest);
        setLastSearchedText(destinationText);
        return newDest;
      } else {
        Alert.alert('Not found', 'Could not find that location.');
      }
    } catch (err) {
      console.error("Search failed:", err);
    }
    return null;
  }

  async function handleStartRoute(overrideDest?: Coordinate) {
    let finalDest = overrideDest || destination;

    if (!finalDest || (destinationText.trim() !== "" && destinationText !== lastSearchedText)) {
      if (!destinationText.trim()) {
        Alert.alert('Set destination', 'Type a place (e.g. University of Birmingham) and tap Start Navigation.');
        return;
      }
      setIsLoading(true);
      finalDest = await handleSearch();
      if (!finalDest) {
        setIsLoading(false);
        return;
      }
    }

    const start: Coordinate = currentLocation ?? { latitude: 52.4814, longitude: -1.9003 };
    setIsLoading(true);

    try {
      const preferences = [];
      if (routeFilters.wheelchairAccessible) preferences.push('wheelchair');
      if (routeFilters.preferWellLitStreets) preferences.push('low-light-penalty');
      if (routeFilters.avoidReportedHazards) preferences.push('avoid-reported-hazards');
      if (routeFilters.avoidSteepHills) preferences.push('avoid-steep-hills');

      const data: any = await api.post('/routing/safe-path', {
        start: { x: start.longitude, y: start.latitude },
        end: { x: finalDest.longitude, y: finalDest.latitude },
        preferences: preferences,
        safetyWeight: routeFilters.avoidReportedHazards ? 0.9 : 0.6
      });

      const path = data.path || data.Path;
      const geojson = path?.coordinates
        ? { type: 'LineString', coordinates: path.coordinates }
        : path;
      setRouteGeoJSON(geojson ?? null);
      
      const dist = data.distance ?? data.Distance ?? 0;
      const time = data.estimatedTime ?? data.EstimatedTime ?? 0;
      const score = data.safetyScore ?? data.SafetyScore ?? 0;
      
      setRouteStats({
        time: `${Math.round(time / 60)} min`,
        distance: `${(dist / 1000).toFixed(1)} km`,
        score: `${Math.round(score * 100)}%`
      });
    } catch (error) {
      console.error(error);
      const msg = error instanceof Error ? error.message : 'Unknown error';
      Alert.alert('Routing Error', `Could not compute route: ${msg}`);
    } finally {
      setIsLoading(false);
    }
  }

  const toggleFilter = (key: keyof RouteFilters) => {
    setRouteFilters(prev => ({ ...prev, [key]: !prev[key] as any }));
  };

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
        onMarkerPress={(h: Hazard) => {
          setSelectedHazard(h);
          setHazardPreviewVisible(true);
        }}
      />

      {/* Floating UI Elements */}
      <View style={styles.searchContainer}>
        <View style={styles.searchBox}>
          <Ionicons name="search" size={20} color="#9CA3AF" />
          <TextInput
            style={styles.input}
            placeholder="Where to?"
            placeholderTextColor="#9CA3AF"
            value={destinationText}
            onChangeText={setDestinationText}
            onSubmitEditing={() => handleStartRoute()}
          />
        </View>
      </View>

      <TouchableOpacity
        style={styles.filterButton}
        onPress={() => setFilterModalVisible(true)}
      >
        <Ionicons name="options-outline" size={20} color="#FFFFFF" />
      </TouchableOpacity>

      {routeStats ? (
        <View style={styles.routeCard}>
          <Text style={styles.routeTitle}>{routeStats.time}</Text>
          <Text style={styles.routeSubtitle}>
            {routeStats.distance} • Safety Score: {routeStats.score}
          </Text>
        </View>
      ) : (
        <TouchableOpacity 
          style={[styles.startRouteButton, isLoading && { opacity: 0.7 }]} 
          onPress={() => handleStartRoute()}
          disabled={isLoading}
        >
          {isLoading ? (
            <ActivityIndicator color="#FFFFFF" />
          ) : (
            <Text style={styles.startRouteButtonText}>Start Navigation</Text>
          )}
        </TouchableOpacity>
      )}

      {/* Filter Modal (Ported from Boming) */}
      <Modal visible={filterModalVisible} animationType="slide" transparent>
        <View style={styles.modalRoot}>
          <Pressable style={styles.overlay} onPress={() => setFilterModalVisible(false)} />
          <View style={styles.filterSheet}>
            <View style={styles.dragHandle} />
            <Text style={styles.filterTitle}>Route Preferences</Text>
            <View style={styles.sheetDivider} />
            
            <View style={styles.filterCard}>
              <TouchableOpacity style={styles.checkboxRow} onPress={() => toggleFilter('wheelchairAccessible')}>
                <View style={[styles.checkbox, routeFilters.wheelchairAccessible && styles.checkboxChecked]}>
                  {routeFilters.wheelchairAccessible && <Ionicons name="checkmark" size={14} color="#FFFFFF" />}
                </View>
                <Text style={styles.checkboxLabel}>Wheelchair Accessibility</Text>
              </TouchableOpacity>

              <TouchableOpacity style={styles.checkboxRow} onPress={() => toggleFilter('preferWellLitStreets')}>
                <View style={[styles.checkbox, routeFilters.preferWellLitStreets && styles.checkboxChecked]}>
                  {routeFilters.preferWellLitStreets && <Ionicons name="checkmark" size={14} color="#FFFFFF" />}
                </View>
                <Text style={styles.checkboxLabel}>Well-lit Streets Only</Text>
              </TouchableOpacity>

              <TouchableOpacity style={styles.checkboxRow} onPress={() => toggleFilter('avoidReportedHazards')}>
                <View style={[styles.checkbox, routeFilters.avoidReportedHazards && styles.checkboxChecked]}>
                  {routeFilters.avoidReportedHazards && <Ionicons name="checkmark" size={14} color="#FFFFFF" />}
                </View>
                <Text style={styles.checkboxLabel}>Avoid Reported Hazards</Text>
              </TouchableOpacity>

              <TouchableOpacity style={styles.checkboxRow} onPress={() => toggleFilter('avoidSteepHills')}>
                <View style={[styles.checkbox, routeFilters.avoidSteepHills && styles.checkboxChecked]}>
                  {routeFilters.avoidSteepHills && <Ionicons name="checkmark" size={14} color="#FFFFFF" />}
                </View>
                <Text style={styles.checkboxLabel}>Avoid Steep Hills</Text>
              </TouchableOpacity>
            </View>

            <TouchableOpacity style={styles.applyButton} onPress={() => setFilterModalVisible(false)}>
              <Text style={styles.applyButtonText}>Apply Filters</Text>
            </TouchableOpacity>
          </View>
        </View>
      </Modal>

      {/* Hazard Preview Card */}
      {hazardPreviewVisible && selectedHazard && (
        <View style={styles.hazardPreviewCard}>
          <Pressable style={styles.hazardPreviewClose} onPress={() => setHazardPreviewVisible(false)}>
            <Ionicons name="close" size={18} color="#6B7280" />
          </Pressable>
          <Text style={styles.hazardPreviewLabel}>{selectedHazard.type.toUpperCase()}</Text>
          <Text style={styles.hazardPreviewTitle}>{selectedHazard.title}</Text>
          <TouchableOpacity style={styles.hazardPreviewDetailsButton} onPress={() => {
            setHazardPreviewVisible(false);
            setHazardDetailsVisible(true);
          }}>
            <Text style={styles.hazardPreviewDetailsText}>Full Details</Text>
          </TouchableOpacity>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1 },
  searchContainer: { position: 'absolute', top: 60, left: 16, right: 80 },
  searchBox: {
    height: 56,
    backgroundColor: '#FFFFFF',
    borderRadius: 28,
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 20,
    shadowColor: '#000',
    shadowOpacity: 0.1,
    shadowRadius: 10,
    elevation: 5,
  },
  input: { flex: 1, paddingHorizontal: 10, fontSize: 16, color: '#111827' },
  filterButton: {
    position: 'absolute',
    top: 60,
    right: 16,
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: '#1D4ED8',
    justifyContent: 'center',
    alignItems: 'center',
    elevation: 5,
  },
  startRouteButton: {
    position: 'absolute',
    bottom: 40,
    left: 20,
    right: 20,
    backgroundColor: '#1D4ED8',
    borderRadius: 16,
    paddingVertical: 18,
    alignItems: 'center',
    shadowColor: '#000',
    shadowOpacity: 0.2,
    shadowRadius: 10,
    elevation: 8,
  },
  startRouteButtonText: { color: '#FFF', fontSize: 18, fontWeight: '800' },
  routeCard: {
    position: 'absolute',
    bottom: 30,
    left: 16,
    right: 16,
    backgroundColor: '#1D4ED8',
    borderRadius: 20,
    padding: 20,
    elevation: 10,
  },
  routeTitle: { color: '#FFF', fontSize: 26, fontWeight: '900' },
  routeSubtitle: { color: '#DBEAFE', fontSize: 16, marginTop: 4 },
  modalRoot: { flex: 1, justifyContent: 'flex-end' },
  overlay: { ...StyleSheet.absoluteFillObject, backgroundColor: 'rgba(0,0,0,0.4)' },
  filterSheet: {
    backgroundColor: '#FFFFFF',
    borderTopLeftRadius: 32,
    borderTopRightRadius: 32,
    padding: 24,
    minHeight: 400,
  },
  dragHandle: {
    width: 40,
    height: 5,
    borderRadius: 3,
    backgroundColor: '#E5E7EB',
    alignSelf: 'center',
    marginBottom: 20,
  },
  filterTitle: { fontSize: 22, fontWeight: '900', color: '#111827', marginBottom: 20 },
  sheetDivider: { height: 1, backgroundColor: '#F3F4F6', marginBottom: 20 },
  filterCard: { backgroundColor: '#F9FAFB', borderRadius: 20, padding: 16, marginBottom: 24 },
  checkboxRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 20 },
  checkbox: { width: 26, height: 26, borderRadius: 8, borderWidth: 2, borderColor: '#D1D5DB', marginRight: 12, justifyContent: 'center', alignItems: 'center' },
  checkboxChecked: { backgroundColor: '#1D4ED8', borderColor: '#1D4ED8' },
  checkboxLabel: { fontSize: 17, color: '#374151', fontWeight: '500' },
  applyButton: { backgroundColor: '#111827', borderRadius: 16, paddingVertical: 16, alignItems: 'center' },
  applyButtonText: { color: '#FFF', fontSize: 16, fontWeight: '700' },
  hazardPreviewCard: {
    position: 'absolute',
    left: 20,
    top: 140,
    width: 240,
    backgroundColor: '#FFFFFF',
    borderRadius: 24,
    padding: 20,
    shadowColor: '#000',
    shadowOpacity: 0.15,
    shadowRadius: 15,
    elevation: 8,
  },
  hazardPreviewClose: { position: 'absolute', top: 12, right: 12, padding: 4 },
  hazardPreviewLabel: { fontSize: 12, color: '#D97706', fontWeight: '800', marginBottom: 4 },
  hazardPreviewTitle: { fontSize: 19, fontWeight: '900', color: '#111827', marginBottom: 12 },
  hazardPreviewDetailsButton: { backgroundColor: '#FFF7ED', borderRadius: 12, paddingVertical: 10, alignItems: 'center' },
  hazardPreviewDetailsText: { color: '#D97706', fontSize: 14, fontWeight: '700' },
});
