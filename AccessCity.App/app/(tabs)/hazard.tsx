import React, { useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  SafeAreaView,
  FlatList,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { hazardsService } from '@/services/hazards.service';
import HazardDetailsModal from '@/components/MapView/HazardDetailsModal';
import { Hazard as HazardType } from '@/components/MapView/MapTypes';

type HazardStatus = 'Reported' | 'Acknowledged' | 'Resolved';

type HazardItem = {
  id: string | number;
  title: string;
  status: HazardStatus;
  description: string;
  location: string;
  reportedAt: string;
  icon: React.ComponentProps<typeof Ionicons>['name'] | React.ComponentProps<typeof MaterialCommunityIcons>['name'];
  iconFamily: 'ionicons' | 'material';
};

const FILTERS: HazardStatus[] = ['Reported', 'Acknowledged', 'Resolved'];

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
  const [hazards, setHazards] = useState<HazardItem[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [selectedFilter, setSelectedFilter] = useState<HazardStatus>('Reported');

  const [selectedHazardDetail, setSelectedHazardDetail] = useState<HazardType | null>(null);
  const [hazardDetailsVisible, setHazardDetailsVisible] = useState(false);

  async function handleOpenDetails(hazardId: string | number) {
    try {
      const fullDetails = await hazardsService.getHazardById(hazardId);
      if (fullDetails) {
        setSelectedHazardDetail({
          ...fullDetails,
          id: String(fullDetails.id)
        } as HazardType);
        setHazardDetailsVisible(true);
      } else {
        Alert.alert("Error", "Could not load hazard details.");
      }
    } catch (error) {
      console.error('Failed to load full details:', error);
      Alert.alert("Error", "Failed to load hazard details.");
    }
  }

  async function loadHazards() {
    try {
      setIsLoading(true);
      const data = await hazardsService.getHazards(selectedFilter);
      const mapped = data
        .slice(0, 100) // limit to 100 items to prevent out-of-memory on massive OSM datasets
        .map<HazardItem | null>((hazard) => {
          const status = hazard.status === 'UnderReview'
            ? 'Acknowledged'
            : hazard.status;

          if (status !== 'Reported' && status !== 'Acknowledged' && status !== 'Resolved') {
            return null;
          }

          return {
            id: hazard.id,
            title: hazard.title,
            status,
            description: hazard.description,
            location: hazard.locationText,
            reportedAt: hazard.reportedTime,
            icon: hazard.type.includes('pavement') || hazard.type.includes('obstruction')
              ? 'boom-gate-outline'
              : hazard.type.includes('light')
                ? 'bulb-outline'
                : 'warning-outline',
            iconFamily: hazard.type.includes('pavement') || hazard.type.includes('obstruction')
              ? 'material'
              : 'ionicons',
          };
        })
        .filter((hazard): hazard is HazardItem => hazard !== null);
      setHazards(mapped);
    } catch (error) {
      console.error('Failed to load hazards:', error);
      setHazards([]);
    } finally {
      setIsLoading(false);
    }
  }

  useFocusEffect(
    React.useCallback(() => {
      void loadHazards();
    }, [selectedFilter])
  );

  return (
    <SafeAreaView style={styles.safeArea}>
      <FlatList
        style={styles.screen}
        contentContainerStyle={styles.content}
        showsVerticalScrollIndicator={false}
        data={hazards}
        keyExtractor={(item) => String(item.id)}
        initialNumToRender={10}
        windowSize={5}
        ListHeaderComponent={
          <>
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

            {isLoading ? (
              <View style={styles.stateCard}>
                <ActivityIndicator color="#174C8E" />
                <Text style={styles.stateText}>Loading hazards...</Text>
              </View>
            ) : null}

            {!isLoading && !hazards.length ? (
              <View style={styles.stateCard}>
                <Text style={styles.stateText}>No hazards found for this status.</Text>
              </View>
            ) : null}
          </>
        }
        renderItem={({ item: hazard }) => (
          <View style={styles.card}>
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

              <TouchableOpacity 
                activeOpacity={0.9} 
                style={styles.secondaryButton}
                onPress={() => handleOpenDetails(hazard.id)}
              >
                <Ionicons name="chevron-forward" size={18} color="#1F2937" />
                <Text style={styles.secondaryButtonText}>Details</Text>
              </TouchableOpacity>
            </View>
          </View>
        )}
      />

      <HazardDetailsModal
        visible={hazardDetailsVisible}
        hazard={selectedHazardDetail}
        onClose={() => setHazardDetailsVisible(false)}
      />
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
  stateCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: 18,
    paddingHorizontal: 18,
    paddingVertical: 20,
    alignItems: 'center',
    gap: 10,
  },
  stateText: {
    color: '#475569',
    fontSize: 15,
    fontWeight: '600',
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
