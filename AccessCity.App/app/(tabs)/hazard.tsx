import React, { useMemo, useState } from 'react';
import {
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';

type HazardStatus = 'Reported' | 'Acknowledged' | 'Resolved';

type HazardItem = {
  id: number;
  title: string;
  status: HazardStatus;
  description: string;
  location: string;
  reportedAt: string;
  icon: React.ComponentProps<typeof Ionicons>['name'] | React.ComponentProps<typeof MaterialCommunityIcons>['name'];
  iconFamily: 'ionicons' | 'material';
};

const FILTERS: HazardStatus[] = ['Reported', 'Acknowledged', 'Resolved'];

const HAZARDS: HazardItem[] = [
  {
    id: 1,
    title: 'Gas Leak Detection!',
    status: 'Reported',
    description:
      'A hazardous gas leak has been detected in this area. Workers should evacuate immediately.',
    location: 'Hazard located in Zone 3',
    reportedAt: '2 minutes ago',
    icon: 'alert-outline',
    iconFamily: 'ionicons',
  },
  {
    id: 2,
    title: 'Blocked pavement',
    status: 'Reported',
    description:
      'Construction equipment is blocking the entire walkway. Pedestrians unable to pass.',
    location: 'Near the east footpath',
    reportedAt: '2 minutes ago',
    icon: 'boom-gate-outline',
    iconFamily: 'material',
  },
  {
    id: 3,
    title: 'Broken Street Light',
    status: 'Acknowledged',
    description:
      'There is a broken street light. The street is dimly-lit and visibility is reduced for pedestrians.',
    location: 'Hazard located in Park Avenue',
    reportedAt: '2 hours ago',
    icon: 'bulb-outline',
    iconFamily: 'ionicons',
  },
  {
    id: 4,
    title: 'Damaged Footpath',
    status: 'Acknowledged',
    description:
      'Large pothole on walking path. May cause tripping hazard and difficulty for wheelchair users.',
    location: 'Hazard located in River Walk',
    reportedAt: '45 minutes ago',
    icon: 'wrench-outline',
    iconFamily: 'ionicons',
  },
  {
    id: 5,
    title: 'Road Obstruction Cleared',
    status: 'Resolved',
    description:
      'Debris has been removed from the sidewalk. Area is now safe for pedestrian traffic.',
    location: 'Hazard located in Oak Street',
    reportedAt: 'Yesterday',
    icon: 'warning-outline',
    iconFamily: 'ionicons',
  },
  {
    id: 6,
    title: 'Pothole Repaired',
    status: 'Resolved',
    description:
      'Large pothole on walking path has been filled and is now safe for use.',
    location: 'Hazard located in Town Square',
    reportedAt: '2 days ago',
    icon: 'wrench-outline',
    iconFamily: 'ionicons',
  },
];

const STATUS_STYLES: Record<
  HazardStatus,
  { badgeBackground: string; badgeBorder: string; badgeText: string }
> = {
  Reported: {
    badgeBackground: '#FCE8D3',
    badgeBorder: '#F2BE83',
    badgeText: '#E15A1D',
  },
  Acknowledged: {
    badgeBackground: '#FFF5CC',
    badgeBorder: '#E7CF68',
    badgeText: '#A67C00',
  },
  Resolved: {
    badgeBackground: '#DDF6E8',
    badgeBorder: '#93D5AF',
    badgeText: '#1F8A4D',
  },
};

function HazardIcon({ item }: { item: HazardItem }) {
  if (item.iconFamily === 'material') {
    return <MaterialCommunityIcons name={item.icon as any} size={38} color="#1F2937" />;
  }

  return <Ionicons name={item.icon as any} size={38} color="#1F2937" />;
}

export default function Hazard() {
  const [selectedFilter, setSelectedFilter] = useState<HazardStatus>('Reported');

  const filteredHazards = useMemo(
    () => HAZARDS.filter((hazard) => hazard.status === selectedFilter),
    [selectedFilter]
  );

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        style={styles.screen}
        contentContainerStyle={styles.content}
        showsVerticalScrollIndicator={false}
      >
        <Text style={styles.title}>Hazards</Text>

        <View style={styles.filterPill}>
          {FILTERS.map((filter) => {
            const isActive = filter === selectedFilter;

            return (
              <TouchableOpacity
                key={filter}
                activeOpacity={0.9}
                style={[
                  styles.filterButton,
                  isActive && styles.filterButtonActive,
                ]}
                onPress={() => setSelectedFilter(filter)}
              >
                <Text
                  numberOfLines={1}
                  style={[styles.filterText, isActive && styles.filterTextActive]}
                >
                  {filter}
                </Text>
              </TouchableOpacity>
            );
          })}
        </View>

        {filteredHazards.map((hazard) => (
          <View key={hazard.id} style={styles.card}>
            <View style={styles.cardHeader}>
              <View style={styles.iconWrap}>
                <HazardIcon item={hazard} />
              </View>

              <View style={styles.headerTextWrap}>
                <Text style={styles.cardTitle}>{hazard.title}</Text>
                <View
                  style={[
                    styles.statusBadge,
                    {
                      backgroundColor: STATUS_STYLES[hazard.status].badgeBackground,
                      borderColor: STATUS_STYLES[hazard.status].badgeBorder,
                    },
                  ]}
                >
                  <Text
                    style={[
                      styles.statusBadgeText,
                      { color: STATUS_STYLES[hazard.status].badgeText },
                    ]}
                  >
                    Status: {hazard.status}
                  </Text>
                </View>
              </View>
            </View>

            <Text style={styles.sectionLabel}>Description</Text>
            <Text style={styles.description}>{hazard.description}</Text>

            <View style={styles.metaRow}>
              <View style={styles.metaBlock}>
                <View style={styles.metaLabelRow}>
                  <Ionicons name="location-outline" size={16} color="#F04D4D" />
                  <Text style={styles.metaLabel}>Location</Text>
                </View>
                <Text style={styles.metaValue}>{hazard.location}</Text>
              </View>

              <View style={styles.metaBlock}>
                <View style={styles.metaLabelRow}>
                  <Ionicons name="time-outline" size={16} color="#A3A8B8" />
                  <Text style={styles.metaLabel}>Reported</Text>
                </View>
                <Text style={styles.metaValue}>{hazard.reportedAt}</Text>
              </View>
            </View>

            <View style={styles.actionRow}>
              <TouchableOpacity activeOpacity={0.9} style={styles.primaryButton}>
                <Ionicons name="paper-plane-outline" size={16} color="#FFFFFF" />
                <Text style={styles.primaryButtonText}>Avoid in Route</Text>
              </TouchableOpacity>

              <TouchableOpacity activeOpacity={0.9} style={styles.secondaryButton}>
                <Ionicons name="chevron-forward" size={18} color="#1F2937" />
                <Text style={styles.secondaryButtonText}>Details</Text>
              </TouchableOpacity>
            </View>
          </View>
        ))}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: '#F4F4F4',
  },
  screen: {
    flex: 1,
    backgroundColor: '#F4F4F4',
  },
  content: {
    paddingHorizontal: 18,
    paddingTop: 18,
    paddingBottom: 34,
    gap: 18,
  },
  title: {
    fontSize: 28,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 2,
  },
  filterPill: {
    flexDirection: 'row',
    backgroundColor: '#E6E6E6',
    borderRadius: 999,
    padding: 6,
    gap: 4,
  },
  filterButton: {
    flex: 1,
    minWidth: 0,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 11,
    paddingHorizontal: 8,
    borderRadius: 999,
  },
  filterButtonActive: {
    backgroundColor: '#2F2F2F',
    shadowColor: '#000000',
    shadowOpacity: 0.12,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 4 },
    elevation: 3,
  },
  filterText: {
    fontSize: 12,
    fontWeight: '500',
    color: '#667085',
    textAlign: 'center',
  },
  filterTextActive: {
    color: '#FFFFFF',
  },
  card: {
    backgroundColor: '#FAFAFA',
    borderRadius: 20,
    borderWidth: 1,
    borderColor: '#D7D7D7',
    padding: 20,
    shadowColor: '#000000',
    shadowOpacity: 0.05,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 3 },
    elevation: 2,
  },
  cardHeader: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    marginBottom: 16,
  },
  iconWrap: {
    width: 52,
    alignItems: 'center',
    paddingTop: 4,
  },
  headerTextWrap: {
    flex: 1,
    gap: 10,
  },
  cardTitle: {
    fontSize: 18,
    fontWeight: '800',
    color: '#1F2937',
  },
  statusBadge: {
    alignSelf: 'flex-start',
    borderRadius: 999,
    borderWidth: 1,
    paddingHorizontal: 14,
    paddingVertical: 7,
  },
  statusBadgeText: {
    fontSize: 14,
    fontWeight: '500',
  },
  sectionLabel: {
    fontSize: 16,
    fontWeight: '700',
    color: '#3D475A',
    marginBottom: 8,
  },
  description: {
    fontSize: 15,
    lineHeight: 31,
    color: '#1F2937',
    marginBottom: 18,
  },
  metaRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    gap: 18,
    marginBottom: 18,
  },
  metaBlock: {
    flex: 1,
    gap: 6,
  },
  metaLabelRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
  },
  metaLabel: {
    fontSize: 14,
    color: '#8B92A3',
  },
  metaValue: {
    fontSize: 15,
    lineHeight: 24,
    fontWeight: '500',
    color: '#1F2937',
  },
  actionRow: {
    flexDirection: 'row',
    gap: 12,
  },
  primaryButton: {
    flex: 1,
    minHeight: 62,
    borderRadius: 16,
    backgroundColor: '#1F5396',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 10,
    paddingHorizontal: 16,
  },
  primaryButtonText: {
    width: 86,
    fontSize: 15,
    lineHeight: 22,
    fontWeight: '600',
    color: '#FFFFFF',
    textAlign: 'center',
  },
  secondaryButton: {
    flex: 1,
    minHeight: 62,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: '#DADADA',
    backgroundColor: '#F9F9F9',
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 10,
    paddingHorizontal: 16,
  },
  secondaryButtonText: {
    fontSize: 15,
    fontWeight: '500',
    color: '#1F2937',
  },
});
