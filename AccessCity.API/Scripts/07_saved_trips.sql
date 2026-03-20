CREATE TABLE IF NOT EXISTS saved_trips(
    id                          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id                     UUID NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    name                        TEXT NOT NULL,
    icon                        TEXT, -- How the safe trip can be recongised
    origin_geom                 geometry(Point, 4326) NOT NULL,
    destination_geom            geometry(Point, 4326) NOT NULL,
    start_address               TEXT,
    end_address                 TEXT,

    preferred_safety_weight     NUMERIC(4,3) NOT NULL DEFAULT 0.5,
    avoid_stairs                BOOLEAN NOT NULL DEFAULT FALSE,
    avoid_steep_gradients       BOOLEAN NOT NULL DEFAULT FALSE,
    avoid_rough_surfaces        BOOLEAN NOT NULL DEFAULT FALSE,
    prioritize_lighting         BOOLEAN NOT NULL DEFAULT TRUE,
    prioritize_cctv             BOOLEAN NOT NULL DEFAULT FALSE, --These are all saftey weightings and deciders for that

    enable_alerts               BOOLEAN NOT NULL DEFAULT FALSE, --for notifications

    created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT chk_safety_weight CHECK (preferred_safety_weight BETWEEN 0.0 AND 1.0)
);

CREATE INDEX IF NOT EXISTS idx_saved_trip_user 
ON saved_trip (user_id);

CREATE INDEX IF NOT EXISTS idx_saved_trip_origin 
ON saved_trip USING GIST (origin_geom);

CREATE INDEX IF NOT EXISTS idx_saved_trip_destination 
ON saved_trip USING GIST (destination_geom);