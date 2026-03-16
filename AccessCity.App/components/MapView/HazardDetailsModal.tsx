import React from 'react';
import { Modal, StyleSheet, View, Text, TouchableOpacity, Pressable } from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import { Hazard } from './MapTypes';

type HazardDetailsModalProps = {
  visible: boolean;
  hazard: Hazard | null;
  onClose: () => void;
};

export default function HazardDetailsModal({
  visible,
  hazard,
  onClose,
}: HazardDetailsModalProps) {
  return (
    <Modal
      visible={visible}
      animationType="fade"
      transparent
      onRequestClose={onClose}
    >
      <View style={styles.detailModalRoot}>
        <Pressable style={styles.detailOverlay} onPress={onClose} />

        <View style={styles.hazardDetailCard}>
          <View style={styles.hazardDetailHeader}>
            <View
              style={[
                styles.hazardDetailIconBox,
                hazard?.type === 'wheelchair'
                  ? styles.hazardDetailIconBlue
                  : styles.hazardDetailIconYellow,
              ]}
            >
              {hazard?.type === 'wheelchair' ? (
                <MaterialCommunityIcons
                  name="wheelchair-accessibility"
                  size={28}
                  color="#2563EB"
                />
              ) : (
                <Ionicons name="bulb-outline" size={28} color="#D97706" />
              )}
            </View>

            <View style={styles.hazardDetailHeaderText}>
              <Text style={styles.hazardDetailTitle}>{hazard?.title}</Text>

              <View style={styles.hazardStatusBadge}>
                <Text style={styles.hazardStatusText}>
                  Status: {hazard?.status}
                </Text>
              </View>
            </View>
          </View>

          <View style={styles.hazardDetailDivider} />

          <Text style={styles.hazardDetailSectionLabel}>Description</Text>
          <Text style={styles.hazardDetailDescription}>
            {hazard?.description}
          </Text>

          <View style={styles.hazardMetaRow}>
            <View style={styles.hazardMetaItem}>
              <Ionicons name="location-outline" size={20} color="#EF4444" />
              <Text style={styles.hazardMetaTitle}>Location</Text>
              <Text style={styles.hazardMetaText}>{hazard?.locationText}</Text>
            </View>

            <View style={styles.hazardMetaItem}>
              <Ionicons name="time-outline" size={20} color="#9CA3AF" />
              <Text style={styles.hazardMetaTitle}>Reported</Text>
              <Text style={styles.hazardMetaText}>{hazard?.reportedTime}</Text>
            </View>
          </View>

          <View style={styles.hazardActionRow}>
            <TouchableOpacity style={styles.avoidRouteButton}>
              <Ionicons name="navigate-outline" size={18} color="#FFFFFF" />
              <Text style={styles.avoidRouteButtonText}>Avoid in Route</Text>
            </TouchableOpacity>

            <TouchableOpacity style={styles.detailSecondaryButton} onPress={onClose}>
              <Ionicons name="chevron-forward" size={18} color="#4B5563" />
              <Text style={styles.detailSecondaryButtonText}>Close</Text>
            </TouchableOpacity>
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  detailModalRoot: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 24,
  },
  detailOverlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.35)',
  },
  hazardDetailCard: {
    width: '100%',
    backgroundColor: '#FFFFFF',
    borderRadius: 28,
    paddingHorizontal: 22,
    paddingVertical: 22,
    shadowColor: '#000',
    shadowOpacity: 0.12,
    shadowRadius: 18,
    shadowOffset: { width: 0, height: 6 },
    elevation: 12,
  },
  hazardDetailHeader: {
    flexDirection: 'row',
    alignItems: 'center',
  },
  hazardDetailIconBox: {
    width: 68,
    height: 68,
    borderRadius: 18,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 14,
  },
  hazardDetailIconYellow: {
    backgroundColor: '#FEF3C7',
  },
  hazardDetailIconBlue: {
    backgroundColor: '#DBEAFE',
  },
  hazardDetailHeaderText: {
    flex: 1,
  },
  hazardDetailTitle: {
    fontSize: 18,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 8,
  },
  hazardStatusBadge: {
    alignSelf: 'flex-start',
    backgroundColor: '#FEF3C7',
    borderRadius: 999,
    paddingHorizontal: 14,
    paddingVertical: 7,
  },
  hazardStatusText: {
    fontSize: 14,
    fontWeight: '600',
    color: '#D97706',
  },
  hazardDetailDivider: {
    height: 1,
    backgroundColor: '#E5E7EB',
    marginVertical: 18,
  },
  hazardDetailSectionLabel: {
    fontSize: 15,
    color: '#6B7280',
    marginBottom: 8,
  },
  hazardDetailDescription: {
    fontSize: 16,
    lineHeight: 24,
    color: '#111827',
    marginBottom: 22,
  },
  hazardMetaRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginBottom: 24,
    gap: 16,
  },
  hazardMetaItem: {
    flex: 1,
  },
  hazardMetaTitle: {
    fontSize: 15,
    color: '#9CA3AF',
    marginTop: 6,
    marginBottom: 6,
  },
  hazardMetaText: {
    fontSize: 15,
    color: '#111827',
    lineHeight: 22,
  },
  hazardActionRow: {
    flexDirection: 'row',
    gap: 12,
  },
  avoidRouteButton: {
    flex: 1,
    height: 54,
    borderRadius: 16,
    backgroundColor: '#1D4ED8',
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
    gap: 8,
  },
  avoidRouteButtonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
  detailSecondaryButton: {
    flex: 1,
    height: 54,
    borderRadius: 16,
    backgroundColor: '#F3F4F6',
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
    gap: 6,
  },
  detailSecondaryButtonText: {
    color: '#4B5563',
    fontSize: 16,
    fontWeight: '600',
  },
});
