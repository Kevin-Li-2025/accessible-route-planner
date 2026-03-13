import React, { useEffect, useState } from 'react';
import { StyleSheet, View, Text } from 'react-native';

const hazards = [
  {
    id: 1,
    title: 'Broken pavement',
    latitude: 52.4865,
    longitude: -1.891,
  },
  {
    id: 2,
    title: 'No wheelchair ramp',
    latitude: 52.4852,
    longitude: -1.888,
  },
];

export default function WebMapView() {
  const [LeafletComponents, setLeafletComponents] = useState<any>(null);

  useEffect(() => {
    if (typeof window !== 'undefined') {
      // Dynamic import to ensure Leaflet is only loaded on the client
      Promise.all([
        import('react-leaflet'),
        import('leaflet'),
      ]).then(([reactLeaflet, leaflet]) => {
        setLeafletComponents({ ...reactLeaflet, L: leaflet.default });
      });

      if (!document.getElementById('leaflet-css')) {
        const link = document.createElement('link');
        link.id = 'leaflet-css';
        link.rel = 'stylesheet';
        link.href = 'https://unpkg.com/leaflet@1.9.4/dist/leaflet.css';
        document.head.appendChild(link);
      }
    }
  }, []);

  if (!LeafletComponents) {
    return (
      <View style={styles.container}>
        <Text style={{ marginTop: 50, textAlign: 'center' }}>Loading Web Map...</Text>
      </View>
    );
  }

  const { MapContainer, TileLayer, Marker, Popup } = LeafletComponents;

  return (
    <View style={styles.container}>
      <MapContainer
        center={[52.4862, -1.8904]}
        zoom={13}
        style={{ height: '100%', width: '100%' }}
      >
        <TileLayer
          attribution='&copy; OpenStreetMap contributors'
          url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
        />
        {hazards.map((hazard: any) => (
          <Marker 
            key={hazard.id} 
            position={[hazard.latitude, hazard.longitude]}
          >
            <Popup>{hazard.title}</Popup>
          </Marker>
        ))}
      </MapContainer>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#fff',
  },
});
