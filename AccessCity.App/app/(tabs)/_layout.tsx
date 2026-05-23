import { router, Tabs } from 'expo-router';
import React from 'react';

import { HapticTab } from '@/components/haptic-tab';
import { IconSymbol } from '@/components/ui/icon-symbol';
import { Colors } from '@/constants/theme';
import { useColorScheme } from '@/hooks/use-color-scheme';

export default function TabLayout() {
  const colorScheme = useColorScheme();

  return (
    <Tabs
      initialRouteName="map"
      screenOptions={{
        tabBarActiveTintColor: Colors[colorScheme ?? 'light'].tint,
        headerShown: false,
        tabBarButton: HapticTab,
      }}
    >
      <Tabs.Screen
        name="index"
        options={{
          href: null,
        }}
      />

      <Tabs.Screen
        name="map"
        options={{
          title: 'Map',
          tabBarIcon: ({ color }) => (
            <IconSymbol size={28} name="paperplane.fill" color={color} />
          ),
        }}
      />

      <Tabs.Screen
        name="report/reportpage"
        listeners={{
          tabPress: (e) => {
            e.preventDefault();
            router.push('/report/reportpage');
          },
        }}
        options={{
          title: 'Report',
          tabBarIcon: ({ color }) => (
            <IconSymbol
              size={28}
              name="exclamationmark.triangle.fill"
              color={color}
            />
          ),
        }}
      />

      <Tabs.Screen
        name="report/adminHazard-report"
        options={{
          href: null,
        }}
      />

      <Tabs.Screen
        name="hazard"
        options={{
          title: 'Hazard',
          tabBarIcon: ({ color }) => (
            <IconSymbol
              size={28}
              name="exclamationmark.triangle.fill"
              color={color}
            />
          ),
        }}
      />

      <Tabs.Screen
        name="profile"
        options={{
          title: 'Profile',
          tabBarIcon: ({ color }) => (
            <IconSymbol size={28} name="person.fill" color={color} />
          ),
        }}
      />
    </Tabs>
  );
}
