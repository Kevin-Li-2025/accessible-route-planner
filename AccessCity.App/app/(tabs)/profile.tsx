import React from 'react';
import {
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons, Feather, MaterialCommunityIcons } from '@expo/vector-icons';
import { router } from 'expo-router';
import { useAuth } from '@/context/AuthContext';

type ProfileAction = {
  id: string;
  title: string;
  description: string;
  icon: React.ComponentProps<typeof Ionicons>['name']
    | React.ComponentProps<typeof Feather>['name']
    | React.ComponentProps<typeof MaterialCommunityIcons>['name'];
  iconFamily: 'ionicons' | 'feather' | 'material';
};

const PROFILE_ACTIONS: ProfileAction[] = [
  {
    id: 'edit-profile',
    title: 'Edit Profile',
    description: 'Update your personal information',
    icon: 'user',
    iconFamily: 'feather',
  },
  {
    id: 'preferences',
    title: 'Accessibility Preferences',
    description: 'Customize route preferences',
    icon: 'map-pin',
    iconFamily: 'feather',
  },
  {
    id: 'notifications',
    title: 'Notifications',
    description: 'Manage alert settings',
    icon: 'notifications-outline',
    iconFamily: 'ionicons',
  },
  {
    id: 'privacy',
    title: 'Privacy & Security',
    description: 'Control your data',
    icon: 'lock-outline',
    iconFamily: 'ionicons',
  },
  {
    id: 'support',
    title: 'Help & Support',
    description: 'Get assistance',
    icon: 'help-circle-outline',
    iconFamily: 'ionicons',
  },
];

function ActionIcon({ item }: { item: ProfileAction }) {
  if (item.iconFamily === 'feather') {
    return <Feather name={item.icon as React.ComponentProps<typeof Feather>['name']} size={22} color="#334155" />;
  }

  if (item.iconFamily === 'material') {
    return (
      <MaterialCommunityIcons
        name={item.icon as React.ComponentProps<typeof MaterialCommunityIcons>['name']}
        size={22}
        color="#334155"
      />
    );
  }

  return <Ionicons name={item.icon as React.ComponentProps<typeof Ionicons>['name']} size={22} color="#334155" />;
}

export default function Profile() {
  const { user, signOut } = useAuth();

  async function handleSignOut() {
    await signOut();
    router.replace('/login');
  }

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        style={styles.screen}
        contentContainerStyle={styles.content}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.header}>
          <Text style={styles.headerTitle}>Profile</Text>
        </View>

        <View style={styles.profileCard}>
          <View style={styles.profileTopRow}>
            <View style={styles.avatar}>
              <Ionicons name="person-outline" size={40} color="#FFFFFF" />
            </View>

            <View style={styles.profileInfo}>
              <Text style={styles.name}>{user?.fullName || 'AccessCity User'}</Text>
              <Text style={styles.email}>{user?.email || 'No email available'}</Text>
              <View style={styles.badge}>
                <Text style={styles.badgeText}>Verified User</Text>
              </View>
            </View>
          </View>

          <View style={styles.statsDivider} />

          <View style={styles.statsRow}>
            <View style={styles.statItem}>
              <Text style={styles.statValue}>24</Text>
              <Text style={styles.statLabel}>Reports</Text>
            </View>

            <View style={styles.statSeparator} />

            <View style={styles.statItem}>
              <Text style={styles.statValue}>156</Text>
              <Text style={styles.statLabel}>Safe Routes</Text>
            </View>

            <View style={styles.statSeparator} />

            <View style={styles.statItem}>
              <Text style={styles.statValue}>12</Text>
              <Text style={styles.statLabel}>Helpful</Text>
            </View>
          </View>
        </View>

        <View style={styles.actionsCard}>
          {PROFILE_ACTIONS.map((action, index) => (
            <TouchableOpacity
              key={action.id}
              activeOpacity={0.85}
              style={[
                styles.actionRow,
                index !== PROFILE_ACTIONS.length - 1 && styles.actionBorder,
              ]}
            >
              <View style={styles.actionIconWrap}>
                <ActionIcon item={action} />
              </View>

              <View style={styles.actionTextWrap}>
                <Text style={styles.actionTitle}>{action.title}</Text>
                <Text style={styles.actionDescription}>{action.description}</Text>
              </View>

              <Ionicons name="chevron-forward" size={20} color="#94A3B8" />
            </TouchableOpacity>
          ))}
        </View>

        <TouchableOpacity activeOpacity={0.85} style={styles.logoutButton} onPress={() => void handleSignOut()}>
          <Ionicons name="log-out-outline" size={20} color="#FF3B30" />
          <Text style={styles.logoutText}>Log Out</Text>
        </TouchableOpacity>
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: '#F6F8FC',
  },
  screen: {
    flex: 1,
    backgroundColor: '#F6F8FC',
  },
  content: {
    paddingBottom: 36,
  },
  header: {
    backgroundColor: '#174C8E',
    paddingHorizontal: 24,
    paddingTop: 28,
    paddingBottom: 88,
  },
  headerTitle: {
    color: '#FFFFFF',
    fontSize: 22,
    fontWeight: '700',
  },
  profileCard: {
    marginTop: -56,
    marginHorizontal: 16,
    backgroundColor: '#FFFFFF',
    borderRadius: 24,
    paddingHorizontal: 24,
    paddingVertical: 24,
    shadowColor: '#0F172A',
    shadowOffset: {
      width: 0,
      height: 10,
    },
    shadowOpacity: 0.08,
    shadowRadius: 24,
    elevation: 4,
  },
  profileTopRow: {
    flexDirection: 'row',
    alignItems: 'center',
  },
  avatar: {
    width: 80,
    height: 80,
    borderRadius: 40,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#2E86C9',
  },
  profileInfo: {
    flex: 1,
    marginLeft: 16,
  },
  name: {
    color: '#1E293B',
    fontSize: 18,
    fontWeight: '700',
  },
  email: {
    marginTop: 6,
    color: '#64748B',
    fontSize: 16,
    fontWeight: '500',
  },
  badge: {
    alignSelf: 'flex-start',
    marginTop: 12,
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 999,
    backgroundColor: '#C9F3E9',
  },
  badgeText: {
    color: '#0F8A73',
    fontSize: 13,
    fontWeight: '700',
  },
  statsDivider: {
    height: 1,
    backgroundColor: '#E8EDF4',
    marginTop: 24,
    marginBottom: 20,
  },
  statsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  statItem: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: 96,
    paddingHorizontal: 10,
  },
  statSeparator: {
    width: 1,
    alignSelf: 'stretch',
    backgroundColor: '#EEF2F7',
  },
  statValue: {
    color: '#0F172A',
    fontSize: 22,
    fontWeight: '800',
  },
  statLabel: {
    marginTop: 6,
    color: '#64748B',
    fontSize: 15,
    fontWeight: '500',
    textAlign: 'center',
    lineHeight: 20,
  },
  actionsCard: {
    marginTop: 18,
    marginHorizontal: 16,
    backgroundColor: '#FFFFFF',
    borderRadius: 24,
    overflow: 'hidden',
    shadowColor: '#0F172A',
    shadowOffset: {
      width: 0,
      height: 8,
    },
    shadowOpacity: 0.05,
    shadowRadius: 18,
    elevation: 3,
  },
  actionRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 18,
    paddingVertical: 18,
  },
  actionBorder: {
    borderBottomWidth: 1,
    borderBottomColor: '#EDF2F7',
  },
  actionIconWrap: {
    width: 50,
    height: 50,
    borderRadius: 16,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#F5F7FB',
  },
  actionTextWrap: {
    flex: 1,
    marginLeft: 14,
    marginRight: 12,
  },
  actionTitle: {
    color: '#1E293B',
    fontSize: 16,
    fontWeight: '700',
  },
  actionDescription: {
    marginTop: 4,
    color: '#64748B',
    fontSize: 15,
    fontWeight: '500',
  },
  logoutButton: {
    marginTop: 18,
    marginHorizontal: 16,
    marginBottom: 12,
    backgroundColor: '#FFFFFF',
    borderRadius: 24,
    paddingVertical: 20,
    alignItems: 'center',
    justifyContent: 'center',
    flexDirection: 'row',
    gap: 10,
    shadowColor: '#0F172A',
    shadowOffset: {
      width: 0,
      height: 8,
    },
    shadowOpacity: 0.05,
    shadowRadius: 18,
    elevation: 3,
  },
  logoutText: {
    color: '#FF3B30',
    fontSize: 18,
    fontWeight: '700',
  },
});
