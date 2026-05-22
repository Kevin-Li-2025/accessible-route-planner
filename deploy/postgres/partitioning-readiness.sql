-- Production cutover helper for append-heavy AccessCity tables.
-- Run only during a planned migration window after taking a backup.
-- EF migrations keep the current non-partitioned tables for local/test simplicity;
-- this script documents the operational partitioning target for production datasets.

BEGIN;

CREATE TABLE IF NOT EXISTS public.hazard_report_partitioned
(
    LIKE public.hazard_report INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE
) PARTITION BY RANGE (reported_at);

CREATE TABLE IF NOT EXISTS public.hazard_report_y2026m01
    PARTITION OF public.hazard_report_partitioned
    FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');

CREATE TABLE IF NOT EXISTS public.hazard_report_y2026m02
    PARTITION OF public.hazard_report_partitioned
    FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');

CREATE INDEX IF NOT EXISTS "IX_hazard_report_partitioned_geom_gist"
    ON public.hazard_report_partitioned USING GIST (geom);

CREATE INDEX IF NOT EXISTS "IX_hazard_report_partitioned_status_reported"
    ON public.hazard_report_partitioned (status, reported_at DESC);

CREATE INDEX IF NOT EXISTS "IX_hazard_report_partitioned_reported_brin"
    ON public.hazard_report_partitioned USING BRIN (reported_at);

CREATE TABLE IF NOT EXISTS public.feed_ingestion_runs_partitioned
(
    LIKE public.feed_ingestion_runs INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING STORAGE
) PARTITION BY RANGE ("StartedAt");

CREATE TABLE IF NOT EXISTS public.feed_ingestion_runs_y2026m01
    PARTITION OF public.feed_ingestion_runs_partitioned
    FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');

CREATE INDEX IF NOT EXISTS "IX_feed_ingestion_runs_partitioned_source_status_started"
    ON public.feed_ingestion_runs_partitioned ("SourceType", "Status", "StartedAt" DESC);

CREATE INDEX IF NOT EXISTS "IX_feed_ingestion_runs_partitioned_started_brin"
    ON public.feed_ingestion_runs_partitioned USING BRIN ("StartedAt");

ROLLBACK;

-- Cutover outline:
-- 1. Replace ROLLBACK with COMMIT after expanding the monthly partitions needed for retained data.
-- 2. INSERT INTO *_partitioned SELECT * FROM existing table in batches.
-- 3. In the same maintenance transaction, rename existing table to *_legacy and partitioned table to the live name.
-- 4. Recreate grants, foreign keys, and EF migration history checks as needed.
