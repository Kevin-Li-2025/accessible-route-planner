export type Coordinate = {
  latitude: number;
  longitude: number;
};

export type Hazard = {
  id: string | number;
  title: string;
  type: 'lighting' | 'wheelchair' | 'pothole' | 'construction' | 'obstruction' | string;
  latitude: number;
  longitude: number;
  description: string;
  status: 'Acknowledged' | 'Pending' | 'Reported' | 'UnderReview' | string;
  locationText: string;
  reportedTime: string;
};

export type ReportHazardType =
  | 'broken_street_light'
  | 'blocked_pavement'
  | 'parked_car_blocking_dropped_kerb'
  | 'road_obstruction'
  | 'unsafe_crossing'
  | 'other';

export type RouteFilters = {
  avoidSteepHills: boolean;
  wheelchairAccessible: boolean;
  avoidReportedHazards: boolean;
  preferWellLitStreets: boolean;
  minSafetyScore: number;
  maxSafetyScore: number;
};

export type ReportHazardOption = {
  key: ReportHazardType;
  label: string;
  iconType: 'ionicons' | 'material';
  iconName: string;
  iconColor: string;
  iconBg: string;
};
