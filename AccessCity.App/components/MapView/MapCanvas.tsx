import React, { RefObject } from 'react';
import MapView, { Marker, Polyline } from 'react-native-maps';
import { StyleSheet, View } from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import { Coordinate, Hazard } from './MapTypes';

type MapCanvasProps = {
  mapRef?: RefObject<MapView | null>;
  initialRegion: {
    latitude: number;
    longitude: number;
    latitudeDelta: number;
    longitudeDelta: number;
  };
  currentLocation: Coordinate | null;
  destination: Coordinate | null;
  hazards: Hazard[];
  routeCoordinates: Coordinate[];
  navigationMode: boolean;
  onHazardPress: (hazard: Hazard) => void;
};

export default function MapCanvas({
  mapRef,
  initialRegion,
  currentLocation,
  destination,
  hazards,
  routeCoordinates,
  navigationMode,
  onHazardPress,
}: MapCanvasProps) {
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
    <MapView
      ref={mapRef}
      style={styles.map}
      initialRegion={initialRegion}
      mapType="mutedStandard"
      showsUserLocation
      showsMyLocationButton={false}
      followsUserLocation={false}
      rotateEnabled={true}
      pitchEnabled={true}
      scrollEnabled={true}
      zoomEnabled={true}
      showsCompass={navigationMode}
      toolbarEnabled={false}
    >
      {!navigationMode && currentLocation && (
        <Marker coordinate={currentLocation} title="Current Location" pinColor="blue" />
      )}

      {destination && !navigationMode && (
        <Marker coordinate={destination} title="Destination">
          <View style={styles.destinationMarkerOuter}>
            <View style={styles.destinationMarkerInner}>
              <Ionicons name="location" size={22} color="#DC2626" />
            </View>
          </View>
        </Marker>
      )}

      {!navigationMode &&
        hazards.map((hazard) => (
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
          strokeWidth={6}
          strokeColor="#1D4ED8"
          lineCap="round"
          lineJoin="round"
        />
      )}
    </MapView>
  );
}

const styles = StyleSheet.create({
  map: {
    flex: 1,
  },

  destinationMarkerOuter: {
    width: 46,
    height: 46,
    borderRadius: 23,
    backgroundColor: 'rgba(239,68,68,0.18)',
    justifyContent: 'center',
    alignItems: 'center',
  },

  destinationMarkerInner: {
    width: 32,
    height: 32,
    borderRadius: 16,
    backgroundColor: '#FEF2F2',
    justifyContent: 'center',
    alignItems: 'center',
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