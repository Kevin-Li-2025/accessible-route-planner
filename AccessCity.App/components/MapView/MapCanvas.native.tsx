import React from 'react';
import MapView, { Marker, Polyline } from 'react-native-maps';
import { StyleSheet, View } from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
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
  const routeCoordinates = routeGeoJSON?.coordinates?.map((c: [number, number]) => ({
    longitude: c[0],
    latitude: c[1]
  })) || [];
  function renderHazardMarker(hazard: Hazard) {
    if (hazard.type === 'wheelchair') {
      return (
        <View style={styles.markerOuterBlue}>
          <View style={styles.markerInnerBlue}>
            <MaterialCommunityIcons
              name="wheelchair-accessibility"
              size={22}
              color="#2563EB"
            />
          </View>
        </View>
      );
    }

    return (
      <View style={styles.markerOuterYellow}>
        <View style={styles.markerInnerYellow}>
          <Ionicons name="bulb" size={20} color="#D97706" />
        </View>
      </View>
    );
  }

  return (
    <MapView style={styles.map} initialRegion={initialRegion}>
      {currentLocation && (
        <Marker coordinate={currentLocation} title="Current Location" pinColor="blue" />
      )}

      {destination && (
        <Marker coordinate={destination} title="Destination" pinColor="red" />
      )}

      {hazards.map((hazard) => (
        <Marker
          key={hazard.id}
          coordinate={{
            latitude: hazard.latitude,
            longitude: hazard.longitude,
          }}
          title={hazard.title}
          onPress={() => onHazardPress(hazard)}
        >
          {renderHazardMarker(hazard)}
        </Marker>
      ))}

      {routeCoordinates.length > 0 && (
        <Polyline
          coordinates={routeCoordinates}
          strokeWidth={5}
          strokeColor="#1D4ED8"
        />
      )}
    </MapView>
  );
}

const styles = StyleSheet.create({
  map: {
    flex: 1,
  },
  markerOuterBlue: {
    width: 44,
    height: 44,
    borderRadius: 22,
    backgroundColor: 'rgba(59,130,246,0.18)',
    justifyContent: 'center',
    alignItems: 'center',
  },
  markerInnerBlue: {
    width: 30,
    height: 30,
    borderRadius: 15,
    backgroundColor: '#EFF6FF',
    justifyContent: 'center',
    alignItems: 'center',
  },
  markerOuterYellow: {
    width: 44,
    height: 44,
    borderRadius: 22,
    backgroundColor: 'rgba(251,191,36,0.22)',
    justifyContent: 'center',
    alignItems: 'center',
  },
  markerInnerYellow: {
    width: 30,
    height: 30,
    borderRadius: 15,
    backgroundColor: '#FFFBEB',
    justifyContent: 'center',
    alignItems: 'center',
  },
});
