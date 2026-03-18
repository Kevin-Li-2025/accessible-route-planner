import { Hazard, ReportHazardOption, ReportHazardType } from './MapTypes';

export const hazards: Hazard[] = [
  {
    id: 1,
    title: 'Broken street light',
    type: 'lighting',
    latitude: 52.4865,
    longitude: -1.891,
    description: 'There is a broken street light. The street is dimly-lit.',
    status: 'Acknowledged',
    locationText: 'Hazard located in Birmingham',
    reportedTime: '2 minutes ago',
  },
  {
    id: 2,
    title: 'No wheelchair ramp',
    type: 'wheelchair',
    latitude: 52.4852,
    longitude: -1.888,
    description: 'Wheelchair users may find it difficult to access this path safely.',
    status: 'Pending',
    locationText: 'Hazard located near city centre',
    reportedTime: '10 minutes ago',
  },
];

export const reportHazardOptions: ReportHazardOption[] = [
  {
    key: 'broken_street_light',
    label: 'Broken street light',
    iconType: 'ionicons',
    iconName: 'bulb-outline',
    iconColor: '#EAB308',
    iconBg: '#FEF3C7',
  },
  {
    key: 'blocked_pavement',
    label: 'Blocked pavement',
    iconType: 'ionicons',
    iconName: 'warning-outline',
    iconColor: '#F97316',
    iconBg: '#FEE2E2',
  },
  {
    key: 'parked_car_blocking_dropped_kerb',
    label: 'Parked car blocking dropped kerb',
    iconType: 'ionicons',
    iconName: 'car-outline',
    iconColor: '#2563EB',
    iconBg: '#DBEAFE',
  },
  {
    key: 'road_obstruction',
    label: 'Road obstruction',
    iconType: 'ionicons',
    iconName: 'warning-outline',
    iconColor: '#EF4444',
    iconBg: '#FCE7F3',
  },
  {
    key: 'unsafe_crossing',
    label: 'Unsafe crossing',
    iconType: 'material',
    iconName: 'walk',
    iconColor: '#14B8A6',
    iconBg: '#DCFCE7',
  },
  {
    key: 'other',
    label: 'Other',
    iconType: 'ionicons',
    iconName: 'document-text-outline',
    iconColor: '#4B5563',
    iconBg: '#E5E7EB',
  },
];

export const reportHazardLabelMap: Record<ReportHazardType, string> = {
  broken_street_light: 'Broken street light',
  blocked_pavement: 'Blocked pavement',
  parked_car_blocking_dropped_kerb: 'Parked car blocking dropped kerb',
  road_obstruction: 'Road obstruction',
  unsafe_crossing: 'Unsafe crossing',
  other: 'Other',
};
