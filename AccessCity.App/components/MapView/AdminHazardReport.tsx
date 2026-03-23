import React, { useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  TouchableOpacity,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { api } from '../../services/api';

/**
 * ============================
 * TODO: Replace these types later
 * ============================
 */

type ReviewStatus = 'pending' | 'approved' | 'rejected';

type PendingHazardItem = {
  id: string;
  category: string;
  locationName: string;
  createdAt: string;
  title?: string;
  severity?: string;
  reporterName?: string;
};

type HazardDetails = {
  id: string;
  category: string;
  status: ReviewStatus;
  title: string;
  severity?: string;
  description: string;
  imageUrl?: string | null;
  coordinates?: {
    latitude: number;
    longitude: number;
  } | null;
  locationName: string;
  submittedAt: string;
  reporter?: {
    name?: string;
    email?: string;
    verified?: boolean;
  } | null;
};

type ReviewStats = {
  reviewedToday: number;
  waitingForReview: number;
};

type ScreenMode =
  | 'list'
  | 'details'
  | 'decision'
  | 'success-approved'
  | 'success-rejected';

type AdminHazardReportProps = {
  /**
   * Optional: If you want to control the return from an external page
   * For example, in ReportPage, if you want to exit the admin page...
   */
  onClose?: () => void;
};

/**
 * ============================
 * TODO: Replace admin id later
 * ============================
 * I'll write it out now and wait for you guys. auth / user profile 接好了，
 * Change it to the ID of the real logged-in user.
 */
const MOCK_ADMIN_ID = 'admin_id_001';

/**
 * ============================
 * Utility function: Formatting time
 * ============================
 */
function formatRelativeTime(dateString: string) {
  const date = new Date(dateString);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();

  if (Number.isNaN(date.getTime())) return dateString;

  const diffMin = Math.floor(diffMs / (1000 * 60));
  if (diffMin < 1) return 'Just now';
  if (diffMin < 60) return `${diffMin} minute${diffMin > 1 ? 's' : ''} ago`;

  const diffHour = Math.floor(diffMin / 60);
  if (diffHour < 24) return `${diffHour} hour${diffHour > 1 ? 's' : ''} ago`;

  const diffDay = Math.floor(diffHour / 24);
  if (diffDay < 7) return `${diffDay} day${diffDay > 1 ? 's' : ''} ago`;

  return date.toLocaleString();
}

function formatDateTime(dateString: string) {
  const date = new Date(dateString);
  if (Number.isNaN(date.getTime())) return dateString;
  return date.toLocaleString();
}

/**
 * ============================
 * TODO: Replace backend mapping later
 * ============================
 * In the future, if the backend changes a field,
 * only this part needs to be changed, instead of changing the entire component.
 */

function mapPendingReport(item: any): PendingHazardItem {
  return {
    id: String(item.id),
    category: item.type ?? item.category ?? 'Unknown',
    locationName:
      item.locationName ??
      item.location ??
      (item.location?.x != null && item.location?.y != null
        ? `${item.location.y}, ${item.location.x}`
        : 'Unknown location'),
    createdAt:
      item.reportedAt ?? item.createdAt ?? item.submittedAt ?? new Date().toISOString(),
    title:
      item.title ??
      (item.type ?? item.category
        ? `${item.type ?? item.category} Report`
        : 'Hazard Report'),
    severity: item.severity ?? item.priority ?? 'Normal',
    reporterName:
      item.reporterName ??
      item.reporter?.name ??
      item.submittedBy ??
      'Unknown Reporter',
  };
}

function mapHazardDetails(item: any): HazardDetails {
  return {
    id: String(item.id),
    category: item.category ?? item.type ?? 'Unknown',
    status: item.status ?? 'pending',
    title:
      item.title ??
      (item.category ? `${item.category} Report` : 'Hazard Report'),
    severity: item.severity ?? item.priority ?? 'Normal',
    description: item.description ?? 'No description provided.',
    imageUrl: item.imageUrl ?? item.photoUrl ?? null,
    coordinates: item.coordinates
      ? {
          latitude: Number(item.coordinates.latitude),
          longitude: Number(item.coordinates.longitude),
        }
      : item.latitude != null && item.longitude != null
      ? {
          latitude: Number(item.latitude),
          longitude: Number(item.longitude),
        }
      : null,
    locationName: item.locationName ?? item.location ?? 'Unknown location',
    submittedAt: item.createdAt ?? item.submittedAt ?? new Date().toISOString(),
    reporter: item.reporter
      ? {
          name: item.reporter.name,
          email: item.reporter.email,
          verified: Boolean(item.reporter.verified),
        }
      : {
          name: item.reporterName ?? item.submittedBy ?? 'Unknown Reporter',
          email: item.reporterEmail ?? '',
          verified: Boolean(item.reporterVerified),
        },
  };
}

function getCategoryColor(category: string) {
  const lower = category.toLowerCase();

  if (lower.includes('emergency')) return '#FEE2E2';
  if (lower.includes('obstruction')) return '#FEF3C7';
  if (lower.includes('lighting')) return '#DBEAFE';
  return '#E5E7EB';
}

function getCategoryTextColor(category: string) {
  const lower = category.toLowerCase();

  if (lower.includes('emergency')) return '#DC2626';
  if (lower.includes('obstruction')) return '#D97706';
  if (lower.includes('lighting')) return '#2563EB';
  return '#4B5563';
}

export default function AdminHazardReport({ onClose }: AdminHazardReportProps) {
  const [screenMode, setScreenMode] = useState<ScreenMode>('list');

  const [pendingReports, setPendingReports] = useState<PendingHazardItem[]>([]);
  const [selectedReportId, setSelectedReportId] = useState<string | null>(null);
  const [selectedReport, setSelectedReport] = useState<HazardDetails | null>(
    null
  );

  const [adminNotes, setAdminNotes] = useState('');
  const [searchText, setSearchText] = useState('');

  const [reviewStats, setReviewStats] = useState<ReviewStats>({
    reviewedToday: 0,
    waitingForReview: 0,
  });

  const [loadingList, setLoadingList] = useState(false);
  const [loadingDetails, setLoadingDetails] = useState(false);
  const [submittingDecision, setSubmittingDecision] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  /**
   * ============================
   * API 1: Get the list of items pending review
   * GET /hazards/pending
   * ============================
   */
  async function fetchPendingReports(showRefresh = false) {
    try {
      if (showRefresh) {
        setRefreshing(true);
      } else {
        setLoadingList(true);
      }

      const response = await api.get<any[]>('/hazards');

      const allItems = Array.isArray(response) ? response : [];

      const pendingItems = allItems.filter((item) => {
        const status = String(item.status ?? '').toLowerCase();
        return status === 'reported' || status === 'underreview';
      });

      const mapped = pendingItems.map(mapPendingReport);

      setPendingReports(mapped);

      setReviewStats((prev) => ({
        ...prev,
        waitingForReview: mapped.length,
      }));
    } catch (error) {
      console.error('Failed to fetch pending reports:', error);
      Alert.alert('Error', 'Failed to load pending reports.');
    } finally {
      setLoadingList(false);
      setRefreshing(false);
    }
  }

  /**
   * ============================
   * API 2: Get details of a single hazard.
   * GET /hazards/{id}
   * ============================
   */
  async function fetchReportDetails(reportId: string) {
    try {
      setLoadingDetails(true);

      /**
       * TODO:
       * If the backend provides /hazards/report/{id} or something else,
       * update
       */
      const response = await api.get<any>(`/hazards/${reportId}`);
      const mapped = mapHazardDetails(response);

      setSelectedReportId(reportId);
      setSelectedReport(mapped);
      setScreenMode('details');
    } catch (error) {
      console.error('Failed to fetch report details:', error);
      Alert.alert('Error', 'Failed to load report details.');
    } finally {
      setLoadingDetails(false);
    }
  }

  /**
   * ============================
   * API 3: Submit Review Decision
   * PATCH /hazards/{id}/review
   * body:
   * {
   *   status: "approved" | "rejected",
   *   adminNotes: "...",
   *   reviewedBy: "admin_id_001"
   * }
   * ============================
   */
  async function submitReviewDecision(status: 'approved' | 'rejected') {
    if (!selectedReportId) return;

    try {
      setSubmittingDecision(true);

      const payload = {
        status,
        adminNotes,
        reviewedBy: MOCK_ADMIN_ID,
      };

      /**
       * TODO:
       * If the backend uses PATCH instead of POST,
       * update：api.patch(`/hazards/${selectedReportId}/review`, payload)
       */
      await api.post(`/hazards/${selectedReportId}/review`, payload);

      /**
       * After a successful decision:
       * 1. Update statistics
       * 2. Remove the current item from the pending review list
       * 3. Redirect to the success page
       */
      setPendingReports((prev) =>
        prev.filter((item) => item.id !== selectedReportId)
      );

      setReviewStats((prev) => ({
        reviewedToday: prev.reviewedToday + 1,
        waitingForReview: Math.max(prev.waitingForReview - 1, 0),
      }));

      setScreenMode(
        status === 'approved' ? 'success-approved' : 'success-rejected'
      );
    } catch (error) {
      console.error('Failed to submit review decision:', error);
      Alert.alert('Error', 'Failed to submit review decision.');
    } finally {
      setSubmittingDecision(false);
    }
  }

  /**
   * ============================
   * API 4: Get Review Statistics
   * GET /admin/stats/daily
   * ============================
   */
  async function fetchReviewStats() {
    try {
      const response = await api.get<any>('/dashboard/summary');

      setReviewStats((prev) => ({
        reviewedToday: prev.reviewedToday, 
        waitingForReview: Number(
          response?.pendingAlerts ?? response?.PendingAlerts ?? prev.waitingForReview ?? 0
        ),
      }));
    } catch (error) {
      console.error('Failed to fetch review stats:', error);
    }
  }

  useEffect(() => {
    fetchPendingReports();
    fetchReviewStats();
  }, []);

  const filteredReports = useMemo(() => {
    const keyword = searchText.trim().toLowerCase();
    if (!keyword) return pendingReports;

    return pendingReports.filter((item) => {
      return (
        item.title?.toLowerCase().includes(keyword) ||
        item.category?.toLowerCase().includes(keyword) ||
        item.locationName?.toLowerCase().includes(keyword) ||
        item.reporterName?.toLowerCase().includes(keyword)
      );
    });
  }, [pendingReports, searchText]);

  function handleOpenDecisionPage() {
    setScreenMode('decision');
  }

  function handleBackFromDetails() {
    setScreenMode('list');
    setSelectedReport(null);
    setSelectedReportId(null);
    setAdminNotes('');
  }

  function handleBackFromDecision() {
    setScreenMode('details');
  }

  async function handleReturnToPendingReports() {
    setScreenMode('list');
    setSelectedReport(null);
    setSelectedReportId(null);
    setAdminNotes('');
    await fetchPendingReports(true);
    await fetchReviewStats();
  }

  async function handleViewNextReport() {
    const remaining = pendingReports.filter((item) => item.id !== selectedReportId);

    if (remaining.length === 0) {
      Alert.alert('No more reports', 'There are no more pending reports right now.');
      await handleReturnToPendingReports();
      return;
    }

    const nextReport = remaining[0];
    await fetchReportDetails(nextReport.id);
  }

  /**
   * ============================
   * List Page
   * ============================
   */
  function renderPendingReportsList() {
    return (
      <View style={styles.container}>
        <View style={styles.header}>
          <Text style={styles.headerTitle}>Pending Reports</Text>
          <Text style={styles.headerSubtitle}>
            Review and manage community hazard reports
          </Text>
        </View>

        <View style={styles.searchContainer}>
          <Ionicons name="search-outline" size={18} color="#9CA3AF" />
          <TextInput
            style={styles.searchInput}
            placeholder="Search reports..."
            placeholderTextColor="#9CA3AF"
            value={searchText}
            onChangeText={setSearchText}
          />
        </View>

        {loadingList ? (
          <View style={styles.centerState}>
            <ActivityIndicator size="large" color="#0F4C92" />
            <Text style={styles.centerStateText}>Loading pending reports...</Text>
          </View>
        ) : (
          <ScrollView
            contentContainerStyle={styles.listContent}
            refreshControl={
              <RefreshControl
                refreshing={refreshing}
                onRefresh={() => {
                  fetchPendingReports(true);
                  fetchReviewStats();
                }}
              />
            }
          >
            {filteredReports.length === 0 ? (
              <View style={styles.emptyCard}>
                <Ionicons name="document-text-outline" size={32} color="#9CA3AF" />
                <Text style={styles.emptyTitle}>No pending reports</Text>
                <Text style={styles.emptyText}>
                  New hazard reports waiting for review will appear here.
                </Text>
              </View>
            ) : (
              filteredReports.map((item) => (
                <TouchableOpacity
                  key={item.id}
                  style={styles.reportCard}
                  activeOpacity={0.85}
                  onPress={() => fetchReportDetails(item.id)}
                >
                  <View style={styles.cardTopRow}>
                    <View
                      style={[
                        styles.tag,
                        {
                          backgroundColor: getCategoryColor(item.category),
                        },
                      ]}
                    >
                      <Text
                        style={[
                          styles.tagText,
                          {
                            color: getCategoryTextColor(item.category),
                          },
                        ]}
                      >
                        {item.category}
                      </Text>
                    </View>

                    <View style={styles.pendingTag}>
                      <Text style={styles.pendingTagText}>Pending</Text>
                    </View>
                  </View>

                  <Text style={styles.reportTitle}>
                    {item.title || 'Hazard Report'}
                  </Text>

                  <Text style={styles.reportDescriptionPreview} numberOfLines={2}>
                    {item.locationName}
                  </Text>

                  <View style={styles.metaRow}>
                    <View style={styles.metaItem}>
                      <Ionicons name="location-outline" size={14} color="#6B7280" />
                      <Text style={styles.metaText}>{item.locationName}</Text>
                    </View>

                    <View style={styles.metaItem}>
                      <Ionicons name="time-outline" size={14} color="#6B7280" />
                      <Text style={styles.metaText}>
                        {formatRelativeTime(item.createdAt)}
                      </Text>
                    </View>
                  </View>

                  <View style={styles.cardFooter}>
                    <Text style={styles.reporterText}>
                      Reported by {item.reporterName || 'Unknown Reporter'}
                    </Text>
                    <Ionicons name="chevron-forward" size={18} color="#9CA3AF" />
                  </View>
                </TouchableOpacity>
              ))
            )}
          </ScrollView>
        )}

        {onClose ? (
          <TouchableOpacity style={styles.closeButton} onPress={onClose}>
            <Ionicons name="close-outline" size={20} color="#FFFFFF" />
          </TouchableOpacity>
        ) : null}
      </View>
    );
  }

  /**
   * ============================
   * Details Page
   * ============================
   */
  function renderDetailsPage() {
    if (loadingDetails || !selectedReport) {
      return (
        <View style={styles.container}>
          <View style={styles.header}>
            <TouchableOpacity style={styles.backButton} onPress={handleBackFromDetails}>
              <Ionicons name="arrow-back" size={18} color="#FFFFFF" />
              <Text style={styles.backButtonText}>Back</Text>
            </TouchableOpacity>

            <Text style={styles.headerTitle}>Report Details</Text>
          </View>

          <View style={styles.centerState}>
            <ActivityIndicator size="large" color="#0F4C92" />
            <Text style={styles.centerStateText}>Loading report details...</Text>
          </View>
        </View>
      );
    }

    return (
      <View style={styles.container}>
        <View style={styles.header}>
          <TouchableOpacity style={styles.backButton} onPress={handleBackFromDetails}>
            <Ionicons name="arrow-back" size={18} color="#FFFFFF" />
            <Text style={styles.backButtonText}>Back</Text>
          </TouchableOpacity>

          <Text style={styles.headerTitle}>Report Details</Text>
        </View>

        <ScrollView contentContainerStyle={styles.detailsContent}>
          <View style={styles.sectionCard}>
            <View style={styles.cardTopRow}>
              <View
                style={[
                  styles.tag,
                  {
                    backgroundColor: getCategoryColor(selectedReport.category),
                  },
                ]}
              >
                <Text
                  style={[
                    styles.tagText,
                    {
                      color: getCategoryTextColor(selectedReport.category),
                    },
                  ]}
                >
                  {selectedReport.category}
                </Text>
              </View>

              <View style={styles.pendingTag}>
                <Text style={styles.pendingTagText}>Pending</Text>
              </View>
            </View>

            <Text style={styles.detailsTitle}>{selectedReport.title}</Text>

            <Text style={styles.sectionLabel}>Description</Text>
            <Text style={styles.detailsParagraph}>{selectedReport.description}</Text>

            <Text style={styles.sectionLabel}>Location</Text>
            <Text style={styles.detailsParagraph}>{selectedReport.locationName}</Text>

            <View style={styles.divider} />

            <View style={styles.twoColumnRow}>
              <View style={styles.infoBlock}>
                <Text style={styles.sectionLabel}>Submitted</Text>
                <Text style={styles.smallInfoText}>
                  {formatDateTime(selectedReport.submittedAt)}
                </Text>
              </View>

              <View style={styles.infoBlock}>
                <Text style={styles.sectionLabel}>Reporter</Text>
                <Text style={styles.smallInfoText}>
                  {selectedReport.reporter?.name || 'Unknown Reporter'}
                </Text>
                {selectedReport.reporter?.email ? (
                  <Text style={styles.smallMutedText}>
                    {selectedReport.reporter.email}
                  </Text>
                ) : null}
                <Text
                  style={[
                    styles.verifiedText,
                    {
                      color: selectedReport.reporter?.verified
                        ? '#059669'
                        : '#9CA3AF',
                    },
                  ]}
                >
                  {selectedReport.reporter?.verified
                    ? 'Verified User'
                    : 'Unverified User'}
                </Text>
              </View>
            </View>
          </View>

          <View style={styles.sectionCard}>
            <Text style={styles.sectionHeader}>Submitted Photo</Text>
            <View style={styles.placeholderBox}>
              <Ionicons name="camera-outline" size={34} color="#9CA3AF" />
              <Text style={styles.placeholderTitle}>
                {selectedReport.imageUrl
                  ? 'Photo URL received from backend'
                  : 'No photo uploaded'}
              </Text>
              <Text style={styles.placeholderSubtitle}>
                {selectedReport.imageUrl
                  ? selectedReport.imageUrl
                  : 'TODO: Replace this placeholder with a real Image component later.'}
              </Text>
            </View>
          </View>

          <View style={styles.sectionCard}>
            <Text style={styles.sectionHeader}>Location on Map</Text>
            <View style={styles.placeholderBox}>
              <Ionicons name="location-outline" size={34} color="#3B82F6" />
              <Text style={styles.placeholderTitle}>Map preview</Text>
              <Text style={styles.placeholderSubtitle}>
                {selectedReport.coordinates
                  ? `${selectedReport.coordinates.latitude}, ${selectedReport.coordinates.longitude}`
                  : selectedReport.locationName}
              </Text>
              <Text style={styles.placeholderSubtitle}>
                TODO: If you want, this block can later be replaced by MapView.
              </Text>
            </View>
          </View>
        </ScrollView>

        <View style={styles.bottomActionBar}>
          <TouchableOpacity
            style={styles.primaryButton}
            onPress={handleOpenDecisionPage}
          >
            <Text style={styles.primaryButtonText}>Review Report</Text>
          </TouchableOpacity>
        </View>
      </View>
    );
  }

  /**
   * ============================
   * Review decision page
   * ============================
   */
  function renderDecisionPage() {
    if (!selectedReport) return null;

    return (
      <View style={styles.container}>
        <View style={styles.header}>
          <TouchableOpacity style={styles.backButton} onPress={handleBackFromDecision}>
            <Ionicons name="arrow-back" size={18} color="#FFFFFF" />
            <Text style={styles.backButtonText}>Back</Text>
          </TouchableOpacity>

          <Text style={styles.headerTitle}>Review Report</Text>
          <Text style={styles.headerSubtitle}>
            Make a decision on this hazard report
          </Text>
        </View>

        <ScrollView contentContainerStyle={styles.detailsContent}>
          <View style={styles.sectionCard}>
            <View style={styles.cardTopRow}>
              <View
                style={[
                  styles.tag,
                  {
                    backgroundColor: getCategoryColor(selectedReport.category),
                  },
                ]}
              >
                <Text
                  style={[
                    styles.tagText,
                    {
                      color: getCategoryTextColor(selectedReport.category),
                    },
                  ]}
                >
                  {selectedReport.category}
                </Text>
              </View>

              <View style={styles.pendingTag}>
                <Text style={styles.pendingTagText}>Pending Review</Text>
              </View>
            </View>

            <Text style={styles.detailsTitle}>{selectedReport.title}</Text>

            <View style={styles.metaVerticalItem}>
              <Ionicons name="location-outline" size={16} color="#6B7280" />
              <Text style={styles.metaVerticalText}>{selectedReport.locationName}</Text>
            </View>

            <View style={styles.metaVerticalItem}>
              <Ionicons name="time-outline" size={16} color="#6B7280" />
              <Text style={styles.metaVerticalText}>
                Submitted {formatDateTime(selectedReport.submittedAt)}
              </Text>
            </View>

            <Text style={styles.reporterTextBlock}>
              Reported by {selectedReport.reporter?.name || 'Unknown Reporter'}
            </Text>
          </View>

          <View style={styles.sectionCard}>
            <Text style={styles.sectionHeader}>Admin Notes (Optional)</Text>
            <TextInput
              style={styles.notesInput}
              placeholder="Add any notes about this decision for internal use"
              placeholderTextColor="#9CA3AF"
              multiline
              value={adminNotes}
              onChangeText={setAdminNotes}
            />
            <Text style={styles.smallMutedText}>
              These notes are for internal use only and will not be visible to the reporter.
            </Text>
          </View>

          <View style={styles.impactCard}>
            <Text style={styles.sectionHeader}>Decision Impact</Text>
            <Text style={styles.impactLine}>
              • Approve: Hazard will be published to the public map and users will be notified
            </Text>
            <Text style={styles.impactLine}>
              • Reject: Report will be archived and not appear on the public map
            </Text>
          </View>
        </ScrollView>

        <View style={styles.bottomActionBar}>
          <TouchableOpacity
            style={[styles.approveButton, submittingDecision && styles.disabledButton]}
            disabled={submittingDecision}
            onPress={() => submitReviewDecision('approved')}
          >
            {submittingDecision ? (
              <ActivityIndicator color="#FFFFFF" />
            ) : (
              <>
                <Ionicons name="checkmark-circle-outline" size={18} color="#FFFFFF" />
                <Text style={styles.approveButtonText}>Approve Report</Text>
              </>
            )}
          </TouchableOpacity>

          <TouchableOpacity
            style={[styles.rejectButton, submittingDecision && styles.disabledButton]}
            disabled={submittingDecision}
            onPress={() => submitReviewDecision('rejected')}
          >
            <Ionicons name="close-circle-outline" size={18} color="#DC2626" />
            <Text style={styles.rejectButtonText}>Reject Report</Text>
          </TouchableOpacity>
        </View>
      </View>
    );
  }

  /**
   * ============================
   * Success page (approved / rejected)
   * ============================
   */
  function renderResultPage(approved: boolean) {
    return (
      <View style={styles.container}>
        <View style={styles.header}>
          <Text style={styles.headerTitle}>
            {approved ? 'Report Approved' : 'Report Rejected'}
          </Text>
        </View>

        <ScrollView contentContainerStyle={styles.detailsContent}>
          <View
            style={[
              styles.resultCard,
              approved ? styles.resultApprovedCard : styles.resultRejectedCard,
            ]}
          >
            <View
              style={[
                styles.resultIconCircle,
                approved ? styles.resultIconApproved : styles.resultIconRejected,
              ]}
            >
              <Ionicons
                name={approved ? 'checkmark' : 'close'}
                size={32}
                color="#FFFFFF"
              />
            </View>

            <Text style={styles.resultTitle}>
              {approved ? 'Report Approved Successfully' : 'Report Rejected'}
            </Text>

            <Text style={styles.resultText}>
              {approved
                ? 'The hazard report has been approved and will be published to the public safety map.'
                : 'The hazard report has been rejected and will not appear on the public map.'}
            </Text>

            <Text style={styles.resultText}>
              {approved
                ? 'Users in the area will be notified about this hazard and can adjust their routes accordingly.'
                : 'This decision has been recorded in the system for internal tracking.'}
            </Text>

            <View style={styles.statusPill}>
              <Text style={styles.statusPillText}>
                {approved ? 'Published to Map' : 'Archived'}
              </Text>
            </View>
          </View>

          <View style={styles.sectionCard}>
            <Text style={styles.sectionHeader}>Review Statistics</Text>
            <Text style={styles.statsText}>
              You have reviewed {reviewStats.reviewedToday} report
              {reviewStats.reviewedToday === 1 ? '' : 's'} today.
            </Text>
            <Text style={styles.statsText}>
              There are {reviewStats.waitingForReview} more report
              {reviewStats.waitingForReview === 1 ? '' : 's'} waiting for review.
            </Text>
          </View>
        </ScrollView>

        <View style={styles.bottomActionBar}>
          <TouchableOpacity
            style={styles.primaryButton}
            onPress={handleReturnToPendingReports}
          >
            <Ionicons name="arrow-back" size={18} color="#FFFFFF" />
            <Text style={styles.primaryButtonText}>Return to Pending Reports</Text>
          </TouchableOpacity>

          <TouchableOpacity style={styles.secondaryButton} onPress={handleViewNextReport}>
            <Text style={styles.secondaryButtonText}>View Next Report</Text>
            <Ionicons name="chevron-forward" size={18} color="#111827" />
          </TouchableOpacity>
        </View>
      </View>
    );
  }

  if (screenMode === 'details') return renderDetailsPage();
  if (screenMode === 'decision') return renderDecisionPage();
  if (screenMode === 'success-approved') return renderResultPage(true);
  if (screenMode === 'success-rejected') return renderResultPage(false);

  return renderPendingReportsList();
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: '#F8FAFC',
  },

  header: {
    backgroundColor: '#0F4C92',
    paddingTop: 58,
    paddingHorizontal: 18,
    paddingBottom: 18,
  },

  headerTitle: {
    color: '#FFFFFF',
    fontSize: 28,
    fontWeight: '700',
  },

  headerSubtitle: {
    marginTop: 6,
    color: '#D1E3F8',
    fontSize: 13,
  },

  backButton: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 16,
    gap: 4,
  },

  backButtonText: {
    color: '#FFFFFF',
    fontSize: 15,
    fontWeight: '500',
  },

  searchContainer: {
    margin: 16,
    paddingHorizontal: 14,
    height: 48,
    borderRadius: 14,
    backgroundColor: '#FFFFFF',
    borderWidth: 1,
    borderColor: '#E5E7EB',
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },

  searchInput: {
    flex: 1,
    fontSize: 15,
    color: '#111827',
  },

  listContent: {
    paddingHorizontal: 16,
    paddingBottom: 32,
  },

  reportCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: 18,
    padding: 16,
    marginBottom: 14,
    borderWidth: 1,
    borderColor: '#E5E7EB',
  },

  cardTopRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },

  tag: {
    paddingHorizontal: 10,
    paddingVertical: 5,
    borderRadius: 999,
  },

  tagText: {
    fontSize: 12,
    fontWeight: '600',
  },

  pendingTag: {
    backgroundColor: '#FEF3C7',
    paddingHorizontal: 10,
    paddingVertical: 5,
    borderRadius: 999,
  },

  pendingTagText: {
    color: '#D97706',
    fontSize: 12,
    fontWeight: '600',
  },

  reportTitle: {
    marginTop: 12,
    fontSize: 22,
    fontWeight: '700',
    color: '#111827',
  },

  reportDescriptionPreview: {
    marginTop: 8,
    fontSize: 14,
    color: '#6B7280',
    lineHeight: 20,
  },

  metaRow: {
    marginTop: 14,
    flexDirection: 'row',
    justifyContent: 'space-between',
    gap: 12,
  },

  metaItem: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
    flex: 1,
  },

  metaText: {
    flex: 1,
    fontSize: 13,
    color: '#6B7280',
  },

  cardFooter: {
    marginTop: 14,
    paddingTop: 12,
    borderTopWidth: 1,
    borderTopColor: '#F3F4F6',
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },

  reporterText: {
    fontSize: 12,
    color: '#9CA3AF',
  },

  closeButton: {
    position: 'absolute',
    right: 18,
    bottom: 26,
    width: 48,
    height: 48,
    borderRadius: 24,
    backgroundColor: '#0F4C92',
    justifyContent: 'center',
    alignItems: 'center',
  },

  centerState: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 24,
  },

  centerStateText: {
    marginTop: 12,
    fontSize: 15,
    color: '#6B7280',
    textAlign: 'center',
  },

  emptyCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: 18,
    padding: 28,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: '#E5E7EB',
    marginTop: 16,
  },

  emptyTitle: {
    marginTop: 12,
    fontSize: 18,
    fontWeight: '700',
    color: '#111827',
  },

  emptyText: {
    marginTop: 8,
    fontSize: 14,
    lineHeight: 20,
    color: '#6B7280',
    textAlign: 'center',
  },

  detailsContent: {
    padding: 16,
    paddingBottom: 24,
  },

  sectionCard: {
    backgroundColor: '#FFFFFF',
    borderRadius: 18,
    padding: 16,
    borderWidth: 1,
    borderColor: '#E5E7EB',
    marginBottom: 14,
  },

  detailsTitle: {
    marginTop: 12,
    fontSize: 24,
    fontWeight: '700',
    color: '#111827',
  },

  sectionLabel: {
    marginTop: 12,
    fontSize: 13,
    fontWeight: '600',
    color: '#6B7280',
  },

  detailsParagraph: {
    marginTop: 8,
    fontSize: 15,
    lineHeight: 22,
    color: '#1F2937',
  },

  divider: {
    marginTop: 16,
    marginBottom: 4,
    height: 1,
    backgroundColor: '#F3F4F6',
  },

  twoColumnRow: {
    flexDirection: 'row',
    gap: 16,
    marginTop: 8,
  },

  infoBlock: {
    flex: 1,
  },

  smallInfoText: {
    marginTop: 6,
    fontSize: 14,
    color: '#111827',
  },

  smallMutedText: {
    marginTop: 4,
    fontSize: 12,
    color: '#9CA3AF',
    lineHeight: 18,
  },

  verifiedText: {
    marginTop: 6,
    fontSize: 12,
    fontWeight: '600',
  },

  sectionHeader: {
    fontSize: 15,
    fontWeight: '700',
    color: '#374151',
    marginBottom: 8,
  },

  placeholderBox: {
    height: 180,
    borderRadius: 16,
    backgroundColor: '#F3F4F6',
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 20,
  },

  placeholderTitle: {
    marginTop: 12,
    fontSize: 16,
    fontWeight: '600',
    color: '#6B7280',
    textAlign: 'center',
  },

  placeholderSubtitle: {
    marginTop: 8,
    fontSize: 12,
    color: '#9CA3AF',
    textAlign: 'center',
    lineHeight: 18,
  },

  bottomActionBar: {
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 20,
    backgroundColor: '#F8FAFC',
  },

  primaryButton: {
    height: 52,
    borderRadius: 14,
    backgroundColor: '#0F4C92',
    justifyContent: 'center',
    alignItems: 'center',
    flexDirection: 'row',
    gap: 8,
  },

  primaryButtonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },

  metaVerticalItem: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    marginTop: 10,
  },

  metaVerticalText: {
    flex: 1,
    fontSize: 14,
    color: '#4B5563',
  },

  reporterTextBlock: {
    marginTop: 14,
    fontSize: 13,
    color: '#9CA3AF',
  },

  notesInput: {
    minHeight: 120,
    borderRadius: 14,
    backgroundColor: '#F9FAFB',
    borderWidth: 1,
    borderColor: '#E5E7EB',
    padding: 12,
    fontSize: 15,
    color: '#111827',
    textAlignVertical: 'top',
  },

  impactCard: {
    backgroundColor: '#EFF6FF',
    borderRadius: 18,
    padding: 16,
    borderWidth: 1,
    borderColor: '#DBEAFE',
    marginBottom: 14,
  },

  impactLine: {
    fontSize: 14,
    color: '#374151',
    lineHeight: 21,
    marginTop: 6,
  },

  approveButton: {
    height: 52,
    borderRadius: 14,
    backgroundColor: '#0D9488',
    justifyContent: 'center',
    alignItems: 'center',
    flexDirection: 'row',
    gap: 8,
    marginBottom: 12,
  },

  approveButtonText: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
  },

  rejectButton: {
    height: 52,
    borderRadius: 14,
    borderWidth: 1.5,
    borderColor: '#FCA5A5',
    backgroundColor: '#FFFFFF',
    justifyContent: 'center',
    alignItems: 'center',
    flexDirection: 'row',
    gap: 8,
  },

  rejectButtonText: {
    color: '#DC2626',
    fontSize: 16,
    fontWeight: '700',
  },

  disabledButton: {
    opacity: 0.65,
  },

  resultCard: {
    borderRadius: 20,
    padding: 24,
    alignItems: 'center',
    marginBottom: 14,
    borderWidth: 1,
  },

  resultApprovedCard: {
    backgroundColor: '#ECFDF5',
    borderColor: '#A7F3D0',
  },

  resultRejectedCard: {
    backgroundColor: '#FEF2F2',
    borderColor: '#FECACA',
  },

  resultIconCircle: {
    width: 76,
    height: 76,
    borderRadius: 38,
    justifyContent: 'center',
    alignItems: 'center',
  },

  resultIconApproved: {
    backgroundColor: '#14B8A6',
  },

  resultIconRejected: {
    backgroundColor: '#EF4444',
  },

  resultTitle: {
    marginTop: 18,
    fontSize: 22,
    fontWeight: '700',
    color: '#111827',
    textAlign: 'center',
  },

  resultText: {
    marginTop: 12,
    fontSize: 14,
    lineHeight: 21,
    color: '#4B5563',
    textAlign: 'center',
  },

  statusPill: {
    marginTop: 18,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 999,
    backgroundColor: '#FFFFFF',
    borderWidth: 1,
    borderColor: '#E5E7EB',
  },

  statusPillText: {
    fontSize: 13,
    fontWeight: '600',
    color: '#374151',
  },

  statsText: {
    fontSize: 14,
    color: '#4B5563',
    lineHeight: 22,
    marginTop: 4,
  },

  secondaryButton: {
    marginTop: 12,
    height: 52,
    borderRadius: 14,
    backgroundColor: '#FFFFFF',
    borderWidth: 1,
    borderColor: '#E5E7EB',
    justifyContent: 'center',
    alignItems: 'center',
    flexDirection: 'row',
    gap: 6,
  },

  secondaryButtonText: {
    color: '#111827',
    fontSize: 16,
    fontWeight: '600',
  },
});