import React from 'react';
import { StyleSheet, View } from 'react-native';
import MapboxGL from '@maplibre/maplibre-react-native';

// Standard practice for MapLibre on Native
MapboxGL.setAccessToken(null);

const TILE_URL = 'http://localhost:5000/api/tiles/{z}/{x}/{y}.pbf';

export default function NativeMapView() {
  return (
    <View style={styles.container}>
      <MapboxGL.MapView style={styles.map} logoEnabled={false}>
        <MapboxGL.Camera
          zoomLevel={13}
          centerCoordinate={[-1.8904, 52.4862]}
        />
        
        {/* Raster OSM Base Layer */}
        <MapboxGL.RasterSource id="osm" url="https://tile.openstreetmap.org/{z}/{x}/{y}.png" tileSize={256}>
          <MapboxGL.RasterLayer id="osmLayer" sourceID="osm" />
        </MapboxGL.RasterSource>

        {/* Vector Hazard Layer from Backend */}
        <MapboxGL.VectorSource id="hazards" url={TILE_URL}>
          <MapboxGL.CircleLayer
            id="hazardLayer"
            sourceID="hazards"
            sourceLayerID="hazards"
            style={{
              circleRadius: 6,
              circleColor: '#EF4444',
              circleStrokeWidth: 2,
              circleStrokeColor: '#FFFFFF',
            }}
          />
        </MapboxGL.VectorSource>
      </MapboxGL.MapView>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },
  map: {
    flex: 1,
  },
});
