import React from 'react';
import { StyleSheet, View, Text, TouchableOpacity, type DimensionValue } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { AppTheme } from '@/constants/theme';
import type { RoutePerformanceDiagnostics } from '@/services/routing.service';

type RouteInfoCardProps = {
  visible: boolean;
  travelTime: string;
  distance: string;
  safetyScore: string;
  optionCount?: number;
  warnings?: string[];
  explanation?: string | null;
  performance?: RoutePerformanceDiagnostics | null;
  onPressRoute: () => void;
  onStartNavigation: () => void;
};

export default function RouteInfoCard({
  visible,
  travelTime,
  distance,
  safetyScore,
  optionCount = 0,
  warnings = [],
  explanation,
  performance,
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
  const engineLatency =
    typeof performance?.searchMilliseconds === 'number'
      ? `${performance.searchMilliseconds.toFixed(performance.searchMilliseconds < 10 ? 2 : 1)} ms`
      : '--';
  const nodesExpanded =
    typeof performance?.nodesExpanded === 'number'
      ? performance.nodesExpanded.toLocaleString()
      : '--';
  const riskLookups =
    typeof performance?.riskLookups === 'number'
      ? performance.riskLookups.toLocaleString()
      : '--';

  if (!visible) {
    return (
      <View style={styles.compactCard}>
        <View style={styles.leftIconCircle}>
          <Ionicons name="navigate-outline" size={24} color={AppTheme.color.textInverse} />
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

      <Text style={styles.recommendedText}>
        {optionCount > 0 ? `Recommended from ${optionCount + 1} options` : 'Recommended route'}
      </Text>
      <Text style={styles.destinationTitle}>User&apos;s destination</Text>

      <View style={styles.scoreCard}>
        <View style={styles.scoreHeaderRow}>
          <View style={styles.scoreLabelRow}>
            <Ionicons name="shield-outline" size={20} color={AppTheme.color.textInverse} />
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
          <Ionicons name="warning-outline" size={16} color={AppTheme.color.textInverse} />
          <Text style={styles.warningText} numberOfLines={2}>
            {warnings[0] || 'Route checked against current hazards and accessibility preferences'}
          </Text>
        </View>
      </View>

      <View style={styles.statsRow}>
        <View style={styles.statItem}>
          <Ionicons name="time-outline" size={22} color={AppTheme.color.primary} />
          <Text style={styles.statValue}>{travelTime || '--'}</Text>
          <Text style={styles.statLabel}>Est. Time</Text>
        </View>

        <View style={styles.statItem}>
          <Ionicons name="walk-outline" size={22} color={AppTheme.color.primary} />
          <Text style={styles.statValue}>{distance || '--'}</Text>
          <Text style={styles.statLabel}>Distance</Text>
        </View>

        <View style={styles.statItem}>
          <Ionicons name="people-outline" size={22} color={AppTheme.color.primary} />
          <Text style={styles.statValue}>{riskLevel}</Text>
          <Text style={styles.statLabel}>Risk Level</Text>
        </View>
      </View>

      <View style={styles.accessibilityBox}>
        <View style={styles.accessibilityIconBox}>
          <Ionicons name="accessibility-outline" size={22} color={AppTheme.color.primary} />
        </View>

        <View style={styles.accessibilityTextWrap}>
          <Text style={styles.accessibilityTitle}>Wheelchair Accessible Route</Text>
          <Text style={styles.accessibilitySubtitle}>
            {explanation || 'This route prioritizes step-free paths, smoother surfaces, and current safety reports.'}
          </Text>
        </View>
      </View>

      <View style={styles.engineBox}>
        <View style={styles.engineHeader}>
          <Ionicons name="speedometer-outline" size={18} color={AppTheme.color.primary} />
          <Text style={styles.engineTitle}>Engine diagnostics</Text>
        </View>

        <View style={styles.engineMetricsRow}>
          <View style={styles.engineMetric}>
            <Text style={styles.engineMetricValue}>{engineLatency}</Text>
            <Text style={styles.engineMetricLabel}>Search</Text>
          </View>
          <View style={styles.engineMetric}>
            <Text style={styles.engineMetricValue}>{nodesExpanded}</Text>
            <Text style={styles.engineMetricLabel}>Nodes</Text>
          </View>
          <View style={styles.engineMetric}>
            <Text style={styles.engineMetricValue}>{riskLookups}</Text>
            <Text style={styles.engineMetricLabel}>Risk lookups</Text>
          </View>
        </View>
      </View>

      <TouchableOpacity
        style={styles.startNavigationButton}
        onPress={onStartNavigation}
      >
        <Ionicons name="paper-plane-outline" size={20} color={AppTheme.color.textInverse} />
        <Text style={styles.startNavigationText}>Start Navigation</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  compactCard: {
    backgroundColor: AppTheme.color.primaryDark,
    borderRadius: AppTheme.radius.xl,
    paddingHorizontal: AppTheme.space.lg,
    paddingVertical: AppTheme.space.lg,
    flexDirection: 'row',
    alignItems: 'center',
    minHeight: 100,
    ...AppTheme.shadow.floating,
  },

  expandedCard: {
    backgroundColor: AppTheme.color.surface,
    borderRadius: AppTheme.radius.xl,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    padding: AppTheme.space.lg,
    ...AppTheme.shadow.floating,
  },

  handleBar: {
    width: 56,
    height: 5,
    borderRadius: 999,
    backgroundColor: AppTheme.color.borderStrong,
    alignSelf: 'center',
    marginBottom: 14,
  },

  recommendedText: {
    color: AppTheme.color.textMuted,
    marginBottom: 6,
    ...AppTheme.type.body,
  },

  destinationTitle: {
    color: AppTheme.color.text,
    marginBottom: 14,
    ...AppTheme.type.sectionTitle,
  },

  leftIconCircle: {
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: 'rgba(255,255,255,0.16)',
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 14,
  },

  textSection: {
    flex: 1,
    paddingRight: 10,
  },

  routeTitle: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.sectionTitle,
  },

  routeSubtitle: {
    color: AppTheme.color.primaryMuted,
    marginTop: 4,
    ...AppTheme.type.body,
  },

  routeActionButton: {
    minWidth: 92,
    backgroundColor: 'rgba(255,255,255,0.16)',
    paddingHorizontal: 18,
    paddingVertical: 13,
    borderRadius: AppTheme.radius.lg,
    justifyContent: 'center',
    alignItems: 'center',
  },

  routeActionText: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.cardTitle,
  },

  scoreCard: {
    backgroundColor: AppTheme.color.accent,
    borderRadius: AppTheme.radius.lg,
    padding: AppTheme.space.lg,
    marginBottom: AppTheme.space.lg,
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
    color: AppTheme.color.textInverse,
    marginLeft: 8,
    ...AppTheme.type.cardTitle,
  },

  optionsChip: {
    backgroundColor: 'rgba(255,255,255,0.18)',
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: AppTheme.radius.md,
  },

  optionsChipText: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.meta,
  },

  scoreMainRow: {
    flexDirection: 'row',
    alignItems: 'flex-end',
    marginTop: 14,
  },

  scoreValue: {
    color: AppTheme.color.textInverse,
    fontSize: 50,
    fontWeight: '800',
    lineHeight: 54,
  },

  scoreOutOf: {
    color: AppTheme.color.textInverse,
    fontSize: 18,
    fontWeight: '700',
    marginLeft: 4,
    marginBottom: 6,
  },

  scoreStatus: {
    color: AppTheme.color.textInverse,
    marginTop: 6,
    marginBottom: 10,
    ...AppTheme.type.cardTitle,
  },

  metricRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginBottom: 8,
  },

  metricLabel: {
    color: AppTheme.color.accentSoft,
    ...AppTheme.type.meta,
  },

  metricValue: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.meta,
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
    backgroundColor: AppTheme.color.textInverse,
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
    color: AppTheme.color.textInverse,
    marginLeft: 8,
    ...AppTheme.type.meta,
  },

  statsRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    marginBottom: AppTheme.space.lg,
  },

  statItem: {
    flex: 1,
    alignItems: 'center',
  },

  statValue: {
    marginTop: 8,
    color: AppTheme.color.text,
    ...AppTheme.type.cardTitle,
  },

  statLabel: {
    marginTop: 4,
    color: AppTheme.color.textMuted,
    ...AppTheme.type.meta,
  },

  accessibilityBox: {
    backgroundColor: AppTheme.color.primarySoft,
    borderRadius: AppTheme.radius.lg,
    padding: AppTheme.space.lg,
    flexDirection: 'row',
    alignItems: 'flex-start',
    marginBottom: AppTheme.space.lg,
  },

  accessibilityIconBox: {
    width: 40,
    height: 40,
    borderRadius: 12,
    backgroundColor: AppTheme.color.surface,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 12,
  },

  accessibilityTextWrap: {
    flex: 1,
  },

  accessibilityTitle: {
    color: AppTheme.color.text,
    marginBottom: 4,
    ...AppTheme.type.cardTitle,
  },

  accessibilitySubtitle: {
    color: AppTheme.color.textMuted,
    ...AppTheme.type.meta,
  },

  engineBox: {
    backgroundColor: AppTheme.color.surfaceSubtle,
    borderRadius: AppTheme.radius.lg,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    padding: AppTheme.space.md,
    marginBottom: AppTheme.space.lg,
  },

  engineHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 10,
  },

  engineTitle: {
    color: AppTheme.color.text,
    marginLeft: 8,
    ...AppTheme.type.cardTitle,
  },

  engineMetricsRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
  },

  engineMetric: {
    flex: 1,
  },

  engineMetricValue: {
    color: AppTheme.color.text,
    ...AppTheme.type.cardTitle,
  },

  engineMetricLabel: {
    color: AppTheme.color.textMuted,
    marginTop: 3,
    ...AppTheme.type.meta,
  },

  startNavigationButton: {
    backgroundColor: AppTheme.color.primary,
    borderRadius: AppTheme.radius.lg,
    minHeight: 58,
    flexDirection: 'row',
    justifyContent: 'center',
    alignItems: 'center',
  },

  startNavigationText: {
    color: AppTheme.color.textInverse,
    marginLeft: 8,
    ...AppTheme.type.sectionTitle,
  },
});
