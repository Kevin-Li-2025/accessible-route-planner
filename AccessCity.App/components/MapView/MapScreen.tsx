import React from 'react';
import { View, Text, StyleSheet } from 'react-native';

export default function MapScreen() {
  return (
    <View style={styles.container}>
      <Text style={styles.title}>Map Not Available on Web</Text>
      <Text style={styles.message}>
        The interactive map and routing features are optimized for mobile devices.
        Please use the Expo Go app on your phone to view the map.
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    padding: 20,
    backgroundColor: '#F8FAFC',
  },
  title: {
    fontSize: 24,
    fontWeight: 'bold',
    marginBottom: 10,
    color: '#0F3D91',
  },
  message: {
    fontSize: 16,
    textAlign: 'center',
    color: '#64748B',
  },
});
