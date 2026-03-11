import React from 'react';
import { View, Text, StyleSheet, TouchableOpacity, ScrollView } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';

const hazardTypes = [
  { id: 1, title: 'Broken street light', icon: 'bulb-outline', bg: '#FFF5D9', iconColor: '#EAB308' },
  { id: 2, title: 'Blocked pavement', icon: 'remove-circle-outline', bg: '#FDEBDD', iconColor: '#F59E0B' },
  { id: 3, title: 'Parked car blocking dropped kerb', icon: 'car-outline', bg: '#E8F0FF', iconColor: '#2563EB' },
  { id: 4, title: 'Road obstruction', icon: 'warning-outline', bg: '#FEECEF', iconColor: '#EF4444' },
  { id: 5, title: 'Unsafe crossing', icon: 'alert-circle-outline', bg: '#E6FBF6', iconColor: '#14B8A6' },
  { id: 6, title: 'Other', icon: 'create-outline', bg: '#F3F4F6', iconColor: '#4B5563' },
];

export default function Report() {
  const goToDetails = (hazardType: string) => {
    router.push({
      pathname: '/(tabs)/report/details',
      params: { hazardType },
    });
  };

  return (
    <ScrollView style={styles.screen} contentContainerStyle={styles.content}>
      <Text style={styles.header}>Report hazard</Text>
      <View style={styles.divider} />

      <View style={styles.stepRow}>
        <View style={styles.stepBlock}>
          <View style={styles.activeCircle} />
          <Text style={styles.stepSmall}>STEP 1</Text>
          <Text style={styles.stepTitle}>Select type</Text>
          <View style={styles.badgeBlue}>
            <Text style={styles.badgeBlueText}>In Progress</Text>
          </View>
        </View>

        <View style={styles.lineActive} />

        <View style={styles.stepBlock}>
          <View style={styles.inactiveCircle} />
          <Text style={styles.stepSmall}>STEP 2</Text>
          <Text style={styles.stepTitle}>Add details</Text>
          <Text style={styles.notCompleted}>Not Completed</Text>
        </View>

        <View style={styles.lineInactive} />

        <View style={styles.stepBlock}>
          <View style={styles.inactiveCircle} />
          <Text style={styles.stepSmall}>STEP 3</Text>
          <Text style={styles.stepTitle}>Report Done</Text>
          <Text style={styles.notCompleted}>Not Completed</Text>
        </View>
      </View>

      <Text style={styles.question}>
        What is the type of hazard?<Text style={styles.required}>*</Text>
      </Text>
      <Text style={styles.subtext}>Select the type of hazard you want to report</Text>

      <View style={styles.grid}>
        {hazardTypes.map((item) => (
          <TouchableOpacity
            key={item.id}
            style={styles.card}
            activeOpacity={0.8}
            onPress={() => goToDetails(item.title)}
          >
            <View style={[styles.iconBox, { backgroundColor: item.bg }]}>
              <Ionicons name={item.icon as any} size={28} color={item.iconColor} />
            </View>
            <Text style={styles.cardText}>{item.title}</Text>
          </TouchableOpacity>
        ))}
      </View>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    backgroundColor: '#F7F7F7',
  },
  content: {
    padding: 16,
    paddingBottom: 40,
  },
  header: {
    fontSize: 34,
    fontWeight: '800',
    textAlign: 'center',
    color: '#111827',
    marginTop: 8,
    marginBottom: 12,
  },
  divider: {
    height: 1,
    backgroundColor: '#E5E7EB',
    marginBottom: 18,
  },
  stepRow: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    marginBottom: 28,
  },
  stepBlock: {
    width: 92,
  },
  activeCircle: {
    width: 20,
    height: 20,
    borderRadius: 999,
    backgroundColor: '#1D5AA6',
    marginBottom: 10,
  },
  inactiveCircle: {
    width: 20,
    height: 20,
    borderRadius: 999,
    backgroundColor: '#D1D5DB',
    marginBottom: 10,
  },
  lineActive: {
    flex: 1,
    height: 2,
    backgroundColor: '#1D5AA6',
    marginTop: 9,
    marginHorizontal: 8,
  },
  lineInactive: {
    flex: 1,
    height: 2,
    backgroundColor: '#D1D5DB',
    marginTop: 9,
    marginHorizontal: 8,
  },
  stepSmall: {
    fontSize: 10,
    color: '#9CA3AF',
    marginBottom: 2,
  },
  stepTitle: {
    fontSize: 15,
    fontWeight: '700',
    color: '#111827',
    marginBottom: 4,
  },
  badgeBlue: {
    alignSelf: 'flex-start',
    backgroundColor: '#E8F0FF',
    borderRadius: 999,
    paddingHorizontal: 10,
    paddingVertical: 4,
  },
  badgeBlueText: {
    fontSize: 10,
    color: '#2563EB',
    fontWeight: '600',
  },
  notCompleted: {
    fontSize: 10,
    color: '#9CA3AF',
  },
  question: {
    fontSize: 22,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 8,
  },
  required: {
    color: '#EF4444',
  },
  subtext: {
    fontSize: 14,
    color: '#6B7280',
    marginBottom: 20,
  },
  grid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    justifyContent: 'space-between',
    rowGap: 16,
  },
  card: {
    width: '48%',
    minHeight: 170,
    backgroundColor: '#FFFFFF',
    borderRadius: 22,
    borderWidth: 1,
    borderColor: '#E5E7EB',
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 16,
    paddingVertical: 18,
  },
  iconBox: {
    width: 72,
    height: 72,
    borderRadius: 18,
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 18,
  },
  cardText: {
    textAlign: 'center',
    fontSize: 15,
    lineHeight: 22,
    fontWeight: '600',
    color: '#111827',
  },
});