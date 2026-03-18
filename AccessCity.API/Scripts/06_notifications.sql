CREATE TABLE IF NOT EXISTS notification (
    id                      UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id                 UUID NOT NULL REFERENCES app_user(id) ON DELETE CASCADE,
    notification_type       TEXT NOT NULL, -- hazard_status_changed, alert_broadcast, etc.
    title                   TEXT NOT NULL,
    body                    TEXT NOT NULL,
    related_entity_type     TEXT,          -- e.g. 'hazard_cluster', 'emergency_alert'
    related_entity_id       TEXT,          -- the id of the related entity
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    read_at                 TIMESTAMPTZ   
);