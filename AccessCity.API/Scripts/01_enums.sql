DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'user_role') THEN
        CREATE TYPE user_role AS ENUM (
            'citizen',
            'council_worker',
            'admin'
        );
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'mobility_profile') THEN
        CREATE TYPE mobility_profile AS ENUM (
            'standard_pedestrian',
            'manual_wheelchair',
            'power_wheelchair',
            'low_vision',
            'other'
        );
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'hazard_type') THEN
        CREATE TYPE hazard_type AS ENUM (
            'broken_light',
            'poor_lighting',
            'blocked_kerb',
            'broken_lift',
            'stairs_no_ramp',
            'rough_surface',
            'steep_gradient',
            'flooding',
            'roadworks',
            'road_closure',
            'unsafe_area',
            'obstruction',
            'other'
        );
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'hazard_status') THEN
        CREATE TYPE hazard_status AS ENUM (
            'reported',
            'under_review',
            'verified',
            'action_planned',
            'in_progress',
            'resolved',
            'rejected',
            'duplicate'
        );
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'feed_source_type') THEN
        CREATE TYPE feed_source_type AS ENUM (
            'crime_api',
            'lighting_dataset',
            'osm',
            'lift_status',
            'flood_warning',
            'roadworks_feed',
            'manual_upload'
        );
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'ticket_status') THEN
        CREATE TYPE ticket_status AS ENUM (
            'open',
            'triaged',
            'assigned',
            'scheduled',
            'resolved',
            'closed'
        );
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'alert_severity') THEN
        CREATE TYPE alert_severity AS ENUM (
            'info',
            'warning',
            'severe',
            'critical'
        );
    END IF;
END $$;