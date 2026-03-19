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

CREATE INDEX IF NOT EXISTS idx_notification_user_created 
ON notification (user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS audit_log (
    id                      BIGSERIAL PRIMARY KEY,
    actor_user_id           UUID REFERENCES app_user(id) ON DELETE SET NULL,
    action                  TEXT NOT NULL,       -- e.g. 'created', 'updated', 'deleted'
    entity_type             TEXT NOT NULL,       -- e.g. 'hazard_report', 'maintenance_ticket'
    entity_id               TEXT NOT NULL,       -- the id of the affected entity
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_audit_log_entity 
ON audit_log (entity_type, entity_id);