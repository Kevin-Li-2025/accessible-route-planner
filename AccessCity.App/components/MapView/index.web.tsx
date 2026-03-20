import React, { useEffect, useRef } from 'react';
import { StyleSheet, View } from 'react-native';
import MapLibreGL from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import { Hazard } from '../../models/spatial';
import { API_BASE_URL } from '../../services/api';

const TILE_URL = `${API_BASE_URL}/api/v1/tiles/{z}/{x}/{y}.pbf`;

interface MapViewProps {
  centerCoordinate?: [number, number];
  markers?: Hazard[];
  routeGeoJSON?: any;
  onMarkerPress?: (hazard: Hazard) => void;
  onMapPress?: (point: { lng: number, lat: number }) => void;
  showHazards?: boolean;
}

export default function WebMapView({
  centerCoordinate = [-1.8904, 52.4862],
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

  useEffect(() => {
    onMapPressRef.current = onMapPress;
  }, [onMapPress]);

  useEffect(() => {
    onMarkerPressRef.current = onMarkerPress;
  }, [onMarkerPress]);

  useEffect(() => {
    showHazardsRef.current = showHazards;
  }, [showHazards]);

  useEffect(() => {
    if (map.current || !mapContainer.current) return;

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
        data: { type: 'FeatureCollection', features: [] }
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
  }, []);

  // Update Route
  useEffect(() => {
    if (!map.current?.isStyleLoaded()) return;

    const source = map.current.getSource('route') as MapLibreGL.GeoJSONSource;
    source?.setData(routeGeoJSON ?? { type: 'FeatureCollection', features: [] });
  }, [routeGeoJSON]);

  // Update Markers
  useEffect(() => {
    if (!map.current) return;

    // Clear old markers
    Object.values(markerObjects.current).forEach(m => m.remove());
    markerObjects.current = {};

    // Add new markers
    markers.forEach(hazard => {
      const el = document.createElement('div');
      el.className = 'custom-marker';
      el.style.width = '20px';
      el.style.height = '20px';
      el.style.borderRadius = '50%';
      el.style.backgroundColor = hazard.type === 'wheelchair' ? '#2563EB' : '#D97706';
      el.style.border = '3px solid white';
      el.style.cursor = 'pointer';
      el.onclick = () => onMarkerPressRef.current?.(hazard);

      const marker = new MapLibreGL.Marker(el)
        .setLngLat([hazard.longitude, hazard.latitude])
        .addTo(map.current!);

      markerObjects.current[String(hazard.id)] = marker;
    });
  }, [markers]);

  // Update Center
  useEffect(() => {
    if (map.current) {
      map.current.flyTo({ center: centerCoordinate as [number, number] });
    }
  }, [centerCoordinate]);

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
});
