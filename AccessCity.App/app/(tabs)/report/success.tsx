import React from 'react';
import { View, Text, StyleSheet, TouchableOpacity } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { router } from 'expo-router';

export default function ReportSuccess() {
  return (
    <View style={styles.screen}>
      <View style={styles.iconCircle}>
        <Ionicons name="checkmark" size={36} color="#16A34A" />
      </View>

      <Text style={styles.title}>Report Submitted!</Text>
      <Text style={styles.subtitle}>Thank you for making our community safer</Text>

      <View style={styles.messageBox}>
        <Text style={styles.messageText}>
          Your report has been acknowledged
        </Text>
      </View>

      <TouchableOpacity style={styles.button} onPress={() => router.push('/report')}>
        <Text style={styles.buttonText}>Back to Report</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    backgroundColor: '#F7F7F7',
    justifyContent: 'center',
    alignItems: 'center',
    padding: 24,
  },
  iconCircle: {
    width: 92,
    height: 92,
    borderRadius: 999,
    backgroundColor: '#DCFCE7',
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 28,
  },
  title: {
    fontSize: 30,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 10,
  },
  subtitle: {
    fontSize: 16,
    color: '#6B7280',
    textAlign: 'center',
    marginBottom: 20,
  },
  messageBox: {
    backgroundColor: '#E8F0FF',
    paddingHorizontal: 18,
    paddingVertical: 16,
    borderRadius: 16,
    marginBottom: 24,
  },
  messageText: {
    color: '#1D4E89',
    textAlign: 'center',
    fontSize: 14,
    fontWeight: '600',
  },
  button: {
    backgroundColor: '#1D4E89',
    paddingHorizontal: 22,
    paddingVertical: 14,
    borderRadius: 14,
  },
  buttonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },
});