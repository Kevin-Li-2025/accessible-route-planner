import React from 'react';
import { StyleSheet, View, Text, TouchableOpacity, Pressable } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { Hazard } from './MapTypes';

type HazardPreviewCardProps = {
  visible: boolean;
  hazard: Hazard | null;
  onClose: () => void;
  onOpenDetails: () => void;
};

export default function HazardPreviewCard({
  visible,
  hazard,
  onClose,
  onOpenDetails,
}: HazardPreviewCardProps) {
  if (!visible || !hazard) return null;

  return (
    <View style={styles.hazardPreviewCard}>
      <Pressable style={styles.hazardPreviewClose} onPress={onClose}>
        <Ionicons name="close" size={18} color="#6B7280" />
      </Pressable>

      <Text style={styles.hazardPreviewLabel}>Hazard ID</Text>
      <Text style={styles.hazardPreviewTitle}>{hazard.title}</Text>

      <TouchableOpacity
        style={styles.hazardPreviewDetailsButton}
        onPress={onOpenDetails}
      >
        <Text style={styles.hazardPreviewDetailsText}>Details</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  hazardPreviewCard: {
    position: 'absolute',
    left: 16,
    top: 160,
    width: 210,
    backgroundColor: '#FFFFFF',
    borderRadius: 20,
    paddingHorizontal: 18,
    paddingVertical: 16,
    shadowColor: '#000',
    shadowOpacity: 0.12,
    shadowRadius: 12,
    shadowOffset: { width: 0, height: 4 },
    elevation: 6,
  },
  hazardPreviewClose: {
    position: 'absolute',
    top: 10,
    right: 10,
    zIndex: 1,
    width: 28,
    height: 28,
    borderRadius: 14,
    backgroundColor: '#F3F4F6',
    justifyContent: 'center',
    alignItems: 'center',
  },
  hazardPreviewLabel: {
    fontSize: 13,
    color: '#9CA3AF',
    marginBottom: 6,
  },
  hazardPreviewTitle: {
    fontSize: 18,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 14,
    paddingRight: 24,
  },
  hazardPreviewDetailsButton: {
    alignSelf: 'flex-start',
    backgroundColor: '#EFF6FF',
    borderRadius: 12,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  hazardPreviewDetailsText: {
    color: '#1D4ED8',
    fontSize: 14,
    fontWeight: '700',
  },
});
