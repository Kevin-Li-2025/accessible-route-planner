CREATE TABLE IF NOT EXISTS infrastructure_asset(
    id                  BIGSERIAL PRIMARY KEY,
    asset_type          TEXT NOT NULL,
    name                TEXT,
    geom                geometry(Geometry, 4326) NOT NULL,
    status              TEXT NOT NULL DEFAULT 'active',
    accessibility_info  JSONB NOT NULL DEFAULT '{}'::jsonb,
    source_system       TEXT NOT NULL,
    source_record_id    TEXT,
    last_observed_at    TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (source_system, source_record_id)
);

CREATE INDEX IF NOT EXISTS idx_infrastructure_asset_geom 
ON infrastructure_asset 
USING GIST (geom);

CREATE INDEX IF NOT EXISTS idx_infrastructure_asset_type 
ON infrastructure_asset (asset_type);

CREATE TABLE IF NOT EXISTS crime_incident (
    id                  BIGSERIAL PRIMARY KEY,
    source_system       TEXT NOT NULL,
    source_record_id    TEXT NOT NULL,
    category            TEXT,
    severity_weight     NUMERIC(5,2),
    occurred_at         TIMESTAMPTZ,
    reported_at         TIMESTAMPTZ,
    geom                geometry(Point, 4326) NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (source_system, source_record_id)
);

CREATE INDEX IF NOT EXISTS idx_crime_incident_geom 
ON crime_incident 
USING GIST (geom);

CREATE INDEX IF NOT EXISTS idx_crime_incident_time 
ON crime_incident (occurred_at);

CREATE TABLE IF NOT EXISTS feed_ingestion_run (
    id                  BIGSERIAL PRIMARY KEY,
    source_type         feed_source_type NOT NULL,
    source_name         TEXT NOT NULL,
    started_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    finished_at         TIMESTAMPTZ,
    status              TEXT NOT NULL,
    records_seen        INTEGER NOT NULL DEFAULT 0,
    records_inserted    INTEGER NOT NULL DEFAULT 0,
    records_updated     INTEGER NOT NULL DEFAULT 0,
    records_failed      INTEGER NOT NULL DEFAULT 0,
    error_summary       TEXT,
    metadata            JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS risk_grid_cell (
    id                  BIGSERIAL PRIMARY KEY,
    cell_geom           geometry(Polygon, 4326) NOT NULL,
    risk_score          NUMERIC(5,2) NOT NULL,
    crime_component     NUMERIC(5,2) NOT NULL DEFAULT 0,
    lighting_component  NUMERIC(5,2) NOT NULL DEFAULT 0,
    hazard_component    NUMERIC(5,2) NOT NULL DEFAULT 0,
    accessibility_component NUMERIC(5,2) NOT NULL DEFAULT 0,
    computed_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    model_version       TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_risk_grid_cell_geom 
ON risk_grid_cell 
USING GIST (cell_geom);