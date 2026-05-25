import React, { useEffect, useRef, useState } from 'react';
import { View, Alert } from 'react-native';
import { router } from 'expo-router';
import * as Location from 'expo-location';
import * as ImagePicker from 'expo-image-picker';

import ReportHazardModal from '../../../components/MapView/ReportHazardModal';
import { ReportHazardType } from '../../../components/MapView/MapTypes';
import {
  geocodingService,
  type GeocodingResult,
} from '../../../services/geocoding.service';
import { hazardsService } from '../../../services/hazards.service';
import type { HazardPhotoUpload } from '../../../services/hazards.service';
import {
  aiAssistService,
  type HazardPhotoAiAnalysisResult,
  type HazardReportDraftAiResult,
} from '../../../services/aiAssist.service';

type PhotoAnalysisStatus = 'idle' | 'uploading' | 'analyzing' | 'ready' | 'error';

function formatReverseGeocode(result: GeocodingResult | null) {
  if (!result) return null;

  if (typeof result.display_name === 'string' && result.display_name.trim()) {
    return result.display_name.trim();
  }

  if (typeof result.name === 'string' && result.name.trim()) {
    return result.name.trim();
  }

  if (result.address) {
    const addressParts = [
      result.address.road,
      result.address.neighbourhood,
      result.address.suburb,
      result.address.city,
      result.address.town,
      result.address.village,
      result.address.postcode,
    ].filter((value): value is string => typeof value === 'string' && value.trim().length > 0);

    if (addressParts.length > 0) {
      return addressParts.slice(0, 3).join(', ');
    }
  }

  return null;
}

export default function ReportPage() {
  const [isMounted, setIsMounted] = useState(false);
  const [reportModalVisible, setReportModalVisible] = useState(true);
  const aiDraftRequestRef = useRef(0);

  const [reportStep, setReportStep] = useState<1 | 2 | 3>(1);
  const [selectedReportType, setSelectedReportType] =
    useState<ReportHazardType | null>(null);
  const [reportDescription, setReportDescription] = useState('');
  const [reportSeverity, setReportSeverity] = useState<'Low' | 'Medium' | 'High'>('Medium');
  const [selectedPhoto, setSelectedPhoto] = useState<HazardPhotoUpload | null>(null);
  const [similarReportCount, setSimilarReportCount] = useState(0);
  const [isCheckingSimilarReports, setIsCheckingSimilarReports] = useState(false);
  const [aiDraft, setAiDraft] = useState<HazardReportDraftAiResult | null>(null);
  const [isLoadingAiDraft, setIsLoadingAiDraft] = useState(false);
  const [photoAnalysisStatus, setPhotoAnalysisStatus] = useState<PhotoAnalysisStatus>('idle');
  const [photoAnalysis, setPhotoAnalysis] = useState<HazardPhotoAiAnalysisResult | null>(null);

  const [currentLocation, setCurrentLocation] = useState<{
    latitude: number;
    longitude: number;
  } | null>(null);
  const [locationLabel, setLocationLabel] = useState('Current Location');
  const [locationHint, setLocationHint] = useState('Waiting for GPS fix');
  const [isResolvingLocation, setIsResolvingLocation] = useState(false);

  useEffect(() => {
    setIsMounted(true);
    setReportModalVisible(true);
    getCurrentLocation();
  }, []);

  useEffect(() => {
    let isMountedForSimilarCheck = true;

    async function loadSimilarReports() {
      if (!currentLocation || !selectedReportType || reportStep !== 2) {
        return;
      }

      setIsCheckingSimilarReports(true);
      try {
        const delta = 0.002;
        const page = await hazardsService.getHazardsPage({
          status: 'Reported',
          minLat: currentLocation.latitude - delta,
          minLng: currentLocation.longitude - delta,
          maxLat: currentLocation.latitude + delta,
          maxLng: currentLocation.longitude + delta,
          limit: 10,
        });

        if (!isMountedForSimilarCheck) return;

        const typeLabel = selectedReportType.replace(/[_-]+/g, ' ').toLowerCase();
        const sameIssueCount = page.items.filter((hazard) => {
          const haystack = `${hazard.type} ${hazard.title} ${hazard.description}`.toLowerCase();
          return haystack.includes(typeLabel) || typeLabel.split(' ').some((part) => part.length > 3 && haystack.includes(part));
        }).length;
        setSimilarReportCount(sameIssueCount);
      } catch {
        if (isMountedForSimilarCheck) {
          setSimilarReportCount(0);
        }
      } finally {
        if (isMountedForSimilarCheck) {
          setIsCheckingSimilarReports(false);
        }
      }
    }

    void loadSimilarReports();

    return () => {
      isMountedForSimilarCheck = false;
    };
  }, [currentLocation, reportStep, selectedReportType]);

  useEffect(() => {
    if (!currentLocation || !selectedReportType || reportStep !== 2) {
      return;
    }

    const requestId = ++aiDraftRequestRef.current;
    setIsLoadingAiDraft(true);

    const timer = setTimeout(() => {
      aiAssistService.previewHazardReportDraft({
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
        type: selectedReportType,
        description: reportDescription,
        photoAttached: Boolean(selectedPhoto),
      })
        .then((result) => {
          if (aiDraftRequestRef.current !== requestId) return;
          setAiDraft(result);
          const suggestedSeverity = normalizeAiSeverity(result.text.suggestedSeverity);
          if (suggestedSeverity && !reportDescription.trim()) {
            setReportSeverity(suggestedSeverity);
          }
        })
        .catch((error) => {
          if (aiDraftRequestRef.current !== requestId) return;
          console.warn('AI hazard draft preview unavailable:', error);
          setAiDraft(null);
        })
        .finally(() => {
          if (aiDraftRequestRef.current === requestId) {
            setIsLoadingAiDraft(false);
          }
        });
    }, 450);

    return () => {
      clearTimeout(timer);
    };
  }, [currentLocation, reportDescription, reportStep, selectedPhoto, selectedReportType]);

  async function getCurrentLocation() {
    try {
      const { status } = await Location.requestForegroundPermissionsAsync();

      if (status !== 'granted') {
        setLocationLabel('Location permission needed');
        setLocationHint('Enable location to attach this report to the map');
        Alert.alert('Permission denied', 'Location permission is required.');
        return;
      }

      const location = await Location.getCurrentPositionAsync({});
      const coordinates = {
        latitude: location.coords.latitude,
        longitude: location.coords.longitude,
      };

      setCurrentLocation(coordinates);
      setLocationLabel('Current Location');
      setLocationHint('Location ready');
      setIsResolvingLocation(true);

      try {
        const reverseGeocode = await geocodingService.reverse(
          coordinates.latitude,
          coordinates.longitude
        );
        const resolvedLabel = formatReverseGeocode(reverseGeocode);

        if (resolvedLabel) {
          setLocationLabel(resolvedLabel);
          setLocationHint('Location matched automatically');
        }
      } catch (reverseError) {
        console.warn('Reverse geocoding failed:', reverseError);
        setLocationHint('Location ready');
      } finally {
        setIsResolvingLocation(false);
      }
    } catch (error) {
      console.error('Location error:', error);
      setLocationLabel('Location unavailable');
      setLocationHint('Try again after the device has a GPS fix');
      setIsResolvingLocation(false);
      Alert.alert('Error', 'Could not get location.');
    }
  }

  function handleClose() {
    setReportModalVisible(false);
    setSimilarReportCount(0);
    setAiDraft(null);
    setPhotoAnalysis(null);
    setPhotoAnalysisStatus('idle');
    router.back();
  }

  function handleNext() {
    if (!selectedReportType) {
      Alert.alert('Missing type', 'Please select a hazard type.');
      return;
    }
    setReportStep(2);
  }

  function handleBack() {
    setSimilarReportCount(0);
    setAiDraft(null);
    setPhotoAnalysis(null);
    setPhotoAnalysisStatus('idle');
    setReportStep(1);
  }

  async function handleAddPhoto() {
    try {
      const permission = await ImagePicker.requestMediaLibraryPermissionsAsync();
      if (!permission.granted) {
        Alert.alert('Photo access needed', 'Allow photo access to attach an image to this hazard report.');
        return;
      }

      const result = await ImagePicker.launchImageLibraryAsync({
        mediaTypes: ImagePicker.MediaTypeOptions.Images,
        allowsEditing: false,
        quality: 0.82,
      });

      if (result.canceled || !result.assets?.[0]?.uri) {
        return;
      }

      const asset = result.assets[0];
      setSelectedPhoto({
        uri: asset.uri,
        name: asset.fileName || `hazard-photo-${Date.now()}.jpg`,
        type: asset.mimeType || 'image/jpeg',
      });
      setPhotoAnalysis(null);
      setPhotoAnalysisStatus('idle');
    } catch (error) {
      console.error('Photo selection error:', error);
      Alert.alert('Photo error', 'Could not select a photo.');
    }
  }

  async function handleSubmit() {
    if (!selectedReportType) {
      Alert.alert('Missing type', 'Please select a hazard type.');
      return;
    }

    if (!currentLocation) {
      Alert.alert('Location unavailable', 'Location not ready.');
      return;
    }

    try {
      const aiNormalizedDescription = aiDraft?.text.normalizedDescription?.trim();
      const submittedDescription = aiNormalizedDescription || reportDescription.trim();
      const normalizedDescription = [
        submittedDescription,
        `Severity: ${reportSeverity}.`,
      ].filter(Boolean).join('\n');

      setPhotoAnalysis(null);
      setPhotoAnalysisStatus(selectedPhoto ? 'uploading' : 'idle');
      const createdHazard = await hazardsService.reportHazard({
        latitude: currentLocation.latitude,
        longitude: currentLocation.longitude,
        type: selectedReportType,
        description: normalizedDescription,
      });

      setReportStep(3);

      if (selectedPhoto && createdHazard.id) {
        try {
          const upload = await hazardsService.uploadHazardPhoto(createdHazard.id, selectedPhoto);
          setPhotoAnalysisStatus('analyzing');
          const analysis = await aiAssistService.analyzeHazardPhoto(createdHazard.id, {
            photoUrl: upload.photoUrl,
            observationText: normalizedDescription,
            includeDraftVerification: true,
          });
          setPhotoAnalysis(analysis);
          setPhotoAnalysisStatus('ready');
        } catch (photoError) {
          console.warn('Photo upload or analysis failed:', photoError);
          setPhotoAnalysisStatus('error');
        }
      }
    } catch (error) {
      console.error('Submit error:', error);
      setPhotoAnalysisStatus('idle');
      Alert.alert('Submit error', 'Could not submit report.');
    }
  }

  function handleDone() {
    setReportModalVisible(false);
    setSelectedPhoto(null);
    setSimilarReportCount(0);
    setAiDraft(null);
    setPhotoAnalysis(null);
    setPhotoAnalysisStatus('idle');
    router.back();
  }

  function handleReviewSimilarReports() {
    setReportModalVisible(false);
    router.push('/hazard' as never);
  }

  function handleApplyAiDraft() {
    if (!aiDraft) return;

    const normalized = aiDraft.text.normalizedDescription?.trim();
    if (normalized) {
      setReportDescription(normalized);
    }

    const suggestedSeverity = normalizeAiSeverity(aiDraft.text.suggestedSeverity);
    if (suggestedSeverity) {
      setReportSeverity(suggestedSeverity);
    }
  }

  return (
    <View style={{ flex: 1 }}>
      {isMounted ? (
        <ReportHazardModal
          visible={reportModalVisible}
          reportStep={reportStep}
          selectedReportType={selectedReportType}
          reportDescription={reportDescription}
          severity={reportSeverity}
          onClose={handleClose}
          onSelectType={setSelectedReportType}
          onChangeDescription={setReportDescription}
          onChangeSeverity={setReportSeverity}
          onAddPhoto={() => void handleAddPhoto()}
          selectedPhotoLabel={selectedPhoto?.name}
          aiDraftSuggestion={aiDraft ? {
            normalizedDescription: aiDraft.text.normalizedDescription,
            suggestedType: aiDraft.text.suggestedType,
            suggestedSeverity: aiDraft.text.suggestedSeverity,
            confidence: aiDraft.text.confidence,
            tags: aiDraft.text.tags,
            duplicateCount: aiDraft.duplicateSuggestions.length,
            shouldReviewExistingReport: aiDraft.shouldReviewExistingReport,
            osmCandidateCount: aiDraft.missingOsmAttributeCandidates.length,
            suggestedDescriptionChips: aiDraft.suggestedDescriptionChips,
          } : null}
          isLoadingAiDraft={isLoadingAiDraft}
          onApplyAiDraft={handleApplyAiDraft}
          photoAnalysis={selectedPhoto ? {
            status: photoAnalysisStatus,
            provider: photoAnalysis?.provider,
            model: photoAnalysis?.model,
            candidateCount: photoAnalysis?.attributeCandidates.length,
            topCandidates: photoAnalysis?.attributeCandidates.map((candidate) => candidate.attribute),
            reviewStatus: photoAnalysis?.reviewStatus,
          } : null}
          onNext={handleNext}
          onBack={handleBack}
          onSubmit={handleSubmit}
          onDone={handleDone}
          locationLabel={locationLabel}
          locationHint={locationHint}
          isResolvingLocation={isResolvingLocation}
          canSubmit={Boolean(currentLocation)}
          onRetryLocation={() => void getCurrentLocation()}
          similarReportCount={similarReportCount}
          isCheckingSimilarReports={isCheckingSimilarReports}
          onReviewSimilarReports={handleReviewSimilarReports}
        />
      ) : null}
    </View>
  );
}

function normalizeAiSeverity(value: string | undefined): 'Low' | 'Medium' | 'High' | null {
  switch (value?.toLowerCase()) {
    case 'low':
      return 'Low';
    case 'medium':
      return 'Medium';
    case 'high':
      return 'High';
    default:
      return null;
  }
}
