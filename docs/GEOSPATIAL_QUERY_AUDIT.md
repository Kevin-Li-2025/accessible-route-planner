# Geospatial Query Audit

Date: 2026-05-22

## Hot Paths Reviewed

| Area | Query path | Risk before hardening | Mitigation |
| --- | --- | --- | --- |
| Hazard bbox | `RealHazardDataService.FetchDbHazardsInBBoxAsync` | Coordinate component filters could bypass PostGIS spatial indexes. | Query now uses `Location.Intersects(envelope)` so Npgsql translates to `ST_Intersects`; `IX_hazard_report_geom_gist` supports bbox lookup. |
| Hazard overlays | `SpatialController.GetMapOverlay` | Ordered latest hazards require a sort as table grows. | `IX_hazard_report_status_reported_at` and existing reported-at index cover active/latest hazard reads. |
| Vector tiles | `SpatialCacheService.GetHazardsInBoundsAsync` | DB fallback uses `ST_Intersects`; needed a GiST index on `hazard_report.geom`. | `IX_hazard_report_geom_gist`. |
| POI search | `SpatialController.GetPointsOfInterest` | `ST_DWithin(...::geography)` cannot use a plain geometry GiST index efficiently. | Added expression index `IX_infrastructure_assets_geometry_geog_gist` and geometry index `IX_infrastructure_assets_geometry_gist`. |
| Route graph load | `RouteGraphRepository.LoadGraphAsync` | `route_edges` bbox scan on imported OSM graph can grow quickly. | `IX_route_edges_geometry_gist` supports `ST_Intersects` graph loads. |
| Infrastructure risk | `RiskScoringService.EstimateInfrastructureRiskAsync` | EF `Distance` expression scanned and sorted nearby route edges. | Npgsql path now uses `ST_DWithin` plus GiST KNN ordering on `route_edges.Geometry`. |
| Nearest route node / route node spatial reads | route import/routing support | Future nearest-node reads need spatial support. | `IX_route_nodes_location_gist`. |

## Startup-Enforced Indexes

Indexes are created idempotently during API startup after migrations and schema normalization:

- `IX_hazard_report_geom_gist`
- `IX_hazard_report_status_reported_at`
- `IX_infrastructure_assets_geometry_gist`
- `IX_infrastructure_assets_geometry_geog_gist`
- `IX_infrastructure_assets_updated_at`
- `IX_route_edges_geometry_gist`
- `IX_route_nodes_location_gist`
- `IX_feed_ingestion_runs_source_status_started`
- `IX_hazard_report_reported_at_brin`
- `IX_feed_ingestion_runs_started_at_brin`

## Remaining Work

- Run `EXPLAIN (ANALYZE, BUFFERS)` against production-sized OSM extracts before launch.
- Consider generated geography columns if POI radius queries dominate load.
- Production runtime now supports `Postgres__StatementTimeoutMs`,
  `Postgres__IdleInTransactionSessionTimeoutMs`, and pool sizing knobs.
  Startup session parameters are opt-in via `Postgres__UseStartupSessionParameters=true`;
  leave it `false` for managed pooled databases that reject `-c` startup options and enforce
  those limits with database role or parameter-group settings instead.
- Keep OSM imports off API replicas; use the Kafka-backed worker profile for large extracts.
- For production-sized append-heavy tables, use `deploy/postgres/partitioning-readiness.sql` as the
  partitioning cutover template after a backup and load rehearsal.
