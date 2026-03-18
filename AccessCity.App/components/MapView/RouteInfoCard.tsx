import React from 'react';
import { StyleSheet, View, Text } from 'react-native';

type RouteInfoCardProps = {
  travelTime: string;
  distance: string;
  safetyScore: string;
};

export default function RouteInfoCard({
  travelTime,
  distance,
  safetyScore,
}: RouteInfoCardProps) {
  if (!travelTime && !distance && !safetyScore) return null;

  return (
    <View style={styles.routeCard}>
      <Text style={styles.routeTitle}>{travelTime || 'Route ready'}</Text>
      <Text style={styles.routeSubtitle}>
        {distance ? `${distance}` : ''}
        {distance && safetyScore ? ' • ' : ''}
        {safetyScore ? `Safety: ${safetyScore}` : ''}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  routeCard: {
    position: 'absolute',
    bottom: 30,
    left: 16,
    right: 16,
    backgroundColor: '#1D4ED8',
    borderRadius: 18,
    padding: 16,
    elevation: 4,
  },
  routeTitle: {
    color: '#fff',
    fontSize: 24,
    fontWeight: '700',
  },
  routeSubtitle: {
    color: '#E5E7EB',
    fontSize: 14,
    marginTop: 4,
  },
});
