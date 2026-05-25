import React from 'react';
import {
  Image,
  type ImageSourcePropType,
  StyleSheet,
  Text,
  TouchableOpacity,
  useWindowDimensions,
  View,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';

import { AppTheme } from '@/constants/theme';

type ScenicHeaderProps = {
  eyebrow: string;
  title: string;
  subtitle?: string;
  actionLabel?: string;
  actionIcon?: React.ComponentProps<typeof Ionicons>['name'];
  onActionPress?: () => void;
  meta?: string;
  artwork?: keyof typeof HEADER_ARTWORK;
};

const HEADER_ARTWORK = {
  city: require('@/assets/design/ctrlv-city-skyline.png') as ImageSourcePropType,
  dashboard: require('@/assets/design/ctrlv-saas-dashboard.png') as ImageSourcePropType,
  journey: require('@/assets/design/ctrlv-design-user-journey.png') as ImageSourcePropType,
  map: require('@/assets/design/ctrlv-mobile-map.png') as ImageSourcePropType,
  walk: require('@/assets/design/ctrlv-walking-together.png') as ImageSourcePropType,
};

export function ScenicHeader({
  eyebrow,
  title,
  subtitle,
  actionLabel,
  actionIcon = 'arrow-forward',
  onActionPress,
  meta,
  artwork = 'journey',
}: ScenicHeaderProps) {
  const { width } = useWindowDimensions();
  const isCompact = width < 560;

  return (
    <View style={styles.shell}>
      <LinearGradient
        colors={[AppTheme.color.surface, AppTheme.color.skySoft, AppTheme.color.surface]}
        locations={[0, 0.48, 1]}
        style={[styles.canvas, isCompact && styles.canvasCompact]}
      >
        <View style={[styles.gridLineOne, styles.nonInteractive]} />
        <View style={[styles.gridLineTwo, styles.nonInteractive]} />
        <View style={[styles.gridLineThree, styles.nonInteractive]} />
        <View style={[styles.artworkHalo, styles.nonInteractive]} />
        <View
          style={[styles.artworkLayer, isCompact && styles.artworkLayerCompact, styles.nonInteractive]}
        >
          <Image
            accessibilityIgnoresInvertColors
            resizeMode="contain"
            source={HEADER_ARTWORK[artwork]}
            style={styles.artwork}
          />
        </View>
        {!isCompact ? (
          <View style={[styles.panelPreview, styles.nonInteractive]}>
            <View style={styles.previewTopRow}>
              <View style={styles.previewDot} />
              <View style={styles.previewDotMuted} />
              <View style={styles.previewDotSoft} />
            </View>
            <View style={styles.previewRoute} />
            <View style={styles.previewRow} />
            <View style={[styles.previewRow, styles.previewRowShort]} />
            <View style={styles.previewMetricRow}>
              <View style={styles.previewMetric} />
              <View style={styles.previewMetric} />
            </View>
          </View>
        ) : null}

        <View style={styles.navPill}>
          <View style={styles.brandMark}>
            <Ionicons name="navigate" size={13} color={AppTheme.color.textInverse} />
          </View>
          <Text style={styles.brandText}>AccessCity</Text>
          {meta ? <Text style={styles.metaText}>{meta}</Text> : null}
        </View>

        <View style={styles.copy}>
          <Text style={styles.eyebrow}>{eyebrow}</Text>
          <Text style={[styles.title, isCompact && styles.titleCompact]}>{title}</Text>
          {subtitle ? <Text style={styles.subtitle}>{subtitle}</Text> : null}
          {actionLabel ? (
            <TouchableOpacity
              activeOpacity={0.86}
              style={styles.action}
              onPress={onActionPress}
              disabled={!onActionPress}
            >
              <Text style={styles.actionText}>{actionLabel}</Text>
              <Ionicons name={actionIcon} size={15} color={AppTheme.color.textInverse} />
            </TouchableOpacity>
          ) : null}
        </View>
      </LinearGradient>
    </View>
  );
}

const styles = StyleSheet.create({
  nonInteractive: {
    pointerEvents: 'none',
  },
  shell: {
    borderRadius: AppTheme.radius.xl,
    backgroundColor: AppTheme.color.surface,
    borderWidth: 1,
    borderColor: AppTheme.color.border,
    overflow: 'hidden',
    ...AppTheme.shadow.card,
  },
  canvas: {
    minHeight: 238,
    paddingHorizontal: AppTheme.space.xl,
    paddingTop: AppTheme.space.lg,
    paddingBottom: AppTheme.space.xl,
    alignItems: 'center',
    justifyContent: 'flex-start',
  },
  canvasCompact: {
    minHeight: 292,
    paddingHorizontal: AppTheme.space.lg,
    paddingTop: AppTheme.space.lg,
  },
  navPill: {
    minHeight: 38,
    borderRadius: AppTheme.radius.pill,
    paddingHorizontal: AppTheme.space.sm,
    paddingVertical: 5,
    backgroundColor: 'rgba(255, 254, 250, 0.92)',
    borderWidth: 1,
    borderColor: 'rgba(20, 20, 17, 0.08)',
    flexDirection: 'row',
    alignItems: 'center',
    gap: 9,
    ...AppTheme.shadow.card,
  },
  brandMark: {
    width: 26,
    height: 26,
    borderRadius: 13,
    backgroundColor: AppTheme.color.ink,
    alignItems: 'center',
    justifyContent: 'center',
  },
  brandText: {
    color: AppTheme.color.text,
    ...AppTheme.type.meta,
  },
  metaText: {
    color: AppTheme.color.textSubtle,
    ...AppTheme.type.label,
  },
  copy: {
    width: '100%',
    maxWidth: 640,
    alignItems: 'center',
    marginTop: AppTheme.space.xl,
    zIndex: 3,
  },
  eyebrow: {
    color: AppTheme.color.textMuted,
    ...AppTheme.type.meta,
    marginBottom: AppTheme.space.sm,
    textAlign: 'center',
  },
  title: {
    color: AppTheme.color.text,
    ...AppTheme.type.displayTitle,
    textAlign: 'center',
  },
  titleCompact: {
    fontSize: 34,
    lineHeight: 40,
  },
  subtitle: {
    marginTop: AppTheme.space.md,
    color: AppTheme.color.textMuted,
    ...AppTheme.type.body,
    textAlign: 'center',
    maxWidth: 520,
  },
  action: {
    marginTop: AppTheme.space.lg,
    minHeight: 40,
    borderRadius: AppTheme.radius.pill,
    paddingHorizontal: AppTheme.space.lg,
    backgroundColor: AppTheme.color.ink,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    ...AppTheme.shadow.card,
  },
  actionText: {
    color: AppTheme.color.textInverse,
    ...AppTheme.type.meta,
  },
  gridLineOne: {
    position: 'absolute',
    left: -80,
    right: -80,
    top: 82,
    height: 1,
    backgroundColor: 'rgba(20, 20, 17, 0.07)',
    transform: [{ rotate: '-4deg' }],
  },
  gridLineTwo: {
    position: 'absolute',
    left: -80,
    right: -80,
    bottom: 74,
    height: 1,
    backgroundColor: 'rgba(20, 20, 17, 0.06)',
    transform: [{ rotate: '5deg' }],
  },
  gridLineThree: {
    position: 'absolute',
    top: -20,
    bottom: -20,
    left: '64%',
    width: 1,
    backgroundColor: 'rgba(20, 20, 17, 0.06)',
    transform: [{ rotate: '8deg' }],
  },
  artworkHalo: {
    position: 'absolute',
    width: 260,
    height: 260,
    right: -58,
    top: -62,
    borderRadius: 130,
    backgroundColor: 'rgba(244, 177, 138, 0.22)',
  },
  artworkLayer: {
    position: 'absolute',
    left: -36,
    bottom: -58,
    width: 330,
    height: 248,
    opacity: 0.2,
  },
  artworkLayerCompact: {
    left: -18,
    bottom: -96,
    width: 278,
    height: 210,
    opacity: 0.12,
  },
  artwork: {
    width: '100%',
    height: '100%',
  },
  panelPreview: {
    position: 'absolute',
    right: AppTheme.space.xl,
    bottom: AppTheme.space.lg,
    width: 174,
    minHeight: 116,
    borderRadius: AppTheme.radius.lg,
    borderWidth: 1,
    borderColor: 'rgba(20, 20, 17, 0.08)',
    backgroundColor: 'rgba(255, 254, 250, 0.78)',
    padding: AppTheme.space.md,
    opacity: 0.82,
  },
  previewTopRow: {
    flexDirection: 'row',
    gap: 5,
    marginBottom: AppTheme.space.md,
  },
  previewDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: AppTheme.color.ink,
  },
  previewDotMuted: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: AppTheme.color.accent,
  },
  previewDotSoft: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: AppTheme.color.peach,
  },
  previewRoute: {
    width: '88%',
    height: 22,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: AppTheme.color.ink,
    borderLeftWidth: 0,
    borderBottomWidth: 0,
    marginBottom: AppTheme.space.md,
    opacity: 0.28,
  },
  previewRow: {
    height: 8,
    borderRadius: 4,
    backgroundColor: 'rgba(20, 20, 17, 0.14)',
    marginBottom: 7,
  },
  previewRowShort: {
    width: '64%',
  },
  previewMetricRow: {
    flexDirection: 'row',
    gap: AppTheme.space.sm,
    marginTop: AppTheme.space.xs,
  },
  previewMetric: {
    flex: 1,
    height: 22,
    borderRadius: AppTheme.radius.sm,
    backgroundColor: 'rgba(79, 127, 85, 0.14)',
  },
});
