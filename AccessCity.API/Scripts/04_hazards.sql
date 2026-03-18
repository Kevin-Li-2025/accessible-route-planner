CREATE TABLE IF NOT EXISTS hazard_cluster (
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

CREATE INDEX IF NOT EXISTS idx_hazard_cluster_geom 
ON hazard_cluster 
USING GIST (canonical_geom);

CREATE INDEX IF NOT EXISTS idx_hazard_cluster_status 
ON hazard_cluster (current_status);

CREATE TABLE IF NOT EXISTS hazard_report (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    reporter_user_id        UUID REFERENCES app_user(id) ON DELETE SET NULL,
    hazard_cluster_id       BIGINT REFERENCES hazard_cluster(id) ON DELETE SET NULL,
    hazard_type             hazard_type NOT NULL,   
    geom                    geometry(Point, 4326) NOT NULL,
    description             TEXT,
    reported_at             TIMESTAMPTZ NOT NULL DEFAULT now(),
    duplicate_of_report_id  UUID REFERENCES hazard_report(id) ON DELETE SET NULL, --Other reports can be linked
    source                  TEXT NOT NULL DEFAULT 'user',
    status                  hazard_status NOT NULL DEFAULT 'reported'                
);

CREATE INDEX IF NOT EXISTS idx_hazard_report_geom 
ON hazard_report USING GIST (geom);

CREATE INDEX IF NOT EXISTS idx_hazard_report_user 
ON hazard_report (reporter_user_id);

CREATE INDEX IF NOT EXISTS idx_hazard_report_cluster 
ON hazard_report (hazard_cluster_id);

CREATE TABLE IF NOT EXISTS hazard_status_history (
    id                      BIGSERIAL PRIMARY KEY,
    hazard_cluster_id       BIGINT NOT NULL REFERENCES hazard_cluster(id) ON DELETE CASCADE,
    old_status              hazard_status,
    new_status              hazard_status,
    changed_by_user_id      UUID REFERENCES app_user(id) ON DELETE SET NULL,
    changed_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_hazard_status_history_cluster
ON hazard_status_history (hazard_cluster_id, changed_at DESC);

CREATE TABLE IF NOT EXISTS hazard_watch ( --which users are watching what hazards essentially
    user_id                 UUID NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    hazard_cluster_id       BIGINT NOT NULL REFERENCES hazard_cluster(id) ON DELETE CASCADE,
    watching_since          TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, hazard_cluster_id) --composite key to link
);


