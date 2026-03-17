CREATE TABLE hazard_cluster (
    id                      BIGSERIAL PRIMARY KEY,     
    hazard_type             hazard_type NOT NULL,  
    canonical_geom          geometry(Point, 4326) NOT NULL,
    canonical_description   TEXT,  
    current_status          hazard_status NOT NULL DEFAULT 'reported',
    reliability_score       NUMERIC(5,2) NOT NULL DEFAULT 0,
    severity_score          NUMERIC(5,2) NOT NULL DEFAULT 0,
    first_reported_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_reported_at        TIMESTAMPTZ NOT NULL DEFAULT now(),  
    verified_at             TIMESTAMPTZ, 
    resolved_at             TIMESTAMPTZ,
    created_by_feed         BOOLEAN NOT NULL DEFAULT FALSE 
);

CREATE INDEX idx_hazard_cluster_geom 
ON hazard_cluster 
USING GIST (canonical_geom);

CREATE INDEX idx_hazard_cluster_status 
ON hazard_cluster (current_status);

CREATE TABLE hazard_report (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    reporter_user_id        UUID REFERENCES app_user(id) ON DELETE SET NULL,
    hazard_cluster_id       BIGINT REFERENCES hazard_cluster(id) ON DELETE SET NULL,
    hazard_type             hazard_type NOT NULL,   
    geom                    geometry(Point, 4326) NOT NULL,
    description             TEXT,
    reported_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    duplicate_of_report_id  UUID REFERENCES hazard_report(id) ON DELETE SET NULL, --Other reports can be linked
    source                  TEXT NOT NULL DEFAULT 'user',
    status                  hazard_status NOT NULL DEFAULT 'reported',                   
);

CREATE INDEX idx_hazard_report_geom 
ON hazard_report USING GIST (geom);

CREATE INDEX idx_hazard_report_user 
ON hazard_report (reporter_user_id);

CREATE INDEX idx_hazard_report_cluster 
ON hazard_report (hazard_cluster_id);
