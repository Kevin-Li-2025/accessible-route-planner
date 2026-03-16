import React, { useEffect, useState, useRef } from 'react';
import { StyleSheet, View, Text } from 'react-native';
import MapLibreGL from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';

const TILE_URL = 'http://localhost:5000/api/tiles/{z}/{x}/{y}.pbf';

export default function WebMapView() {
  const mapContainer = useRef<HTMLDivElement>(null);
  const map = useRef<MapLibreGL.Map | null>(null);

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
          },
          'hazards': {
            type: 'vector',
            tiles: [TILE_URL]
          }
        },
        layers: [
          {
            id: 'osm-layer',
            type: 'raster',
            source: 'osm'
          },
          {
            id: 'hazard-layer',
            type: 'circle',
            source: 'hazards',
            'source-layer': 'hazards',
            paint: {
              'circle-radius': 6,
              'circle-color': '#EF4444',
              'circle-stroke-width': 2,
              'circle-stroke-color': '#FFFFFF'
            }
          }
        ],
        center: [-1.8904, 52.4862],
        zoom: 13
      }
    });

    return () => {
      map.current?.remove();
    };
  }, []);

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
