import React from 'react';
import { View, StyleSheet } from 'react-native';
import WebMapView from './index.web';
import { Coordinate, Hazard } from './MapTypes';

type MapCanvasProps = {
  initialRegion: {
    latitude: number;
    longitude: number;
    latitudeDelta: number;
    longitudeDelta: number;
  };
  currentLocation: Coordinate | null;
  destination: Coordinate | null;
  hazards: Hazard[];
  routeGeoJSON: any;
  onHazardPress: (hazard: Hazard) => void;
};

export default function MapCanvas({
  initialRegion,
  currentLocation,
  destination,
  hazards,
  routeGeoJSON,
  onHazardPress,
}: MapCanvasProps) {
  // Convert props to WebMapView format
  const center: [number, number] = destination 
    ? [destination.longitude, destination.latitude]
    : currentLocation 
      ? [currentLocation.longitude, currentLocation.latitude]
      : [initialRegion.longitude, initialRegion.latitude];

  return (
    <View style={styles.container}>
      <WebMapView 
        centerCoordinate={center}
        markers={hazards}
        routeGeoJSON={routeGeoJSON}
        onMarkerPress={onHazardPress}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
  },
});
