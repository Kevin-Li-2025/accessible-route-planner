import React from 'react';
import { StyleSheet, View } from 'react-native';
import MapView, { Marker, Polyline, PROVIDER_DEFAULT } from 'react-native-maps';
import { Coordinate, Hazard } from '../../models/spatial';

interface MapViewProps {
  centerCoordinate?: [number, number];
  markers?: Hazard[];
  routeGeoJSON?: any;
  onMarkerPress?: (hazard: Hazard) => void;
  onMapPress?: (point: any) => void;
  showHazards?: boolean;
}

export default function NativeMapView({
  centerCoordinate = [-1.8904, 52.4862],
  markers = [],
  routeGeoJSON,
  onMarkerPress,
  onMapPress,
  showHazards = true
}: MapViewProps) {
  const mapRef = React.useRef<MapView>(null);

  // Parse route coordinates from GeoJSON LineString
  const routeCoordinates = React.useMemo(() => {
    if (!routeGeoJSON || !routeGeoJSON.coordinates) return [];
    return routeGeoJSON.coordinates.map((coord: [number, number]) => ({
      longitude: coord[0],
      latitude: coord[1],
    }));
  }, [routeGeoJSON]);

  // Animate to new center coordinate when it changes
  React.useEffect(() => {
    if (mapRef.current && centerCoordinate) {
      mapRef.current.animateToRegion({
        latitude: centerCoordinate[1],
        longitude: centerCoordinate[0],
        latitudeDelta: 0.05,
        longitudeDelta: 0.05,
      }, 1000);
    }
  }, [centerCoordinate]);

  const initialRegion = {
    latitude: centerCoordinate[1],
    longitude: centerCoordinate[0],
    latitudeDelta: 0.05,
    longitudeDelta: 0.05,
  };

  return (
    <View style={styles.container}>
      <MapView
        ref={mapRef}
        provider={PROVIDER_DEFAULT}
        style={styles.map}
        initialRegion={initialRegion}
        onPress={(e) => {
          onMapPress?.({
            lng: e.nativeEvent.coordinate.longitude,
            lat: e.nativeEvent.coordinate.latitude,
          });
        }}
      >
        {/* Render Hazard Markers */}
        {showHazards && markers.map((h) => (
          <Marker
            key={h.id}
            coordinate={{ latitude: h.latitude, longitude: h.longitude }}
            title={h.title}
            description={h.description}
            pinColor={h.type === 'wheelchair' ? '#2563EB' : '#D97706'}
            onPress={() => onMarkerPress?.(h)}
          />
        ))}

        {/* Render Route Polyline */}
        {routeCoordinates.length > 0 && (
          <Polyline
            coordinates={routeCoordinates}
            strokeColor="#3B82F6"
            strokeWidth={5}
          />
        )}
      </MapView>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },
  map: {
    ...StyleSheet.absoluteFillObject,
  },
});
