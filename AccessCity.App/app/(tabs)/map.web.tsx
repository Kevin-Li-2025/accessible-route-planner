import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  StyleSheet,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useLocalSearchParams } from 'expo-router';

import MapView from '@/components/MapView';
import { PremiumTag } from '@/components/ui/PremiumTag';
import { DEFAULT_MAP_CENTER_LNG_LAT } from '@/constants/defaultMapRegion';
import { AppTheme } from '@/constants/theme';
import { type AppHazard, hazardsService } from '@/services/hazards.service';
import { geocodingService, type GeocodingResult } from '@/services/geocoding.service';
import { routingService, type RouteResponse } from '@/services/routing.service';
import { type Hazard } from '@/models/spatial';

type TagTone = React.ComponentProps<typeof PremiumTag>['tone'];
type ProfileKey = 'walking' | 'manual-wheelchair' | 'stroller';
type RouteModeKey = 'safe' | 'accessible' | 'fastest';

function toMapHazard(hazard: AppHazard): Hazard {
  return {
    id: hazard.id,
    title: hazard.title,
    type: hazard.type,
    latitude: hazard.latitude,
    longitude: hazard.longitude,
    description: hazard.description,
    status: hazard.status,
    locationText: hazard.locationText,
    reportedTime: hazard.reportedTime,
  };
}

function formatHazardType(value: string) {
  return value
    .replace(/[_-]+/g, ' ')
    .replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function getStatusTone(status: string): TagTone {
  const normalized = status.toLowerCase();
  if (normalized.includes('resolved')) return 'good';
  if (normalized.includes('review') || normalized.includes('acknowledged')) return 'warning';
  if (normalized.includes('reported') || normalized.includes('pending')) return 'danger';
  return 'neutral';
}

const DEFAULT_START = { x: -1.89, y: 52.48 };
const DEFAULT_END = { x: -1.88, y: 52.485 };

const PROFILE_OPTIONS: {
  key: ProfileKey;
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
}[] = [
  { key: 'walking', label: 'Walking', icon: 'walk-outline' },
  { key: 'manual-wheelchair', label: 'Wheelchair', icon: 'accessibility-outline' },
  { key: 'stroller', label: 'Stroller', icon: 'body-outline' },
];

const ROUTE_MODE_OPTIONS: {
  key: RouteModeKey;
  label: string;
  safetyWeight: number;
  preferences: string[];
}[] = [
  { key: 'safe', label: 'Safe route', safetyWeight: 0.75, preferences: ['avoid-reported-hazards', 'prefer-crossings', 'low-light-penalty'] },
  { key: 'accessible', label: 'Accessible', safetyWeight: 0.65, preferences: ['wheelchair', 'avoid-stairs', 'avoid-steep-hills', 'prefer-crossings'] },
  { key: 'fastest', label: 'Fastest', safetyWeight: 0.35, preferences: [] },
];

function formatDistance(distance?: number) {
  if (typeof distance !== 'number' || !Number.isFinite(distance)) return '-';
  return distance >= 1000 ? `${(distance / 1000).toFixed(1)} km` : `${Math.round(distance)} m`;
}

function formatEta(minutes?: number) {
  if (typeof minutes !== 'number' || !Number.isFinite(minutes)) return '-';
  return `${Math.max(1, Math.round(minutes))} min`;
}

function formatSafetyScore(score?: number) {
  if (typeof score !== 'number' || !Number.isFinite(score)) return '-';
  return String(Math.round(score * 100));
}

function formatSafetyLabel(score?: number) {
  if (typeof score !== 'number' || !Number.isFinite(score)) return 'Pending';
  if (score >= 0.85) return 'Very safe';
  if (score >= 0.7) return 'Safe';
  if (score >= 0.5) return 'Use care';
  return 'High risk';
}

function formatRouteStep(step: unknown) {
  if (!step || typeof step !== 'object') return 'Continue on the highlighted route.';
  const value = step as Record<string, unknown>;
  for (const key of ['instruction', 'text', 'description', 'name']) {
    const candidate = value[key];
    if (typeof candidate === 'string' && candidate.trim()) {
      return candidate.trim();
    }
  }
  return 'Continue on the highlighted route.';
}

function formatRouteImpactLabel(route: RouteResponse | null, routeStatus: 'idle' | 'loading' | 'ready' | 'error') {
  if (routeStatus === 'loading') return 'Checking route impact';
  if (routeStatus === 'error') return 'Route impact unavailable';

  const warningCount = route?.warnings?.length ?? 0;
  if (warningCount > 0) return `${warningCount} route warnings`;
  if (route) return 'No reports affect this route';
  return 'Route impact pending';
}

function routeToGeoJson(route: RouteResponse | null) {
  if (!route?.path) {
    return { type: 'FeatureCollection', features: [] };
  }

  return {
    type: 'FeatureCollection',
    features: [
      {
        type: 'Feature',
        geometry: route.path,
        properties: {},
      },
    ],
  };
}

function readCoordinate(value: string | number | undefined) {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value === 'string' && value.trim()) {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  }
  return null;
}

function getResultCoordinate(result: GeocodingResult) {
  const latitude = readCoordinate(result.lat ?? result.latitude ?? result.y);
  const longitude = readCoordinate(result.lon ?? result.lng ?? result.longitude ?? result.x);
  if (latitude === null || longitude === null) return null;
  return { latitude, longitude };
}

function formatGeocodingLabel(result: GeocodingResult, fallback: string) {
  if (typeof result.display_name === 'string' && result.display_name.trim()) {
    return result.display_name.trim().split(',').slice(0, 2).join(',');
  }
  if (typeof result.name === 'string' && result.name.trim()) {
    return result.name.trim();
  }
  return fallback;
}

export default function MapPageWeb() {
  const params = useLocalSearchParams<{ avoidHazardId?: string; avoidHazardTitle?: string }>();
  const avoidHazardId = typeof params.avoidHazardId === 'string' && params.avoidHazardId.trim()
    ? params.avoidHazardId.trim()
    : null;
  const avoidHazardTitle = typeof params.avoidHazardTitle === 'string' && params.avoidHazardTitle.trim()
    ? params.avoidHazardTitle.trim()
    : null;
  const [hazards, setHazards] = useState<Hazard[]>([]);
  const [selectedHazard, setSelectedHazard] = useState<Hazard | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showHazards, setShowHazards] = useState(true);
  const [route, setRoute] = useState<RouteResponse | null>(null);
  const [routeStatus, setRouteStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [routeError, setRouteError] = useState<string | null>(null);
  const [destinationQuery, setDestinationQuery] = useState('');
  const [destinationLabel, setDestinationLabel] = useState('Where to?');
  const [destinationCoordinate, setDestinationCoordinate] = useState<{ latitude: number; longitude: number } | null>(null);
  const [selectedProfile, setSelectedProfile] = useState<ProfileKey>('manual-wheelchair');
  const [selectedRouteMode, setSelectedRouteMode] = useState<RouteModeKey>('safe');
  const [isSearchingDestination, setIsSearchingDestination] = useState(false);
  const [selectedAvoidHazard, setSelectedAvoidHazard] = useState<{ id: string; title: string } | null>(null);
  const [navigationActive, setNavigationActive] = useState(false);
  const hazardRequestIdRef = useRef(0);
  const routeRequestIdRef = useRef(0);
  const routeLoadingRef = useRef(false);

  const centerCoordinate = useMemo<[number, number]>(() => {
    const first = hazards[0];
    return first
      ? [first.longitude, first.latitude]
      : DEFAULT_MAP_CENTER_LNG_LAT;
  }, [hazards]);

  const routeGeoJSON = useMemo(() => routeToGeoJson(route), [route]);

  const selectedRouteModeConfig = useMemo(
    () => ROUTE_MODE_OPTIONS.find((option) => option.key === selectedRouteMode) ?? ROUTE_MODE_OPTIONS[0],
    [selectedRouteMode]
  );

  const effectiveAvoidHazard = useMemo(() => {
    if (selectedAvoidHazard) return selectedAvoidHazard;
    if (avoidHazardId) {
      return {
        id: avoidHazardId,
        title: avoidHazardTitle ?? 'selected hazard',
      };
    }
    return null;
  }, [avoidHazardId, avoidHazardTitle, selectedAvoidHazard]);

  const routeRequest = useMemo(() => {
    const end = destinationCoordinate
      ? { x: destinationCoordinate.longitude, y: destinationCoordinate.latitude }
      : DEFAULT_END;
    const preferences = selectedRouteModeConfig.preferences;
    const routePreferences = effectiveAvoidHazard && !preferences.includes('avoid-reported-hazards')
      ? [...preferences, 'avoid-reported-hazards']
      : preferences;

    return {
      start: DEFAULT_START,
      end,
      profile: selectedProfile,
      safetyWeight: effectiveAvoidHazard ? Math.max(selectedRouteModeConfig.safetyWeight, 0.85) : selectedRouteModeConfig.safetyWeight,
      preferences: routePreferences,
    };
  }, [destinationCoordinate, effectiveAvoidHazard, selectedProfile, selectedRouteModeConfig]);

  const loadHazards = useCallback(async () => {
    const requestId = hazardRequestIdRef.current + 1;
    hazardRequestIdRef.current = requestId;

    try {
      setIsLoading(true);
      setError(null);
      const page = await hazardsService.getHazardsPage({ status: 'Reported', limit: 100 });
      if (hazardRequestIdRef.current !== requestId) return;
      setHazards(page.items.map(toMapHazard));
    } catch (loadError) {
      if (hazardRequestIdRef.current !== requestId) return;
      console.warn('Failed to load web map hazards:', loadError);
      setError('Could not load hazards');
      setHazards([]);
    } finally {
      if (hazardRequestIdRef.current === requestId) {
        setIsLoading(false);
      }
    }
  }, []);

  const loadRecommendedRoute = useCallback(async () => {
    if (routeLoadingRef.current) return;

    const requestId = routeRequestIdRef.current + 1;
    routeRequestIdRef.current = requestId;
    routeLoadingRef.current = true;

    try {
      setRouteStatus('loading');
      setRouteError(null);
      const nextRoute = await routingService.getSafePathResolved(routeRequest);
      if (routeRequestIdRef.current !== requestId) return;
      setRoute(nextRoute);
      setRouteStatus('ready');
    } catch (loadError) {
      if (routeRequestIdRef.current !== requestId) return;
      console.warn('Failed to load recommended route:', loadError);
      setRoute(null);
      setRouteStatus('error');
      setRouteError('Route engine unavailable');
    } finally {
      if (routeRequestIdRef.current === requestId) {
        routeLoadingRef.current = false;
      }
    }
  }, [routeRequest]);

  const searchDestination = useCallback(async () => {
    const query = destinationQuery.trim();
    if (!query) {
      Alert.alert('Destination needed', 'Enter a destination in Birmingham to calculate a route.');
      return;
    }

    try {
      setIsSearchingDestination(true);
      const results = await geocodingService.search(query);
      const match = results
        .map((result) => ({ result, coordinate: getResultCoordinate(result) }))
        .find((item) => item.coordinate !== null);

      if (!match?.coordinate) {
        Alert.alert('No destination found', 'Try a street, place, or postcode nearby.');
        return;
      }

      setDestinationCoordinate(match.coordinate);
      setDestinationLabel(formatGeocodingLabel(match.result, query));
      setRoute(null);
      setRouteStatus('idle');
      setNavigationActive(false);
    } catch (searchError) {
      console.warn('Destination search failed:', searchError);
      Alert.alert('Search error', 'Could not find that destination right now.');
    } finally {
      setIsSearchingDestination(false);
    }
  }, [destinationQuery]);

  useEffect(() => {
    void loadHazards();
    void loadRecommendedRoute();
  }, [loadHazards, loadRecommendedRoute]);

  useEffect(() => {
    setNavigationActive(false);
  }, [routeRequest]);

  function avoidSelectedHazardInRoute() {
    if (!selectedHazard) return;
    setSelectedAvoidHazard({
      id: String(selectedHazard.id),
      title: selectedHazard.title,
    });
    setSelectedHazard(null);
    setRoute(null);
    setRouteStatus('idle');
    setNavigationActive(false);
  }

  function startNavigation() {
    if (!route) {
      void loadRecommendedRoute();
      return;
    }
    setNavigationActive(true);
  }

  return (
    <View style={styles.container}>
      <MapView
        centerCoordinate={centerCoordinate}
        markers={hazards}
        routeGeoJSON={routeGeoJSON}
        onMarkerPress={setSelectedHazard}
        showHazards={showHazards}
      />

      <View style={styles.topPanel}>
        <View style={styles.searchStack}>
          <View style={styles.searchRow}>
            <Ionicons name="radio-button-on-outline" size={14} color={AppTheme.color.textSubtle} />
            <Text style={styles.searchLabel}>From</Text>
            <Text style={styles.searchValue}>My location</Text>
          </View>
          <View style={styles.searchDivider} />
          <View style={styles.searchRow}>
            <Ionicons name="location-outline" size={14} color={AppTheme.color.textSubtle} />
            <Text style={styles.searchLabel}>To</Text>
            <TextInput
              value={destinationQuery}
              onChangeText={setDestinationQuery}
              onSubmitEditing={() => void searchDestination()}
              placeholder={destinationLabel}
              placeholderTextColor={AppTheme.color.textSubtle}
              returnKeyType="search"
              style={styles.destinationInput}
              accessibilityLabel="Destination"
            />
            <TouchableOpacity
              activeOpacity={0.82}
              onPress={() => void searchDestination()}
              accessibilityRole="button"
              accessibilityLabel="Search destination"
              style={styles.searchIconButton}
            >
              {isSearchingDestination ? (
                <ActivityIndicator size="small" color={AppTheme.color.text} />
              ) : (
                <Ionicons name="search" size={16} color={AppTheme.color.text} />
              )}
            </TouchableOpacity>
          </View>
        </View>

        <View style={styles.modeRow}>
          {PROFILE_OPTIONS.map((option) => {
            const isActive = selectedProfile === option.key;
            return (
              <TouchableOpacity
                key={option.key}
                activeOpacity={0.84}
                accessibilityRole="button"
                accessibilityState={{ selected: isActive }}
                onPress={() => setSelectedProfile(option.key)}
              >
                <PremiumTag
                  label={option.label}
                  icon={option.icon}
                  tone={isActive ? 'accent' : 'neutral'}
                  variant={isActive ? 'soft' : 'surface'}
                />
              </TouchableOpacity>
            );
          })}
        </View>

        <View style={styles.routeModeRow}>
          {ROUTE_MODE_OPTIONS.map((option) => {
            const isActive = selectedRouteMode === option.key;
            return (
              <TouchableOpacity
                key={option.key}
                activeOpacity={0.84}
                accessibilityRole="button"
                accessibilityState={{ selected: isActive }}
                onPress={() => setSelectedRouteMode(option.key)}
                style={isActive ? styles.routeModeActive : styles.routeModeButton}
              >
                <Text style={isActive ? styles.routeModeActiveText : styles.routeModeText}>
                  {option.label}
                </Text>
              </TouchableOpacity>
            );
          })}
        </View>

        <View style={styles.tagRow}>
          <PremiumTag
            label={showHazards ? `${hazards.length} city reports` : 'Reports hidden'}
            icon="warning-outline"
            tone={hazards.length > 0 ? 'danger' : 'neutral'}
            variant="surface"
          />
          <PremiumTag
            label={formatRouteImpactLabel(route, routeStatus)}
            icon="shield-checkmark-outline"
            tone={route?.warnings?.length ? 'warning' : 'good'}
            variant="soft"
          />
          {destinationCoordinate ? (
            <PremiumTag
              label={destinationLabel}
              icon="flag-outline"
              tone="accent"
              variant="soft"
            />
          ) : null}
          {effectiveAvoidHazard ? (
            <PremiumTag
              label={`Avoiding ${effectiveAvoidHazard.title.slice(0, 18)}`}
              icon="navigate-outline"
              tone="warning"
              variant="soft"
            />
          ) : null}
        </View>
      </View>

      <View style={styles.mapControls}>
        <TouchableOpacity
          style={[styles.controlButton, !showHazards && styles.controlButtonMuted]}
          activeOpacity={0.86}
          onPress={() => setShowHazards((current) => !current)}
          accessibilityRole="button"
          accessibilityLabel="Toggle hazard layer"
        >
          <Ionicons name="layers-outline" size={19} color={showHazards ? AppTheme.color.text : AppTheme.color.textSubtle} />
        </TouchableOpacity>
        <TouchableOpacity
          style={styles.controlButton}
          activeOpacity={0.86}
          onPress={() => void loadRecommendedRoute()}
          accessibilityRole="button"
          accessibilityLabel="Refresh recommended route"
        >
          {routeStatus === 'loading' ? (
            <ActivityIndicator size="small" color={AppTheme.color.text} />
          ) : (
            <Ionicons name="navigate" size={19} color={AppTheme.color.text} />
          )}
        </TouchableOpacity>
        <TouchableOpacity
          style={styles.controlButton}
          onPress={() => {
            void loadHazards();
            void loadRecommendedRoute();
          }}
          accessibilityRole="button"
          accessibilityLabel="Refresh hazards"
          activeOpacity={0.86}
        >
          {isLoading ? (
            <ActivityIndicator size="small" color={AppTheme.color.text} />
          ) : (
            <Ionicons name="refresh" size={18} color={AppTheme.color.text} />
          )}
        </TouchableOpacity>
      </View>

      <View style={styles.recommendationCard}>
        <View style={styles.recommendationHeader}>
          <View>
            <Text style={styles.recommendationTitle}>Recommended route</Text>
            <View style={styles.recommendationMetaRow}>
              <Text style={styles.routeMetric}>{formatEta(route?.estimatedTime)}</Text>
              <Text style={styles.routeMetric}>{formatDistance(route?.distance)}</Text>
              <Text style={styles.routeMetricMuted}>
                {routeStatus === 'loading'
                  ? 'Calculating'
                  : route?.warnings?.length
                    ? `${route.warnings.length} warnings`
                    : routeStatus === 'ready'
                      ? 'No route warnings'
                      : 'Route not loaded'}
              </Text>
            </View>
          </View>
          <View style={styles.scoreBadge}>
            <Text style={styles.scoreValue}>{formatSafetyScore(route?.safetyScore)}</Text>
            <Text style={styles.scoreLabel}>{formatSafetyLabel(route?.safetyScore)}</Text>
          </View>
        </View>
        <View style={styles.routeSparkline}>
          <View style={styles.sparklineFill} />
        </View>
        {routeError ? <Text style={styles.routeErrorText}>{routeError}</Text> : null}
        <TouchableOpacity
          style={styles.startButton}
          activeOpacity={0.9}
          onPress={startNavigation}
        >
          {routeStatus === 'loading' ? (
            <ActivityIndicator size="small" color={AppTheme.color.textInverse} />
          ) : (
            <Ionicons name="navigate-outline" size={16} color={AppTheme.color.textInverse} />
          )}
          <Text style={styles.startButtonText}>
            {navigationActive ? 'Navigation active' : route ? 'Start navigation' : 'Load route'}
          </Text>
        </TouchableOpacity>
        {navigationActive && route ? (
          <View style={styles.guidancePanel}>
            <View style={styles.guidanceHeader}>
              <Text style={styles.guidanceLabel}>Next step</Text>
              <TouchableOpacity
                activeOpacity={0.84}
                style={styles.endNavigationButton}
                onPress={() => setNavigationActive(false)}
                accessibilityRole="button"
                accessibilityLabel="End navigation"
              >
                <Text style={styles.endNavigationText}>End</Text>
              </TouchableOpacity>
            </View>
            <Text style={styles.guidanceText} numberOfLines={2}>
              {formatRouteStep(Array.isArray(route.steps) ? route.steps[0] : null)}
            </Text>
          </View>
        ) : null}
        <View style={styles.reasonList}>
          {[
            route ? 'Avoids known hazards' : 'Finding a safer route',
            route?.warnings?.length ? 'Review warnings before you go' : 'No major warnings on this route',
          ].map((reason) => (
            <View key={reason} style={styles.reasonRow}>
              <Ionicons name="checkmark-circle" size={14} color={AppTheme.color.success} />
              <Text style={styles.reasonText}>{reason}</Text>
            </View>
          ))}
        </View>
      </View>

      {selectedHazard ? (
        <View style={styles.detailPanel}>
          <View style={styles.detailHeader}>
            <View style={styles.detailTitleBlock}>
              <Text style={styles.detailTitle} numberOfLines={1}>
                {selectedHazard.title}
              </Text>
              <View style={styles.detailTagRow}>
                <PremiumTag
                  label={selectedHazard.status}
                  tone={getStatusTone(selectedHazard.status)}
                  variant="soft"
                />
                <PremiumTag
                  label={formatHazardType(selectedHazard.type)}
                  icon="pricetag-outline"
                  tone="neutral"
                  variant="surface"
                />
                <PremiumTag
                  label={selectedHazard.reportedTime}
                  icon="time-outline"
                  tone="neutral"
                  variant="surface"
                />
              </View>
            </View>
            <TouchableOpacity
              style={styles.iconButton}
              onPress={() => setSelectedHazard(null)}
              accessibilityRole="button"
              accessibilityLabel="Close hazard details"
            >
              <Ionicons name="close" size={18} color={AppTheme.color.text} />
            </TouchableOpacity>
          </View>
          <Text style={styles.detailDescription} numberOfLines={3}>
            {selectedHazard.description}
          </Text>
          <Text style={styles.detailLocation} numberOfLines={1}>
            {selectedHazard.locationText}
          </Text>
          <View style={styles.detailActions}>
            <TouchableOpacity
              activeOpacity={0.86}
              style={styles.detailPrimaryButton}
              onPress={avoidSelectedHazardInRoute}
              accessibilityRole="button"
              accessibilityLabel={`Avoid ${selectedHazard.title} in route`}
            >
              <Ionicons name="navigate-outline" size={15} color={AppTheme.color.textInverse} />
              <Text style={styles.detailPrimaryText}>Avoid in route</Text>
            </TouchableOpacity>
          </View>
        </View>
      ) : null}

      {error ? (
        <View style={styles.errorPanel}>
          <Text style={styles.errorText}>{error}</Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: AppTheme.color.background,
  },
  topPanel: {
    position: 'absolute',
    top: 12,
    left: 12,
    right: 12,
    maxWidth: AppTheme.layout.mobileFrameWidth,
    borderRadius: AppTheme.radius.md,
    backgroundColor: 'rgba(255, 253, 247, 0.94)',
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    paddingHorizontal: 9,
    paddingVertical: 9,
    gap: 7,
    boxShadow: '0 10px 20px rgba(26, 23, 16, 0.10)',
  },
  panelHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  panelTitle: {
    color: AppTheme.color.text,
    ...AppTheme.type.cardTitle,
  },
  searchStack: {
    borderRadius: AppTheme.radius.sm,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    backgroundColor: AppTheme.color.surface,
    paddingHorizontal: 9,
  },
  searchRow: {
    minHeight: 32,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 7,
  },
  searchDivider: {
    height: 1,
    backgroundColor: AppTheme.color.border,
    marginLeft: 22,
  },
  searchLabel: {
    color: AppTheme.color.textMuted,
    ...AppTheme.type.label,
  },
  searchValue: {
    flex: 1,
    color: AppTheme.color.text,
    ...AppTheme.type.label,
  },
  destinationInput: {
    flex: 1,
    minHeight: 28,
    paddingVertical: 0,
    color: AppTheme.color.text,
    ...AppTheme.type.label,
  },
  searchIconButton: {
    width: 28,
    height: 28,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: AppTheme.color.surfaceSubtle,
  },
  modeRow: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 5,
  },
  routeModeRow: {
    minHeight: 34,
    borderRadius: AppTheme.radius.pill,
    backgroundColor: AppTheme.color.surfaceSubtle,
    flexDirection: 'row',
    alignItems: 'center',
    padding: 3,
  },
  routeModeActive: {
    flex: 1,
    minHeight: 28,
    borderRadius: AppTheme.radius.pill,
    backgroundColor: AppTheme.color.primary,
    alignItems: 'center',
    justifyContent: 'center',
  },
  routeModeButton: {
    flex: 1,
    minHeight: 28,
    borderRadius: AppTheme.radius.pill,
    alignItems: 'center',
    justifyContent: 'center',
  },
  routeModeActiveText: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.label,
  },
  routeModeText: {
    flex: 1,
    textAlign: 'center',
    color: AppTheme.color.textMuted,
    ...AppTheme.type.label,
  },
  tagRow: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 5,
  },
  iconButton: {
    width: 34,
    height: 34,
    borderRadius: AppTheme.radius.sm,
    backgroundColor: AppTheme.color.surfaceSubtle,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    justifyContent: 'center',
    alignItems: 'center',
  },
  mapControls: {
    position: 'absolute',
    right: 14,
    top: 218,
    gap: 8,
  },
  controlButton: {
    width: 38,
    height: 38,
    borderRadius: 19,
    backgroundColor: AppTheme.color.surface,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    alignItems: 'center',
    justifyContent: 'center',
    boxShadow: '0 8px 16px rgba(26, 23, 16, 0.12)',
  },
  controlButtonMuted: {
    opacity: 0.68,
  },
  errorPanel: {
    position: 'absolute',
    top: 88,
    left: 18,
    right: 18,
    borderRadius: AppTheme.radius.md,
    backgroundColor: AppTheme.color.dangerSoft,
    borderWidth: 1,
    borderColor: '#FECACA',
    padding: 12,
  },
  errorText: {
    color: AppTheme.color.danger,
    ...AppTheme.type.meta,
  },
  recommendationCard: {
    position: 'absolute',
    left: 12,
    right: 12,
    bottom: 70,
    maxWidth: AppTheme.layout.mobileFrameWidth,
    borderRadius: AppTheme.radius.md,
    backgroundColor: 'rgba(255, 253, 247, 0.96)',
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    padding: 10,
    boxShadow: '0 10px 22px rgba(26, 23, 16, 0.12)',
  },
  recommendationHeader: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: 12,
  },
  recommendationTitle: {
    color: AppTheme.color.text,
    ...AppTheme.type.cardTitle,
  },
  recommendationMetaRow: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 8,
    marginTop: 4,
  },
  routeMetric: {
    color: AppTheme.color.text,
    ...AppTheme.type.label,
  },
  routeMetricMuted: {
    color: AppTheme.color.textMuted,
    ...AppTheme.type.label,
  },
  scoreBadge: {
    width: 48,
    borderRadius: AppTheme.radius.sm,
    backgroundColor: AppTheme.color.success,
    alignItems: 'center',
    paddingVertical: 5,
  },
  scoreValue: {
    color: AppTheme.color.textInverse,
    fontSize: 18,
    lineHeight: 21,
    fontWeight: '800',
  },
  scoreLabel: {
    color: AppTheme.color.textInverse,
    fontSize: 9,
    lineHeight: 12,
    fontWeight: '700',
  },
  routeSparkline: {
    height: 24,
    borderRadius: 10,
    backgroundColor: AppTheme.color.successSoft,
    marginTop: 8,
    overflow: 'hidden',
  },
  routeErrorText: {
    marginTop: 8,
    color: AppTheme.color.warning,
    ...AppTheme.type.label,
  },
  sparklineFill: {
    position: 'absolute',
    left: -8,
    right: -8,
    bottom: 8,
    height: 2,
    backgroundColor: AppTheme.color.success,
    transform: [{ rotate: '4deg' }],
  },
  startButton: {
    minHeight: 38,
    borderRadius: AppTheme.radius.sm,
    backgroundColor: AppTheme.color.primary,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
    marginTop: 8,
  },
  startButtonText: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.label,
  },
  guidancePanel: {
    marginTop: 8,
    borderRadius: AppTheme.radius.sm,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    backgroundColor: AppTheme.color.surfaceSubtle,
    padding: 8,
    gap: 4,
  },
  guidanceHeader: {
    minHeight: 28,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 8,
  },
  guidanceLabel: {
    color: AppTheme.color.textSubtle,
    ...AppTheme.type.label,
  },
  guidanceText: {
    color: AppTheme.color.text,
    ...AppTheme.type.body,
  },
  endNavigationButton: {
    minHeight: 28,
    borderRadius: AppTheme.radius.pill,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    paddingHorizontal: 12,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: AppTheme.color.surface,
  },
  endNavigationText: {
    color: AppTheme.color.text,
    ...AppTheme.type.label,
  },
  reasonList: {
    marginTop: 8,
    gap: 3,
  },
  reasonRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 7,
  },
  reasonText: {
    color: AppTheme.color.textMuted,
    ...AppTheme.type.label,
  },
  detailPanel: {
    position: 'absolute',
    left: 18,
    right: 18,
    bottom: 76,
    borderRadius: AppTheme.radius.lg,
    backgroundColor: 'rgba(255,255,255,0.98)',
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    padding: 14,
    boxShadow: '0 10px 18px rgba(15, 23, 42, 0.1)',
  },
  detailHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
  },
  detailTitleBlock: {
    flex: 1,
    minWidth: 0,
  },
  detailTitle: {
    color: AppTheme.color.text,
    ...AppTheme.type.cardTitle,
  },
  detailTagRow: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: 7,
    marginTop: 8,
  },
  detailDescription: {
    marginTop: 12,
    color: AppTheme.color.text,
    ...AppTheme.type.body,
  },
  detailLocation: {
    marginTop: 10,
    color: AppTheme.color.textMuted,
    ...AppTheme.type.meta,
  },
  detailActions: {
    marginTop: 12,
    flexDirection: 'row',
  },
  detailPrimaryButton: {
    minHeight: 38,
    borderRadius: AppTheme.radius.md,
    backgroundColor: AppTheme.color.primary,
    paddingHorizontal: 14,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 7,
  },
  detailPrimaryText: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.label,
  },
});
