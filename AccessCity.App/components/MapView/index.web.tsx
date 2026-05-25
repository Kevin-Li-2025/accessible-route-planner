import React, { useEffect, useMemo, useRef, useState } from 'react';
import { StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import MapLibreGL from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { DEFAULT_MAP_CENTER_LNG_LAT } from '../../constants/defaultMapRegion';
import { Hazard } from '../../models/spatial';
import { API_BASE_URL } from '../../services/api';

const TILE_URL = `${API_BASE_URL}/api/v1/tiles/{z}/{x}/{y}.pbf`;
const EMPTY_ROUTE_DATA = { type: 'FeatureCollection', features: [] };
const FALLBACK_ROUTE_PADDING = 0.0015;
const FALLBACK_SVG_STYLE: React.CSSProperties = {
  position: 'absolute',
  inset: '7% 7% 13% 7%',
  width: '86%',
  height: '80%',
  overflow: 'visible',
  pointerEvents: 'none',
};

type LngLat = [number, number];
type FallbackBounds = {
  minLng: number;
  minLat: number;
  maxLng: number;
  maxLat: number;
};

interface MapViewProps {
  centerCoordinate?: [number, number];
  markers?: Hazard[];
  routeGeoJSON?: any;
  onMarkerPress?: (hazard: Hazard) => void;
  onMapPress?: (point: { lng: number, lat: number }) => void;
  showHazards?: boolean;
}

function readRouteCoordinates(routeGeoJSON: any): LngLat[] {
  const feature = routeGeoJSON?.features?.[0];
  const coordinates = feature?.geometry?.coordinates;
  if (!Array.isArray(coordinates)) {
    return [];
  }

  return coordinates
    .filter((coord: unknown): coord is LngLat =>
      Array.isArray(coord)
      && coord.length >= 2
      && Number.isFinite(coord[0])
      && Number.isFinite(coord[1]))
    .map((coord) => [coord[0], coord[1]]);
}

function buildFallbackBounds(
  routeCoordinates: LngLat[],
  markers: Hazard[],
  centerCoordinate: [number, number]
): FallbackBounds {
  const points: LngLat[] = [
    ...routeCoordinates,
    ...markers.map((marker) => [marker.longitude, marker.latitude] as LngLat),
  ];

  if (points.length === 0) {
    points.push(centerCoordinate);
  }

  let minLng = Math.min(...points.map((point) => point[0]));
  let maxLng = Math.max(...points.map((point) => point[0]));
  let minLat = Math.min(...points.map((point) => point[1]));
  let maxLat = Math.max(...points.map((point) => point[1]));

  if (Math.abs(maxLng - minLng) < FALLBACK_ROUTE_PADDING) {
    minLng -= FALLBACK_ROUTE_PADDING;
    maxLng += FALLBACK_ROUTE_PADDING;
  }
  if (Math.abs(maxLat - minLat) < FALLBACK_ROUTE_PADDING) {
    minLat -= FALLBACK_ROUTE_PADDING;
    maxLat += FALLBACK_ROUTE_PADDING;
  }

  const lngPadding = Math.max(FALLBACK_ROUTE_PADDING, (maxLng - minLng) * 0.12);
  const latPadding = Math.max(FALLBACK_ROUTE_PADDING, (maxLat - minLat) * 0.12);

  return {
    minLng: minLng - lngPadding,
    maxLng: maxLng + lngPadding,
    minLat: minLat - latPadding,
    maxLat: maxLat + latPadding,
  };
}

function projectFallbackPoint(point: LngLat, bounds: FallbackBounds) {
  const lngSpan = bounds.maxLng - bounds.minLng || 1;
  const latSpan = bounds.maxLat - bounds.minLat || 1;
  return {
    x: ((point[0] - bounds.minLng) / lngSpan) * 100,
    y: ((bounds.maxLat - point[1]) / latSpan) * 100,
  };
}

export default function WebMapView({
  centerCoordinate = DEFAULT_MAP_CENTER_LNG_LAT,
  markers = [],
  routeGeoJSON,
  onMarkerPress,
  onMapPress,
  showHazards = true
}: MapViewProps) {
  const mapContainer = useRef<HTMLDivElement>(null);
  const map = useRef<MapLibreGL.Map | null>(null);
  const markerObjects = useRef<Record<string, MapLibreGL.Marker>>({});
  const initialCenterRef = useRef(centerCoordinate);
  const onMapPressRef = useRef(onMapPress);
  const onMarkerPressRef = useRef(onMarkerPress);
  const showHazardsRef = useRef(showHazards);
  const pendingRouteDataRef = useRef(routeGeoJSON ?? EMPTY_ROUTE_DATA);
  const hasCenteredRef = useRef(false);
  const [mapUnavailable, setMapUnavailable] = useState(false);
  const fallbackRouteCoordinates = useMemo(() => readRouteCoordinates(routeGeoJSON), [routeGeoJSON]);
  const fallbackBounds = useMemo(
    () => buildFallbackBounds(fallbackRouteCoordinates, markers, centerCoordinate),
    [centerCoordinate, fallbackRouteCoordinates, markers]
  );
  const fallbackRoutePoints = useMemo(
    () => fallbackRouteCoordinates
      .map((coordinate) => {
        const point = projectFallbackPoint(coordinate, fallbackBounds);
        return `${point.x.toFixed(2)},${point.y.toFixed(2)}`;
      })
      .join(' '),
    [fallbackBounds, fallbackRouteCoordinates]
  );

  useEffect(() => {
    onMapPressRef.current = onMapPress;
  }, [onMapPress]);

  useEffect(() => {
    onMarkerPressRef.current = onMarkerPress;
  }, [onMarkerPress]);

  useEffect(() => {
    showHazardsRef.current = showHazards;
    if (map.current?.getLayer('hazard-layer')) {
      map.current.setLayoutProperty(
        'hazard-layer',
        'visibility',
        showHazards ? 'visible' : 'none'
      );
    }
  }, [showHazards]);

  useEffect(() => {
    if (map.current || !mapContainer.current || mapUnavailable) return;

    try {
      map.current = new MapLibreGL.Map({
        container: mapContainer.current,
        style: {
          version: 8,
          sources: {
            'osm': {
              type: 'raster',
              tiles: ['https://tile.openstreetmap.org/{z}/{x}/{y}.png'],
              tileSize: 256,
              attribution: '&copy; OpenStreetMap contributors'
            }
          },
          layers: [
            {
              id: 'osm-layer',
              type: 'raster',
              source: 'osm'
            }
          ],
          center: initialCenterRef.current as [number, number],
          zoom: 13
        }
      });
    } catch (error) {
      console.warn('Map renderer unavailable; using static fallback:', error);
      setMapUnavailable(true);
      return;
    }

    map.current.on('load', () => {
      if (!map.current) return;

      // Add Vector Hazards with high visual quality
      if (showHazardsRef.current) {
        map.current.addSource('hazards', {
          type: 'vector',
          tiles: [TILE_URL]
        });

        map.current.addLayer({
          id: 'hazard-layer',
          type: 'circle',
          source: 'hazards',
          'source-layer': 'hazards',
          paint: {
            'circle-radius': 8,
            'circle-color': '#EF4444',
            'circle-stroke-width': 2,
            'circle-stroke-color': '#FFFFFF',
            'circle-opacity': 0.8
          }
        });

        map.current.on('click', 'hazard-layer', (e) => {
          if (e.features && e.features.length > 0) {
            // Internal vector hazards press handler if needed
          }
        });
      }

      // Add Route Source
      map.current.addSource('route', {
        type: 'geojson',
        data: pendingRouteDataRef.current
      });

      map.current.addLayer({
        id: 'route-layer',
        type: 'line',
        source: 'route',
        layout: {
          'line-join': 'round',
          'line-cap': 'round'
        },
        paint: {
          'line-color': '#3B82F6',
          'line-width': 5,
          'line-opacity': 0.8
        }
      });
    });

    map.current.on('click', (e) => {
      onMapPressRef.current?.({ lng: e.lngLat.lng, lat: e.lngLat.lat });
    });

    return () => {
      map.current?.remove();
    };
  }, [mapUnavailable]);

  // Update Route
  useEffect(() => {
    pendingRouteDataRef.current = routeGeoJSON ?? EMPTY_ROUTE_DATA;
    if (!map.current?.isStyleLoaded()) return;

    const source = map.current.getSource('route') as MapLibreGL.GeoJSONSource;
    source?.setData(pendingRouteDataRef.current);
  }, [routeGeoJSON]);

  // Update Markers
  useEffect(() => {
    if (!map.current) return;

    const visibleMarkerIds = new Set(showHazards ? markers.map((hazard) => String(hazard.id)) : []);

    Object.entries(markerObjects.current).forEach(([id, marker]) => {
      if (!visibleMarkerIds.has(id)) {
        marker.remove();
        delete markerObjects.current[id];
      }
    });

    if (!showHazards) {
      return;
    }

    markers.forEach(hazard => {
      const id = String(hazard.id);
      const existingMarker = markerObjects.current[id];
      if (existingMarker) {
        existingMarker.setLngLat([hazard.longitude, hazard.latitude]);
        return;
      }

      const el = document.createElement('div');
      el.className = 'custom-marker';
      el.title = hazard.title;
      el.style.width = '20px';
      el.style.height = '20px';
      el.style.borderRadius = '50%';
      el.style.backgroundColor = hazard.type === 'wheelchair' ? '#2563EB' : '#D97706';
      el.style.border = '3px solid white';
      el.style.cursor = 'pointer';
      el.onclick = (event) => {
        event.stopPropagation();
        onMarkerPressRef.current?.(hazard);
      };

      const marker = new MapLibreGL.Marker(el)
        .setLngLat([hazard.longitude, hazard.latitude])
        .addTo(map.current!);

      markerObjects.current[id] = marker;
    });
  }, [markers, showHazards]);

  // Update Center
  useEffect(() => {
    if (map.current) {
      if (!hasCenteredRef.current) {
        map.current.jumpTo({ center: centerCoordinate as [number, number] });
        hasCenteredRef.current = true;
        return;
      }

      map.current.easeTo({ center: centerCoordinate as [number, number], duration: 250 });
    }
  }, [centerCoordinate]);

  if (mapUnavailable) {
    return (
      <View
        style={styles.fallbackContainer}
        accessibilityRole="image"
        accessibilityLabel="Birmingham route map fallback"
      >
        <TouchableOpacity
          activeOpacity={0.92}
          style={styles.fallbackCanvas}
          onPress={() => onMapPress?.({ lng: centerCoordinate[0], lat: centerCoordinate[1] })}
        >
          <View style={styles.fallbackGridHorizontal} />
          <View style={styles.fallbackGridVertical} />
          {fallbackRoutePoints ? (
            <svg
              aria-hidden="true"
              viewBox="0 0 100 100"
              preserveAspectRatio="none"
              style={FALLBACK_SVG_STYLE}
            >
              <polyline
                points={fallbackRoutePoints}
                fill="none"
                stroke="rgba(47, 128, 237, 0.18)"
                strokeWidth="3.4"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
              <polyline
                points={fallbackRoutePoints}
                fill="none"
                stroke="#2F80ED"
                strokeWidth="1.15"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          ) : null}
          {showHazards ? markers.slice(0, 24).map((hazard) => {
            const point = projectFallbackPoint([hazard.longitude, hazard.latitude], fallbackBounds);
            return (
              <TouchableOpacity
                key={String(hazard.id)}
                activeOpacity={0.84}
                accessibilityRole="button"
                accessibilityLabel={`Open ${hazard.title}`}
                onPress={(event) => {
                  event.stopPropagation();
                  onMarkerPress?.(hazard);
                }}
                style={[
                  styles.fallbackMarker,
                  {
                    left: `${Math.min(96, Math.max(4, point.x))}%`,
                    top: `${Math.min(94, Math.max(6, point.y))}%`,
                  },
                ]}
              />
            );
          }) : null}
          <View style={styles.fallbackLabel}>
            <Text style={styles.fallbackTitle}>Map view</Text>
            <Text style={styles.fallbackSubtitle}>Route and reports remain available</Text>
          </View>
        </TouchableOpacity>
      </View>
    );
  }

  return (
    <View style={styles.container}>
      <div ref={mapContainer} style={{ width: '100%', height: '100%' }} />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },
  fallbackContainer: {
    flex: 1,
    backgroundColor: '#EDE7DD',
  },
  fallbackCanvas: {
    flex: 1,
    position: 'relative',
    overflow: 'hidden',
    backgroundColor: '#EDE7DD',
  },
  fallbackGridHorizontal: {
    position: 'absolute',
    left: '-10%',
    right: '-10%',
    top: '42%',
    height: 96,
    borderTopWidth: 1,
    borderBottomWidth: 1,
    borderColor: 'rgba(26, 23, 16, 0.12)',
    transform: [{ rotate: '-9deg' }],
  },
  fallbackGridVertical: {
    position: 'absolute',
    top: '-10%',
    bottom: '-10%',
    left: '54%',
    width: 92,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    borderColor: 'rgba(26, 23, 16, 0.1)',
    transform: [{ rotate: '18deg' }],
  },
  fallbackMarker: {
    position: 'absolute',
    width: 18,
    height: 18,
    borderRadius: 9,
    backgroundColor: '#E85D2A',
    borderWidth: 3,
    borderColor: '#FFFDF7',
    shadowColor: '#1A1710',
    shadowOpacity: 0.16,
    shadowRadius: 10,
    shadowOffset: { width: 0, height: 3 },
  },
  fallbackLabel: {
    position: 'absolute',
    left: 18,
    bottom: 112,
    maxWidth: 230,
    borderRadius: 18,
    paddingHorizontal: 14,
    paddingVertical: 12,
    backgroundColor: 'rgba(255, 253, 247, 0.88)',
    borderWidth: 1,
    borderColor: 'rgba(26, 23, 16, 0.08)',
  },
  fallbackTitle: {
    fontSize: 14,
    lineHeight: 18,
    fontWeight: '800',
    color: '#1A1710',
  },
  fallbackSubtitle: {
    marginTop: 2,
    fontSize: 12,
    lineHeight: 16,
    fontWeight: '600',
    color: '#6D665C',
  },
});
