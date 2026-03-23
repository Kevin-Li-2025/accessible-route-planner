import React from 'react';
import { Modal, StyleSheet, View, Text, TouchableOpacity, Pressable } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { RouteFilters } from './MapTypes';

type FilterModalProps = {
  visible: boolean;
  routeFilters: RouteFilters;
  onClose: () => void;
  onToggleFilter: <K extends keyof RouteFilters>(key: K) => void;
  onAdjustMinSafety: (delta: number) => void;
  onAdjustMaxSafety: (delta: number) => void;
  onApply: () => void;
  onReset: () => void;
};

export default function FilterModal({
  visible,
  routeFilters,
  onClose,
  onToggleFilter,
  onAdjustMinSafety,
  onAdjustMaxSafety,
  onApply,
  onReset,
}: FilterModalProps) {
  return (
    <Modal visible={visible} animationType="slide" transparent onRequestClose={onClose}>
      <View style={styles.modalRoot}>
        <Pressable style={styles.overlay} onPress={onClose} />

        <View style={styles.sheetWrapper}>
          <View style={styles.filterSheet}>
            <View style={styles.dragHandle} />

            <Text style={styles.filterTitle}>Filter by:</Text>
            <Text style={styles.pilotHint}>Pilot area: Birmingham, UK — routing uses OpenStreetMap + local hazards.</Text>
            <View style={styles.sheetDivider} />

            <Text style={styles.filterSectionHeading}>Accessibility Preferences</Text>

            <View style={styles.filterCard}>
              <Text style={styles.filterCardTitle}>Filter accessibility preferences</Text>

              {[
                ['avoidSteepHills', 'Avoid steep hills'],
                ['wheelchairAccessible', 'Step-free / manual wheelchair routing'],
                ['avoidReportedHazards', 'Avoid reported hazards'],
                ['preferWellLitStreets', 'Well-lit streets'],
              ].map(([key, label]) => {
                const filterKey = key as keyof RouteFilters;
                const checked = typeof routeFilters[filterKey] === 'boolean'
                  ? (routeFilters[filterKey] as boolean)
                  : false;

                return (
                  <TouchableOpacity
                    key={key}
                    style={styles.checkboxRow}
                    onPress={() => onToggleFilter(filterKey)}
                    activeOpacity={0.8}
                  >
                    <View style={[styles.checkbox, checked && styles.checkboxChecked]}>
                      {checked && <Ionicons name="checkmark" size={14} color="#FFFFFF" />}
                    </View>
                    <Text style={styles.checkboxLabel}>{label}</Text>
                  </TouchableOpacity>
                );
              })}

              <View style={styles.filterButtonRow}>
                <TouchableOpacity style={styles.applyButton} onPress={onApply}>
                  <Text style={styles.applyButtonText}>Apply</Text>
                </TouchableOpacity>

                <TouchableOpacity style={styles.resetButton} onPress={onReset}>
                  <Text style={styles.resetButtonText}>Reset</Text>
                </TouchableOpacity>
              </View>
            </View>

            <View style={styles.sheetDividerLarge} />

            <Text style={styles.filterSectionHeading}>Safety Score</Text>

            <View style={styles.safetyPanel}>
              <View style={styles.safetyAdjustRow}>
                <Text style={styles.safetyAdjustLabel}>Min</Text>
                <View style={styles.safetyStepper}>
                  <TouchableOpacity
                    style={styles.stepperButton}
                    onPress={() => onAdjustMinSafety(-10)}
                  >
                    <Text style={styles.stepperButtonText}>-</Text>
                  </TouchableOpacity>
                  <Text style={styles.safetyValue}>{routeFilters.minSafetyScore}</Text>
                  <TouchableOpacity
                    style={styles.stepperButton}
                    onPress={() => onAdjustMinSafety(10)}
                  >
                    <Text style={styles.stepperButtonText}>+</Text>
                  </TouchableOpacity>
                </View>
              </View>

              <View style={styles.safetyAdjustRow}>
                <Text style={styles.safetyAdjustLabel}>Max</Text>
                <View style={styles.safetyStepper}>
                  <TouchableOpacity
                    style={styles.stepperButton}
                    onPress={() => onAdjustMaxSafety(-10)}
                  >
                    <Text style={styles.stepperButtonText}>-</Text>
                  </TouchableOpacity>
                  <Text style={styles.safetyValue}>{routeFilters.maxSafetyScore}</Text>
                  <TouchableOpacity
                    style={styles.stepperButton}
                    onPress={() => onAdjustMaxSafety(10)}
                  >
                    <Text style={styles.stepperButtonText}>+</Text>
                  </TouchableOpacity>
                </View>
              </View>

              <View style={styles.fakeSliderWrap}>
                <View style={styles.fakeSliderTrack} />
                <View
                  style={[
                    styles.fakeSliderActive,
                    {
                      left: `${routeFilters.minSafetyScore}%`,
                      width: `${routeFilters.maxSafetyScore - routeFilters.minSafetyScore}%`,
                    },
                  ]}
                />
                <View
                  style={[styles.fakeSliderThumb, { left: `${routeFilters.minSafetyScore}%` }]}
                />
                <View
                  style={[styles.fakeSliderThumb, { left: `${routeFilters.maxSafetyScore}%` }]}
                />
              </View>

              <View style={styles.safetyRangeLabels}>
                <Text style={styles.safetyRangeText}>0</Text>
                <Text style={styles.safetyRangeText}>100</Text>
              </View>
            </View>
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  modalRoot: { flex: 1, justifyContent: 'flex-end' },
  overlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.18)',
  },
  sheetWrapper: { width: '100%', justifyContent: 'flex-end' },
  filterSheet: {
    backgroundColor: '#FFFFFF',
    borderTopLeftRadius: 28,
    borderTopRightRadius: 28,
    paddingTop: 10,
    paddingHorizontal: 20,
    paddingBottom: 28,
    minHeight: '62%',
    shadowColor: '#000',
    shadowOpacity: 0.08,
    shadowRadius: 12,
    shadowOffset: { width: 0, height: -4 },
    elevation: 12,
  },
  dragHandle: {
    width: 48,
    height: 5,
    borderRadius: 999,
    backgroundColor: '#D1D5DB',
    alignSelf: 'center',
    marginBottom: 14,
  },
  filterTitle: {
    fontSize: 22,
    fontWeight: '500',
    color: '#4B5563',
    marginBottom: 4,
  },
  pilotHint: {
    fontSize: 13,
    color: '#6B7280',
    lineHeight: 18,
    marginBottom: 4,
  },
  sheetDivider: {
    height: 1,
    backgroundColor: '#E5E7EB',
    marginTop: 14,
    marginBottom: 16,
  },
  filterSectionHeading: {
    fontSize: 18,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 14,
  },
  filterCard: {
    borderWidth: 1.5,
    borderColor: '#E5E7EB',
    borderRadius: 18,
    backgroundColor: '#FFFFFF',
    paddingHorizontal: 16,
    paddingVertical: 16,
  },
  filterCardTitle: {
    fontSize: 15,
    color: '#9CA3AF',
    marginBottom: 14,
  },
  checkboxRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 18,
  },
  checkbox: {
    width: 28,
    height: 28,
    borderRadius: 8,
    borderWidth: 1.5,
    borderColor: '#9CA3AF',
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 14,
  },
  checkboxChecked: {
    backgroundColor: '#1D4ED8',
    borderColor: '#1D4ED8',
  },
  checkboxLabel: {
    fontSize: 16,
    color: '#374151',
    flexShrink: 1,
  },
  filterButtonRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginTop: 8,
    gap: 12,
  },
  applyButton: {
    flex: 1,
    height: 50,
    borderRadius: 14,
    backgroundColor: '#1D4ED8',
    justifyContent: 'center',
    alignItems: 'center',
  },
  applyButtonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
  resetButton: {
    flex: 1,
    height: 50,
    borderRadius: 14,
    backgroundColor: '#F3F4F6',
    justifyContent: 'center',
    alignItems: 'center',
  },
  resetButtonText: {
    color: '#6B7280',
    fontSize: 16,
    fontWeight: '500',
  },
  sheetDividerLarge: {
    height: 1,
    backgroundColor: '#E5E7EB',
    marginVertical: 22,
  },
  safetyPanel: { paddingTop: 4 },
  safetyAdjustRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: 14,
  },
  safetyAdjustLabel: {
    fontSize: 16,
    fontWeight: '600',
    color: '#111827',
  },
  safetyStepper: {
    flexDirection: 'row',
    alignItems: 'center',
  },
  stepperButton: {
    width: 34,
    height: 34,
    borderRadius: 17,
    backgroundColor: '#E5E7EB',
    justifyContent: 'center',
    alignItems: 'center',
  },
  stepperButtonText: {
    fontSize: 20,
    fontWeight: '700',
    color: '#374151',
    lineHeight: 22,
  },
  safetyValue: {
    width: 44,
    textAlign: 'center',
    fontSize: 16,
    fontWeight: '700',
    color: '#111827',
  },
  fakeSliderWrap: {
    position: 'relative',
    height: 34,
    justifyContent: 'center',
    marginTop: 10,
  },
  fakeSliderTrack: {
    height: 6,
    borderRadius: 999,
    backgroundColor: '#D1D5DB',
    width: '100%',
  },
  fakeSliderActive: {
    position: 'absolute',
    height: 6,
    borderRadius: 999,
    backgroundColor: '#1D4ED8',
  },
  fakeSliderThumb: {
    position: 'absolute',
    marginLeft: -10,
    width: 22,
    height: 22,
    borderRadius: 11,
    backgroundColor: '#1D4ED8',
    borderWidth: 3,
    borderColor: '#FFFFFF',
    top: 6,
  },
  safetyRangeLabels: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginTop: 6,
  },
  safetyRangeText: {
    fontSize: 15,
    color: '#111827',
  },
});
