import React from 'react';
import {
  Modal,
  StyleSheet,
  View,
  Text,
  TouchableOpacity,
  Pressable,
  ScrollView,
  TextInput,
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import { reportHazardLabelMap, reportHazardOptions } from './mapData';
import { ReportHazardType } from './MapTypes';

type ReportHazardModalProps = {
  visible: boolean;
  reportStep: 1 | 2 | 3;
  selectedReportType: ReportHazardType | null;
  reportDescription: string;
  onClose: () => void;
  onSelectType: (type: ReportHazardType) => void;
  onNext: () => void;
  onBack: () => void;
  onSubmit: () => void;
  onDone: () => void;
  onChangeDescription: (text: string) => void;
};

function renderOptionIcon(
  iconType: 'ionicons' | 'material',
  iconName: string,
  iconColor: string
) {
  if (iconType === 'material') {
    return (
      <MaterialCommunityIcons
        name={iconName as any}
        size={28}
        color={iconColor}
      />
    );
  }

  return <Ionicons name={iconName as any} size={28} color={iconColor} />;
}

export default function ReportHazardModal({
  visible,
  reportStep,
  selectedReportType,
  reportDescription,
  onClose,
  onSelectType,
  onNext,
  onBack,
  onSubmit,
  onDone,
  onChangeDescription,
}: ReportHazardModalProps) {
  const selectedTypeOption = reportHazardOptions.find(
    (item) => item.key === selectedReportType
  );

  return (
    <Modal visible={visible} animationType="slide" transparent onRequestClose={onClose}>
      <View style={styles.modalRoot}>
        <Pressable style={styles.overlay} onPress={onClose} />

        <View style={styles.sheetWrapper}>
          <View style={styles.sheet}>
            <View style={styles.dragHandle} />
            <Text style={styles.sheetTitle}>Report hazard</Text>
            <View style={styles.sheetDivider} />

            <View style={styles.stepRow}>
              <View style={styles.stepItem}>
                <View style={[styles.stepCircle, reportStep >= 1 && styles.stepCircleActive]} />
                <Text style={styles.stepLabel}>STEP 1</Text>
                <Text style={styles.stepTitle}>Select type</Text>
              </View>

              <View style={[styles.stepLine, reportStep >= 2 && styles.stepLineActive]} />

              <View style={styles.stepItem}>
                <View style={[styles.stepCircle, reportStep >= 2 && styles.stepCircleActive]} />
                <Text style={styles.stepLabel}>STEP 2</Text>
                <Text style={styles.stepTitle}>Add details</Text>
              </View>

              <View style={[styles.stepLine, reportStep >= 3 && styles.stepLineActive]} />

              <View style={styles.stepItem}>
                <View style={[styles.stepCircle, reportStep >= 3 && styles.stepCircleActive]} />
                <Text style={styles.stepLabel}>STEP 3</Text>
                <Text style={styles.stepTitle}>Report Done</Text>
              </View>
            </View>

            {reportStep === 1 && (
              <>
                <ScrollView style={styles.sheetScroll} contentContainerStyle={styles.sheetContent}>
                  <Text style={styles.questionTitle}>
                    What is the type of hazard?
                    <Text style={styles.required}>*</Text>
                  </Text>

                  <Text style={styles.questionSubtitle}>
                    Select the type of hazard you want to report
                  </Text>

                  <View style={styles.grid}>
                    {reportHazardOptions.map((item) => {
                      const isSelected = selectedReportType === item.key;

                      return (
                        <TouchableOpacity
                          key={item.key}
                          style={[styles.card, isSelected && styles.cardSelected]}
                          onPress={() => onSelectType(item.key)}
                          activeOpacity={0.85}
                        >
                          <View style={[styles.cardIconBox, { backgroundColor: item.iconBg }]}>
                            {renderOptionIcon(item.iconType, item.iconName, item.iconColor)}
                          </View>
                          <Text style={styles.cardText}>{item.label}</Text>
                        </TouchableOpacity>
                      );
                    })}
                  </View>
                </ScrollView>

                <View style={styles.sheetBottomButtons}>
                  <TouchableOpacity style={styles.cancelButton} onPress={onClose}>
                    <Text style={styles.cancelButtonText}>Cancel</Text>
                  </TouchableOpacity>

                  <TouchableOpacity
                    style={[styles.nextButton, !selectedReportType && styles.nextButtonDisabled]}
                    onPress={onNext}
                    disabled={!selectedReportType}
                  >
                    <Text style={styles.nextButtonText}>Next</Text>
                  </TouchableOpacity>
                </View>
              </>
            )}

            {reportStep === 2 && (
              <>
                <ScrollView style={styles.sheetScroll} contentContainerStyle={styles.sheetContent}>
                  <View style={styles.selectedTypeBox}>
                    <View style={styles.selectedTypeLeft}>
                      <View
                        style={[
                          styles.selectedTypeIcon,
                          { backgroundColor: selectedTypeOption?.iconBg ?? '#F3F4F6' },
                        ]}
                      >
                        {selectedTypeOption
                          ? renderOptionIcon(
                              selectedTypeOption.iconType,
                              selectedTypeOption.iconName,
                              selectedTypeOption.iconColor
                            )
                          : <Ionicons name="warning-outline" size={22} color="#EF4444" />}
                      </View>

                      <View style={styles.selectedTypeTextWrap}>
                        <Text style={styles.selectedTypeMiniLabel}>Reporting</Text>
                        <Text style={styles.selectedTypeText}>
                          {selectedReportType
                            ? reportHazardLabelMap[selectedReportType]
                            : ''}
                        </Text>
                      </View>
                    </View>

                    <TouchableOpacity onPress={onBack}>
                      <Text style={styles.changeText}>Change &gt;&gt;</Text>
                    </TouchableOpacity>
                  </View>

                  <Text style={styles.sectionTitle}>Where is this hazard?</Text>

                  <View style={styles.locationBox}>
                    <Ionicons name="location-outline" size={22} color="#6B7280" />
                    <Text style={styles.locationText}>Current Location</Text>
                  </View>

                  <Text style={styles.locationHint}>Using your current location</Text>

                  <Text style={styles.sectionTitle}>Tell us more (optional)</Text>

                  <TextInput
                    style={styles.descriptionInput}
                    placeholder="Describe what you see to help others..."
                    placeholderTextColor="#9CA3AF"
                    multiline
                    value={reportDescription}
                    onChangeText={onChangeDescription}
                    textAlignVertical="top"
                  />

                  <Text style={styles.sectionTitle}>Add a photo (optional)</Text>

                  <TouchableOpacity style={styles.photoBox} activeOpacity={0.85}>
                    <View style={styles.photoIconCircle}>
                      <Ionicons name="camera-outline" size={28} color="#9CA3AF" />
                    </View>
                    <Text style={styles.photoTitle}>Tap to add a photo</Text>
                    <Text style={styles.photoSubtitle}>
                      Helps others identify the hazard
                    </Text>
                  </TouchableOpacity>
                </ScrollView>

                <View style={styles.sheetBottomButtons}>
                  <TouchableOpacity style={styles.cancelButton} onPress={onBack}>
                    <Text style={styles.cancelButtonText}>Back</Text>
                  </TouchableOpacity>

                  <TouchableOpacity style={styles.nextButton} onPress={onSubmit}>
                    <Text style={styles.nextButtonText}>Submit Report</Text>
                  </TouchableOpacity>
                </View>
              </>
            )}

            {reportStep === 3 && (
              <>
                <View style={styles.successContainer}>
                  <View style={styles.successIconCircle}>
                    <Ionicons name="checkmark" size={40} color="#16A34A" />
                  </View>

                  <Text style={styles.successTitle}>Report Submitted!</Text>

                  <Text style={styles.successSubtitle}>
                    Thank you for making our community safer
                  </Text>

                  <View style={styles.successMessageBox}>
                    <Text style={styles.successMessageText}>
                      Your report has been acknowledged
                    </Text>
                  </View>
                </View>

                <View style={styles.sheetBottomButtons}>
                  <TouchableOpacity style={styles.fullWidthButton} onPress={onDone}>
                    <Text style={styles.nextButtonText}>Done</Text>
                  </TouchableOpacity>
                </View>
              </>
            )}
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  modalRoot: { flex: 1, justifyContent: 'flex-end' },
  overlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.18)',
  },
  sheetWrapper: { width: '100%', justifyContent: 'flex-end' },
  sheet: {
    backgroundColor: '#FFFFFF',
    borderTopLeftRadius: 28,
    borderTopRightRadius: 28,
    paddingTop: 10,
    paddingHorizontal: 20,
    paddingBottom: 22,
    shadowColor: '#000',
    shadowOpacity: 0.08,
    shadowRadius: 12,
    shadowOffset: { width: 0, height: -4 },
    elevation: 12,
    height: '95%',
  },
  dragHandle: {
    width: 48,
    height: 5,
    borderRadius: 999,
    backgroundColor: '#D1D5DB',
    alignSelf: 'center',
    marginBottom: 14,
  },
  sheetTitle: {
    fontSize: 18,
    fontWeight: '700',
    textAlign: 'center',
    color: '#111827',
  },
  sheetDivider: {
    height: 1,
    backgroundColor: '#E5E7EB',
    marginTop: 14,
    marginBottom: 16,
  },
  stepRow: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    marginBottom: 18,
  },
  stepItem: {
    width: '28%',
    alignItems: 'center',
  },
  stepCircle: {
    width: 34,
    height: 34,
    borderRadius: 17,
    backgroundColor: '#D1D5DB',
    marginBottom: 6,
  },
  stepCircleActive: {
    backgroundColor: '#1D4ED8',
  },
  stepLine: {
    flex: 1,
    height: 2,
    backgroundColor: '#D1D5DB',
    marginTop: 16,
    marginHorizontal: 6,
  },
  stepLineActive: {
    backgroundColor: '#1D4ED8',
  },
  stepLabel: {
    fontSize: 10,
    color: '#9CA3AF',
    marginBottom: 2,
  },
  stepTitle: {
    fontSize: 13,
    fontWeight: '600',
    textAlign: 'center',
    color: '#111827',
    marginBottom: 5,
  },
  sheetScroll: { flex: 1 },
  sheetContent: { paddingBottom: 10 },
  questionTitle: {
    fontSize: 22,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 8,
    lineHeight: 30,
  },
  required: { color: '#EF4444' },
  questionSubtitle: {
    fontSize: 15,
    color: '#6B7280',
    marginBottom: 18,
  },
  grid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    justifyContent: 'space-between',
  },
  card: {
    width: '48%',
    minHeight: 150,
    backgroundColor: '#FFFFFF',
    borderRadius: 20,
    borderWidth: 1.5,
    borderColor: '#E5E7EB',
    paddingVertical: 18,
    paddingHorizontal: 12,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 14,
  },
  cardSelected: {
    borderColor: '#2563EB',
    backgroundColor: '#EFF6FF',
  },
  cardIconBox: {
    width: 64,
    height: 64,
    borderRadius: 18,
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 14,
  },
  cardText: {
    fontSize: 15,
    fontWeight: '500',
    textAlign: 'center',
    color: '#111827',
    lineHeight: 20,
  },
  sheetBottomButtons: {
    flexDirection: 'row',
    gap: 12,
    marginTop: 12,
  },
  cancelButton: {
    flex: 1,
    backgroundColor: '#F3F4F6',
    borderRadius: 16,
    height: 54,
    justifyContent: 'center',
    alignItems: 'center',
  },
  cancelButtonText: {
    color: '#374151',
    fontSize: 16,
    fontWeight: '600',
  },
  nextButton: {
    flex: 1,
    backgroundColor: '#1D4ED8',
    borderRadius: 16,
    height: 54,
    justifyContent: 'center',
    alignItems: 'center',
  },
  fullWidthButton: {
    flex: 1,
    backgroundColor: '#1D4ED8',
    borderRadius: 16,
    height: 54,
    justifyContent: 'center',
    alignItems: 'center',
  },
  nextButtonDisabled: {
    backgroundColor: '#93C5FD',
  },
  nextButtonText: {
    color: '#fff',
    fontSize: 16,
    fontWeight: '700',
  },
  selectedTypeBox: {
    minHeight: 84,
    borderRadius: 20,
    borderWidth: 1.5,
    borderColor: '#E5E7EB',
    backgroundColor: '#FFFFFF',
    paddingHorizontal: 16,
    paddingVertical: 14,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: 22,
  },
  selectedTypeLeft: {
    flexDirection: 'row',
    alignItems: 'center',
    flex: 1,
    marginRight: 10,
  },
  selectedTypeIcon: {
    width: 56,
    height: 56,
    borderRadius: 16,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: 12,
  },
  selectedTypeTextWrap: {
    flex: 1,
  },
  selectedTypeMiniLabel: {
    fontSize: 12,
    color: '#9CA3AF',
    marginBottom: 2,
  },
  selectedTypeText: {
    fontSize: 18,
    fontWeight: '700',
    color: '#111827',
    flexShrink: 1,
  },
  changeText: {
    fontSize: 14,
    fontWeight: '600',
    color: '#2563EB',
  },
  sectionTitle: {
    fontSize: 19,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 10,
  },
  locationBox: {
    height: 64,
    borderRadius: 18,
    borderWidth: 1.5,
    borderColor: '#E5E7EB',
    backgroundColor: '#FFFFFF',
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 16,
    marginTop: 6,
  },
  locationText: {
    fontSize: 17,
    color: '#111827',
    marginLeft: 10,
  },
  locationHint: {
    marginTop: 10,
    fontSize: 14,
    color: '#10B981',
    marginBottom: 22,
  },
  descriptionInput: {
    minHeight: 124,
    borderRadius: 18,
    borderWidth: 1.5,
    borderColor: '#E5E7EB',
    backgroundColor: '#FFFFFF',
    paddingHorizontal: 16,
    paddingVertical: 16,
    fontSize: 16,
    color: '#111827',
    marginBottom: 22,
  },
  photoBox: {
    minHeight: 150,
    borderRadius: 18,
    borderWidth: 1.5,
    borderColor: '#E5E7EB',
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    marginTop: 6,
    paddingHorizontal: 16,
  },
  photoIconCircle: {
    width: 64,
    height: 64,
    borderRadius: 32,
    backgroundColor: '#F3F4F6',
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 12,
  },
  photoTitle: {
    fontSize: 16,
    fontWeight: '600',
    color: '#111827',
    marginBottom: 6,
  },
  photoSubtitle: {
    fontSize: 13,
    color: '#9CA3AF',
    textAlign: 'center',
  },
  successContainer: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'flex-start',
    paddingHorizontal: 24,
    paddingTop: 40,
  },
  successIconCircle: {
    width: 140,
    height: 140,
    borderRadius: 70,
    backgroundColor: '#DCFCE7',
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 22,
  },
  successTitle: {
    fontSize: 22,
    fontWeight: '800',
    color: '#111827',
    marginBottom: 8,
  },
  successSubtitle: {
    fontSize: 16,
    color: '#6B7280',
    textAlign: 'center',
    marginBottom: 18,
    lineHeight: 22,
  },
  successMessageBox: {
    width: '100%',
    backgroundColor: '#F3F4F6',
    borderRadius: 18,
    paddingHorizontal: 18,
    paddingVertical: 16,
  },
  successMessageText: {
    fontSize: 15,
    color: '#374151',
    textAlign: 'center',
    lineHeight: 22,
  },
});
