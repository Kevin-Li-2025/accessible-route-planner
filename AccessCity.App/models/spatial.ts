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

export type RouteFilters = {
  avoidSteepHills: boolean;
  wheelchairAccessible: boolean;
  avoidReportedHazards: boolean;
  preferWellLitStreets: boolean;
  minSafetyScore: number;
  maxSafetyScore: number;
};

export type RouteResponse = {
  routeCoordinates: Coordinate[];
  travelTime: string;
  distance: string;
  safetyScore: string;
};
