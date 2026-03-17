import React from 'react';
import { StyleSheet, View, Text, TouchableOpacity } from 'react-native';
import { Ionicons } from '@expo/vector-icons';

type RouteInfoCardProps = {
  travelTime: string;
  distance: string;
  safetyScore: string;
  onPressRoute: () => void;
};

export default function RouteInfoCard({
  travelTime,
  distance,
  safetyScore,
  onPressRoute,
}: RouteInfoCardProps) {
  return (
    <View style={styles.routeCard}>
      <View style={styles.leftIconCircle}>
        <Ionicons name="navigate-outline" size={24} color="#FFFFFF" />
      </View>

      <View style={styles.textSection}>
        <Text style={styles.routeTitle}>{travelTime || 'Route ready'}</Text>

        <Text style={styles.routeSubtitle}>
          {distance || '--'}
          {' • '}
          {safetyScore ? `Safety: ${safetyScore}` : 'Safety: --'}
        </Text>
      </View>

      <TouchableOpacity style={styles.routeActionButton} onPress={onPressRoute}>
        <Text style={styles.routeActionText}>Route</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  routeCard: {
    backgroundColor: '#184A8C',
    borderRadius: 24,
    paddingHorizontal: 16,
    paddingVertical: 16,
    elevation: 6,
    flexDirection: 'row',
    alignItems: 'center',
    minHeight: 100,
  },

  leftIconCircle: {
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: 'rgba(255,255,255,0.14)',
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 14,
  },

  textSection: {
    flex: 1,
    paddingRight: 10,
  },

  routeTitle: {
    color: '#FFFFFF',
    fontSize: 21,
    fontWeight: '800',
  },

  routeSubtitle: {
    color: '#DCE8FF',
    fontSize: 15,
    marginTop: 4,
    lineHeight: 21,
  },

  routeActionButton: {
    minWidth: 92,
    backgroundColor: 'rgba(255,255,255,0.16)',
    paddingHorizontal: 18,
    paddingVertical: 13,
    borderRadius: 18,
    marginLeft: 12,
    justifyContent: 'center',
    alignItems: 'center',
  },

  routeActionText: {
    color: '#FFFFFF',
    fontSize: 17,
    fontWeight: '700',
  },
});