import React from 'react';
import { StyleSheet, Text, TouchableOpacity, useWindowDimensions, View } from 'react-native';
import type { BottomTabBarProps } from '@react-navigation/bottom-tabs';
import { Ionicons } from '@expo/vector-icons';

import { AppTheme } from '@/constants/theme';

const VISIBLE_TAB_ROUTES = new Set(['map', 'report/reportpage', 'hazard', 'profile']);
type StickerIconName = React.ComponentProps<typeof Ionicons>['name'];

function getRouteLabel(routeName: string, options: BottomTabBarProps['descriptors'][string]['options']) {
  if (typeof options.tabBarLabel === 'string') return options.tabBarLabel;
  if (typeof options.title === 'string') return options.title;
  const leaf = routeName.split('/').pop() ?? routeName;
  return leaf.charAt(0).toUpperCase() + leaf.slice(1);
}

function getStickerIcon(routeName: string, focused: boolean): StickerIconName {
  if (routeName === 'map') return focused ? 'map' : 'map-outline';
  if (routeName === 'report/reportpage') return focused ? 'add-circle' : 'add-circle-outline';
  if (routeName === 'hazard') return focused ? 'warning' : 'warning-outline';
  if (routeName === 'profile') return focused ? 'person-circle' : 'person-circle-outline';
  return focused ? 'ellipse' : 'ellipse-outline';
}

export function PremiumTabBar({ state, descriptors, navigation }: BottomTabBarProps) {
  const { width } = useWindowDimensions();
  const isCompact = width < 520;
  const visibleRoutes = state.routes.filter((route) => VISIBLE_TAB_ROUTES.has(route.name));

  return (
    <View style={[styles.root, styles.rootHitTest]}>
      <View style={styles.bar}>
        {visibleRoutes.map((route) => {
          const descriptor = descriptors[route.key];
          const options = descriptor.options;
          const isFocused = state.routes[state.index]?.key === route.key;
          const label = getRouteLabel(route.name, options);
          const iconName = getStickerIcon(route.name, isFocused);

          function handlePress() {
            const event = navigation.emit({
              type: 'tabPress',
              target: route.key,
              canPreventDefault: true,
            });

            if (!isFocused && !event.defaultPrevented) {
              navigation.navigate(route.name, route.params);
            }
          }

          return (
            <TouchableOpacity
              key={route.key}
              activeOpacity={0.86}
              accessibilityRole="button"
              accessibilityState={isFocused ? { selected: true } : {}}
              accessibilityLabel={options.tabBarAccessibilityLabel}
              testID={options.tabBarButtonTestID}
              onPress={handlePress}
              style={[
                styles.item,
                isCompact && styles.itemCompact,
                isFocused && styles.itemActive,
              ]}
            >
              <View
                style={[
                  styles.iconWrap,
                  isCompact && styles.iconWrapCompact,
                  isFocused && styles.iconWrapActive,
                ]}
              >
                <Ionicons
                  name={iconName}
                  size={isFocused ? 18 : 17}
                  color={isFocused ? AppTheme.color.textInverse : AppTheme.color.textMuted}
                />
              </View>
              <Text
                numberOfLines={1}
                style={[
                  styles.label,
                  isCompact && styles.labelCompact,
                  isFocused && styles.labelActive,
                ]}
              >
                {label}
              </Text>
            </TouchableOpacity>
          );
        })}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    backgroundColor: 'transparent',
    paddingHorizontal: AppTheme.space.lg,
    paddingTop: 2,
    paddingBottom: 6,
  },
  rootHitTest: {
    pointerEvents: 'box-none',
  },
  bar: {
    width: '100%',
    maxWidth: AppTheme.layout.mobileFrameWidth,
    minHeight: 50,
    alignSelf: 'center',
    borderRadius: AppTheme.radius.pill,
    borderWidth: 1,
    borderColor: 'rgba(23, 21, 16, 0.1)',
    backgroundColor: 'rgba(255, 253, 247, 0.98)',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 5,
    paddingVertical: 4,
    shadowColor: AppTheme.color.shadow,
    shadowOffset: { width: 0, height: 10 },
    shadowOpacity: 0.1,
    shadowRadius: 20,
    elevation: 5,
  },
  item: {
    flex: 1,
    minHeight: 40,
    minWidth: 0,
    borderRadius: AppTheme.radius.pill,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 6,
    flexDirection: 'column',
    gap: 2,
  },
  itemCompact: {
    flexDirection: 'column',
    gap: 2,
    paddingHorizontal: 4,
  },
  itemActive: {
    backgroundColor: 'transparent',
  },
  iconWrap: {
    width: 30,
    height: 30,
    borderRadius: 15,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: AppTheme.color.surfaceSubtle,
  },
  iconWrapCompact: {
    width: 28,
    height: 28,
    borderRadius: 14,
  },
  iconWrapActive: {
    backgroundColor: AppTheme.color.ink,
    shadowColor: AppTheme.color.shadow,
    shadowOffset: { width: 0, height: 6 },
    shadowOpacity: 0.16,
    shadowRadius: 12,
    elevation: 4,
  },
  label: {
    color: AppTheme.color.textMuted,
    ...AppTheme.type.label,
    maxWidth: 70,
  },
  labelCompact: {
    maxWidth: 58,
    fontSize: 10,
    lineHeight: 13,
  },
  labelActive: {
    color: AppTheme.color.text,
  },
});
