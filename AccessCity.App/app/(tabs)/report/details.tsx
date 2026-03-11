import React, { useState } from 'react';
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  TextInput,
  TouchableOpacity,
  Image,
  Alert,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { router, useLocalSearchParams } from 'expo-router';
import * as ImagePicker from 'expo-image-picker';

export default function ReportDetails() {
  const { hazardType } = useLocalSearchParams<{ hazardType?: string }>();
  const [description, setDescription] = useState('');
  const [imageUri, setImageUri] = useState<string | null>(null);

  const pickImage = async () => {
    const permissionResult = await ImagePicker.requestMediaLibraryPermissionsAsync();

    if (!permissionResult.granted) {
      Alert.alert('Permission needed', 'Please allow photo library access.');
      return;
    }

    const result = await ImagePicker.launchImageLibraryAsync({
      mediaTypes: ['images'],
      allowsEditing: true,
      quality: 0.8,
    });

    if (!result.canceled && result.assets.length > 0) {
      setImageUri(result.assets[0].uri);
    }
  };

  const handleSubmit = () => {
    router.push('/(tabs)/report/success');
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
          <View style={styles.badgeGreen}>
            <Text style={styles.badgeGreenText}>Complete</Text>
          </View>
        </View>

        <View style={styles.lineActive} />

        <View style={styles.stepBlock}>
          <View style={styles.activeCircle} />
          <Text style={styles.stepSmall}>STEP 2</Text>
          <Text style={styles.stepTitle}>Add details</Text>
          <View style={styles.badgeBlue}>
            <Text style={styles.badgeBlueText}>In Progress</Text>
          </View>
        </View>

        <View style={styles.lineInactive} />

        <View style={styles.stepBlock}>
          <View style={styles.inactiveCircle} />
          <Text style={styles.stepSmall}>STEP 3</Text>
          <Text style={styles.stepTitle}>Report Done</Text>
          <Text style={styles.notCompleted}>Not Completed</Text>
        </View>
      </View>

      <View style={styles.summaryCard}>
        <View style={styles.summaryIcon}>
          <Ionicons name="warning-outline" size={24} color="#EF4444" />
        </View>
        <View style={{ flex: 1 }}>
          <Text style={styles.summaryLabel}>Reporting</Text>
          <Text style={styles.summaryTitle}>{hazardType || 'Hazard'}</Text>
        </View>
        <TouchableOpacity onPress={() => router.push('/report')}>
          <Text style={styles.changeText}>Change &gt;&gt;</Text>
        </TouchableOpacity>
      </View>

      <Text style={styles.label}>Where is this hazard?</Text>
      <View style={styles.inputBox}>
        <Ionicons name="location-outline" size={18} color="#6B7280" />
        <Text style={styles.inputPlaceholder}>Current Location</Text>
      </View>
      <Text style={styles.locationHelp}>Using your current location</Text>

      <Text style={styles.label}>Tell us more (optional)</Text>
      <TextInput
        style={styles.textArea}
        placeholder="Describe what you see to help others..."
        placeholderTextColor="#9CA3AF"
        multiline
        value={description}
        onChangeText={setDescription}
        textAlignVertical="top"
      />

      <Text style={styles.label}>Add a photo (optional)</Text>
      <TouchableOpacity style={styles.photoBox} activeOpacity={0.8} onPress={pickImage}>
        {imageUri ? (
          <Image source={{ uri: imageUri }} style={styles.previewImage} />
        ) : (
          <>
            <View style={styles.cameraCircle}>
              <Ionicons name="camera-outline" size={26} color="#9CA3AF" />
            </View>
            <Text style={styles.photoTitle}>Tap to add a photo</Text>
            <Text style={styles.photoSubtitle}>Helps others identify the hazard</Text>
          </>
        )}
      </TouchableOpacity>

      <TouchableOpacity style={styles.submitButton} activeOpacity={0.85} onPress={handleSubmit}>
        <Ionicons name="paper-plane-outline" size={18} color="#FFFFFF" />
        <Text style={styles.submitText}>Submit Report</Text>
      </TouchableOpacity>
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
    marginBottom: 24,
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
  badgeGreen: {
    alignSelf: 'flex-start',
    backgroundColor: '#DCFCE7',
    borderRadius: 999,
    paddingHorizontal: 10,
    paddingVertical: 4,
  },
  badgeGreenText: {
    fontSize: 10,
    color: '#16A34A',
    fontWeight: '600',
  },
  notCompleted: {
    fontSize: 10,
    color: '#9CA3AF',
  },
  summaryCard: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: '#FFFFFF',
    borderRadius: 18,
    borderWidth: 1,
    borderColor: '#E5E7EB',
    padding: 14,
    marginBottom: 18,
  },
  summaryIcon: {
    width: 48,
    height: 48,
    borderRadius: 14,
    backgroundColor: '#FEECEF',
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 12,
  },
  summaryLabel: {
    fontSize: 12,
    color: '#9CA3AF',
  },
  summaryTitle: {
    fontSize: 18,
    fontWeight: '700',
    color: '#111827',
  },
  changeText: {
    color: '#2563EB',
    fontWeight: '600',
    fontSize: 13,
  },
  label: {
    fontSize: 16,
    fontWeight: '700',
    color: '#111827',
    marginBottom: 10,
    marginTop: 8,
  },
  inputBox: {
    height: 52,
    borderRadius: 14,
    borderWidth: 1,
    borderColor: '#D1D5DB',
    backgroundColor: '#FFFFFF',
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 14,
    gap: 8,
  },
  inputPlaceholder: {
    fontSize: 15,
    color: '#111827',
  },
  locationHelp: {
    fontSize: 12,
    color: '#10B981',
    marginTop: 6,
    marginBottom: 8,
  },
  textArea: {
    minHeight: 110,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: '#D1D5DB',
    backgroundColor: '#FFFFFF',
    paddingHorizontal: 14,
    paddingVertical: 14,
    fontSize: 15,
    color: '#111827',
  },
  photoBox: {
    minHeight: 150,
    borderRadius: 18,
    borderWidth: 1,
    borderColor: '#D1D5DB',
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    marginTop: 4,
    overflow: 'hidden',
  },
  cameraCircle: {
    width: 54,
    height: 54,
    borderRadius: 999,
    backgroundColor: '#F3F4F6',
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 12,
  },
  photoTitle: {
    fontSize: 15,
    color: '#111827',
    fontWeight: '600',
    marginBottom: 6,
  },
  photoSubtitle: {
    fontSize: 12,
    color: '#9CA3AF',
  },
  previewImage: {
    width: '100%',
    height: 220,
  },
  submitButton: {
    height: 56,
    borderRadius: 16,
    backgroundColor: '#1D4E89',
    marginTop: 22,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
  },
  submitText: {
    color: '#FFFFFF',
    fontSize: 17,
    fontWeight: '700',
  },
});