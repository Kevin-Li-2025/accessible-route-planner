import React, { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';

import MapView from '@/components/MapView';
import { DEFAULT_MAP_CENTER_LNG_LAT } from '@/constants/defaultMapRegion';
import { type AppHazard, hazardsService } from '@/services/hazards.service';
import { type Hazard } from '@/models/spatial';

function toMapHazard(hazard: AppHazard): Hazard {
  return {
    id: hazard.id,
    title: hazard.title,
    type: hazard.type,
    latitude: hazard.latitude,
    longitude: hazard.longitude,
    description: hazard.description,
    status: hazard.status,
    locationText: hazard.locationText,
    reportedTime: hazard.reportedTime,
  };
}

export default function MapPageWeb() {
  const [hazards, setHazards] = useState<Hazard[]>([]);
  const [selectedHazard, setSelectedHazard] = useState<Hazard | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const centerCoordinate = useMemo<[number, number]>(() => {
    const first = hazards[0];
    return first
      ? [first.longitude, first.latitude]
      : DEFAULT_MAP_CENTER_LNG_LAT;
  }, [hazards]);

  async function loadHazards() {
    try {
      setIsLoading(true);
      setError(null);
      const data = await hazardsService.getHazards();
      setHazards(data.map(toMapHazard));
    } catch (loadError) {
      console.warn('Failed to load web map hazards:', loadError);
      setError('Could not load hazards');
      setHazards([]);
    } finally {
      setIsLoading(false);
    }
  }

  useEffect(() => {
    void loadHazards();
  }, []);

  return (
    <View style={styles.container}>
      <MapView
        centerCoordinate={centerCoordinate}
        markers={hazards}
        onMarkerPress={setSelectedHazard}
        showHazards
      />

      <View style={styles.topPanel}>
        <View>
          <Text style={styles.panelTitle}>AccessCity Map</Text>
          <Text style={styles.panelMeta}>
            {isLoading ? 'Loading hazards' : `${hazards.length} live hazards`}
          </Text>
        </View>
        <TouchableOpacity
          style={styles.iconButton}
          onPress={() => void loadHazards()}
          accessibilityRole="button"
          accessibilityLabel="Refresh hazards"
        >
          {isLoading ? (
            <ActivityIndicator size="small" color="#0F172A" />
          ) : (
            <Ionicons name="refresh" size={18} color="#0F172A" />
          )}
        </TouchableOpacity>
      </View>

      {error ? (
        <View style={styles.errorPanel}>
          <Text style={styles.errorText}>{error}</Text>
        </View>
      ) : null}

      {selectedHazard ? (
        <View style={styles.detailPanel}>
          <View style={styles.detailHeader}>
            <View style={styles.detailTitleBlock}>
              <Text style={styles.detailTitle} numberOfLines={1}>
                {selectedHazard.title}
              </Text>
              <Text style={styles.detailMeta} numberOfLines={1}>
                {selectedHazard.status} · {selectedHazard.reportedTime}
              </Text>
            </View>
            <TouchableOpacity
              style={styles.iconButton}
              onPress={() => setSelectedHazard(null)}
              accessibilityRole="button"
              accessibilityLabel="Close hazard details"
            >
              <Ionicons name="close" size={18} color="#0F172A" />
            </TouchableOpacity>
          </View>
          <Text style={styles.detailDescription} numberOfLines={3}>
            {selectedHazard.description}
          </Text>
          <Text style={styles.detailLocation} numberOfLines={1}>
            {selectedHazard.locationText}
          </Text>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#F8FAFC',
  },
  topPanel: {
    position: 'absolute',
    top: 18,
    left: 18,
    right: 18,
    minHeight: 58,
    borderRadius: 8,
    backgroundColor: 'rgba(255,255,255,0.96)',
    borderWidth: 1,
    borderColor: '#E5E7EB',
    paddingHorizontal: 14,
    paddingVertical: 10,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    boxShadow: '0 8px 14px rgba(15, 23, 42, 0.08)',
  },
  panelTitle: {
    fontSize: 16,
    fontWeight: '700',
    color: '#0F172A',
  },
  panelMeta: {
    marginTop: 2,
    fontSize: 12,
    color: '#64748B',
  },
  iconButton: {
    width: 34,
    height: 34,
    borderRadius: 8,
    backgroundColor: '#F8FAFC',
    borderWidth: 1,
    borderColor: '#E2E8F0',
    justifyContent: 'center',
    alignItems: 'center',
  },
  errorPanel: {
    position: 'absolute',
    top: 88,
    left: 18,
    right: 18,
    borderRadius: 8,
    backgroundColor: '#FEF2F2',
    borderWidth: 1,
    borderColor: '#FECACA',
    padding: 12,
  },
  errorText: {
    color: '#991B1B',
    fontSize: 13,
    fontWeight: '600',
  },
  detailPanel: {
    position: 'absolute',
    left: 18,
    right: 18,
    bottom: 18,
    borderRadius: 8,
    backgroundColor: 'rgba(255,255,255,0.98)',
    borderWidth: 1,
    borderColor: '#E5E7EB',
    padding: 14,
    boxShadow: '0 10px 18px rgba(15, 23, 42, 0.1)',
  },
  detailHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
  },
  detailTitleBlock: {
    flex: 1,
  },
  detailTitle: {
    fontSize: 16,
    fontWeight: '700',
    color: '#0F172A',
  },
  detailMeta: {
    marginTop: 3,
    fontSize: 12,
    color: '#64748B',
  },
  detailDescription: {
    marginTop: 12,
    color: '#334155',
    fontSize: 14,
    lineHeight: 20,
  },
  detailLocation: {
    marginTop: 10,
    color: '#64748B',
    fontSize: 12,
  },
});
