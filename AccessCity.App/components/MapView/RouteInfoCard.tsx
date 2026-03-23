import React from 'react';
import { StyleSheet, View, Text, TouchableOpacity, type DimensionValue } from 'react-native';
import { Ionicons } from '@expo/vector-icons';

type RouteInfoCardProps = {
  visible: boolean;
  travelTime: string;
  distance: string;
  safetyScore: string;
  onPressRoute: () => void;
  onStartNavigation: () => void;
};

export default function RouteInfoCard({
  visible,
  travelTime,
  distance,
  safetyScore,
  onPressRoute,
  onStartNavigation,
}: RouteInfoCardProps) {
  const numericSafetyScore = safetyScore
    ? Number(safetyScore.replace('%', ''))
    : null;

  const safetyStatus =
    numericSafetyScore === null || Number.isNaN(numericSafetyScore)
      ? '--'
      : numericSafetyScore >= 80
      ? 'Good'
      : numericSafetyScore >= 65
      ? 'Moderate'
      : 'Low';

  const riskLevel =
    numericSafetyScore === null || Number.isNaN(numericSafetyScore)
      ? '--'
      : numericSafetyScore >= 80
      ? 'Low'
      : numericSafetyScore >= 65
      ? 'Moderate'
      : 'High';

  const progressWidth =
    numericSafetyScore === null || Number.isNaN(numericSafetyScore)
      ? '0%'
      : `${Math.max(0, Math.min(100, numericSafetyScore))}%`;

  if (!visible) {
    return (
      <View style={styles.compactCard}>
        <View style={styles.leftIconCircle}>
          <Ionicons name="navigate-outline" size={24} color="#FFFFFF" />
        </View>

        <View style={styles.textSection}>
          <Text style={styles.routeTitle}>Ready to plan route</Text>
          <Text style={styles.routeSubtitle}>
            Tap Route to load route details
          </Text>
        </View>

        <TouchableOpacity style={styles.routeActionButton} onPress={onPressRoute}>
          <Text style={styles.routeActionText}>Route</Text>
        </TouchableOpacity>
      </View>
    );
  }

  return (
    <View style={styles.expandedCard}>
      <View style={styles.handleBar} />

      <Text style={styles.recommendedText}>Recommended route</Text>
      <Text style={styles.destinationTitle}>User&apos;s destination</Text>

      <View style={styles.scoreCard}>
        <View style={styles.scoreHeaderRow}>
          <View style={styles.scoreLabelRow}>
            <Ionicons name="shield-outline" size={20} color="#FFFFFF" />
            <Text style={styles.scoreLabel}>Safety Score</Text>
          </View>

          <TouchableOpacity style={styles.optionsChip} onPress={onPressRoute}>
            <Text style={styles.optionsChipText}>Refresh</Text>
          </TouchableOpacity>
        </View>

        <View style={styles.scoreMainRow}>
          <Text style={styles.scoreValue}>
            {numericSafetyScore !== null && !Number.isNaN(numericSafetyScore)
              ? numericSafetyScore
              : '--'}
          </Text>
          <Text style={styles.scoreOutOf}>/100</Text>
        </View>

        <Text style={styles.scoreStatus}>{safetyStatus}</Text>

        <View style={styles.metricRow}>
          <Text style={styles.metricLabel}>Route Safety Breakdown</Text>
          <Text style={styles.metricValue}>{safetyScore || '--'}</Text>
        </View>

        <View style={styles.progressTrack}>
          <View
            style={[
              styles.progressFill,
              { width: progressWidth as DimensionValue },
            ]}
          />
        </View>

        <View style={styles.warningBanner}>
          <Ionicons name="warning-outline" size={16} color="#FFFFFF" />
          <Text style={styles.warningText}>2 minor hazards on this route</Text>
        </View>
      </View>

      <View style={styles.statsRow}>
        <View style={styles.statItem}>
          <Ionicons name="time-outline" size={22} color="#184A8C" />
          <Text style={styles.statValue}>{travelTime || '--'}</Text>
          <Text style={styles.statLabel}>Est. Time</Text>
        </View>

        <View style={styles.statItem}>
          <Ionicons name="walk-outline" size={22} color="#184A8C" />
          <Text style={styles.statValue}>{distance || '--'}</Text>
          <Text style={styles.statLabel}>Distance</Text>
        </View>

        <View style={styles.statItem}>
          <Ionicons name="people-outline" size={22} color="#184A8C" />
          <Text style={styles.statValue}>{riskLevel}</Text>
          <Text style={styles.statLabel}>Risk Level</Text>
        </View>
      </View>

      <View style={styles.accessibilityBox}>
        <View style={styles.accessibilityIconBox}>
          <Ionicons name="accessibility-outline" size={22} color="#2563EB" />
        </View>

        <View style={styles.accessibilityTextWrap}>
          <Text style={styles.accessibilityTitle}>Wheelchair Accessible Route</Text>
          <Text style={styles.accessibilitySubtitle}>
            This route includes step-free access and accessible crossings.
          </Text>
        </View>
      </View>

      <TouchableOpacity
        style={styles.startNavigationButton}
        onPress={onStartNavigation}
      >
        <Ionicons name="paper-plane-outline" size={20} color="#FFFFFF" />
        <Text style={styles.startNavigationText}>Start Navigation</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  compactCard: {
    backgroundColor: '#184A8C',
    borderRadius: 24,
    paddingHorizontal: 16,
    paddingVertical: 16,
    elevation: 6,
    flexDirection: 'row',
    alignItems: 'center',
    minHeight: 100,
  },

  expandedCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: 28,
    padding: 18,
    elevation: 10,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: -2 },
    shadowOpacity: 0.12,
    shadowRadius: 10,
  },

  handleBar: {
    width: 56,
    height: 5,
    borderRadius: 999,
    backgroundColor: '#CBD5E1',
    alignSelf: 'center',
    marginBottom: 14,
  },

  recommendedText: {
    color: '#64748B',
    fontSize: 15,
    marginBottom: 6,
    fontWeight: '500',
  },

  destinationTitle: {
    color: '#0F172A',
    fontSize: 20,
    fontWeight: '800',
    marginBottom: 14,
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
    elevation: 4,
    justifyContent: 'center',
    alignItems: 'center',
  },

  routeActionText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },

  scoreCard: {
    backgroundColor: '#14B8A6',
    borderRadius: 20,
    padding: 16,
    marginBottom: 18,
    overflow: 'hidden',
  },

  scoreHeaderRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },

  scoreLabelRow: {
    flexDirection: 'row',
    alignItems: 'center',
  },

  scoreLabel: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    marginLeft: 8,
  },

  optionsChip: {
    backgroundColor: 'rgba(255,255,255,0.18)',
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 16,
  },

  optionsChipText: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '600',
  },

  scoreMainRow: {
    flexDirection: 'row',
    alignItems: 'flex-end',
    marginTop: 14,
  },

  scoreValue: {
    color: '#FFFFFF',
    fontSize: 50,
    fontWeight: '800',
    lineHeight: 54,
  },

  scoreOutOf: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '700',
    marginLeft: 4,
    marginBottom: 6,
  },

  scoreStatus: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    marginTop: 6,
    marginBottom: 10,
  },

  metricRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginBottom: 8,
  },

  metricLabel: {
    color: '#E6FFFB',
    fontSize: 14,
    fontWeight: '500',
  },

  metricValue: {
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '700',
  },

  progressTrack: {
    height: 8,
    backgroundColor: 'rgba(255,255,255,0.25)',
    borderRadius: 999,
    overflow: 'hidden',
    marginBottom: 14,
  },

  progressFill: {
    height: '100%',
    backgroundColor: '#FFFFFF',
    borderRadius: 999,
  },

  warningBanner: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: 'rgba(255,255,255,0.14)',
    borderRadius: 14,
    paddingHorizontal: 12,
    paddingVertical: 10,
  },

  warningText: {
    color: '#FFFFFF',
    fontSize: 14,
    marginLeft: 8,
    fontWeight: '500',
  },

  statsRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginBottom: 18,
  },

  statItem: {
    flex: 1,
    alignItems: 'center',
  },

  statValue: {
    marginTop: 8,
    fontSize: 17,
    fontWeight: '800',
    color: '#0F172A',
  },

  statLabel: {
    marginTop: 4,
    fontSize: 13,
    color: '#64748B',
  },

  accessibilityBox: {
    backgroundColor: '#EFF6FF',
    borderRadius: 18,
    padding: 14,
    flexDirection: 'row',
    alignItems: 'flex-start',
    marginBottom: 18,
  },

  accessibilityIconBox: {
    width: 40,
    height: 40,
    borderRadius: 12,
    backgroundColor: '#DBEAFE',
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 12,
  },

  accessibilityTextWrap: {
    flex: 1,
  },

  accessibilityTitle: {
    fontSize: 16,
    fontWeight: '700',
    color: '#1E293B',
    marginBottom: 4,
  },

  accessibilitySubtitle: {
    fontSize: 14,
    lineHeight: 20,
    color: '#475569',
  },

  startNavigationButton: {
    backgroundColor: '#184A8C',
    borderRadius: 18,
    minHeight: 58,
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
  },

  startNavigationText: {
    color: '#FFFFFF',
    fontSize: 18,
    fontWeight: '700',
    marginLeft: 8,
  },
});