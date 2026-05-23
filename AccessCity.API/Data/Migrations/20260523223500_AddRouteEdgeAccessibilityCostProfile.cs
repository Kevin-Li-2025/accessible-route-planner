using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260523223500_AddRouteEdgeAccessibilityCostProfile")]
    public partial class AddRouteEdgeAccessibilityCostProfile : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE public.route_edges
                    ADD COLUMN IF NOT EXISTS kerb_height double precision NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS smoothness character varying(50),
                    ADD COLUMN IF NOT EXISTS width_metres double precision,
                    ADD COLUMN IF NOT EXISTS has_tactile_paving boolean NOT NULL DEFAULT false,
                    ADD COLUMN IF NOT EXISTS "HasBarrier" boolean NOT NULL DEFAULT false,
                    ADD COLUMN IF NOT EXISTS "Access" text,
                    ADD COLUMN IF NOT EXISTS accessibility_cost_version integer NOT NULL DEFAULT 1,
                    ADD COLUMN IF NOT EXISTS standard_accessibility_penalty_seconds double precision NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS wheelchair_accessibility_penalty_seconds double precision NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS stroller_accessibility_penalty_seconds double precision NOT NULL DEFAULT 0,
                    ADD COLUMN IF NOT EXISTS accessibility_data_quality double precision NOT NULL DEFAULT 1;
                """);

            migrationBuilder.Sql(
                """
                WITH edge_profile AS (
                    SELECT
                        "Id",
                        GREATEST("DistanceMetres", 0) AS distance_metres,
                        lower(COALESCE(NULLIF("SurfaceType", ''), 'unknown')) AS surface,
                        lower(COALESCE(NULLIF(smoothness, ''), '')) AS smoothness_value,
                        COALESCE("HasStairs", false) AS has_stairs,
                        COALESCE("HasBarrier", false) AS has_barrier,
                        COALESCE(kerb_height, 0) AS kerb_height,
                        width_metres,
                        COALESCE("IsSteep", false) AS is_steep,
                        lower(COALESCE("Access", '')) AS access_value
                    FROM public.route_edges
                ),
                computed AS (
                    SELECT
                        "Id",
                        LEAST(2400, GREATEST(0,
                            CASE WHEN has_stairs THEN 90 ELSE 0 END
                            + CASE WHEN has_barrier THEN 60 ELSE 0 END
                            + CASE WHEN kerb_height > 0.03 THEN 30 ELSE 0 END
                            + CASE WHEN access_value LIKE '%access=no%' OR access_value LIKE '%access=private%' OR access_value LIKE '%foot=no%' OR access_value LIKE '%wheelchair=no%' THEN 90 ELSE 0 END
                            + CASE
                                WHEN surface = 'unknown' THEN distance_metres / 1.3 * 0.15
                                WHEN surface IN ('cobblestone', 'sett') THEN distance_metres / 1.3 * 0.4
                                WHEN surface IN ('gravel', 'unpaved', 'sand', 'dirt', 'earth', 'grass') THEN distance_metres / 1.3 * 0.8
                                ELSE 0
                              END
                            + CASE WHEN smoothness_value IN ('bad', 'very_bad', 'horrible', 'very_horrible', 'impassable') THEN 45 ELSE 0 END
                            + CASE WHEN width_metres IS NOT NULL AND width_metres < 0.9 THEN 30 ELSE 0 END
                            + CASE WHEN is_steep THEN distance_metres / 1.3 * 0.5 ELSE 0 END
                        )) AS standard_penalty,
                        LEAST(2400, GREATEST(0,
                            CASE WHEN has_stairs THEN 600 ELSE 0 END
                            + CASE WHEN has_barrier THEN 600 ELSE 0 END
                            + CASE WHEN kerb_height > 0.03 THEN 300 ELSE 0 END
                            + CASE WHEN access_value LIKE '%access=no%' OR access_value LIKE '%access=private%' OR access_value LIKE '%foot=no%' OR access_value LIKE '%wheelchair=no%' THEN 600 ELSE 0 END
                            + CASE
                                WHEN surface = 'unknown' THEN distance_metres / 0.9 * 0.6
                                WHEN surface IN ('cobblestone', 'sett') THEN distance_metres / 0.9 * 2.0
                                WHEN surface IN ('gravel', 'unpaved', 'sand', 'dirt', 'earth', 'grass') THEN distance_metres / 0.9 * 4.0
                                ELSE 0
                              END
                            + CASE
                                WHEN smoothness_value IN ('bad', 'very_bad', 'horrible', 'very_horrible', 'impassable') THEN 300
                                WHEN smoothness_value = '' THEN distance_metres / 0.9 * 0.25
                                ELSE 0
                              END
                            + CASE
                                WHEN width_metres IS NOT NULL AND width_metres < 0.9 THEN 300
                                WHEN width_metres IS NULL THEN distance_metres / 0.9 * 0.35
                                ELSE 0
                              END
                            + CASE WHEN is_steep THEN distance_metres / 0.9 * 1.5 ELSE 0 END
                        )) AS wheelchair_penalty,
                        LEAST(2400, GREATEST(0,
                            CASE WHEN has_stairs THEN 600 ELSE 0 END
                            + CASE WHEN has_barrier THEN 600 ELSE 0 END
                            + CASE WHEN kerb_height > 0.03 THEN 300 ELSE 0 END
                            + CASE WHEN access_value LIKE '%access=no%' OR access_value LIKE '%access=private%' OR access_value LIKE '%foot=no%' OR access_value LIKE '%wheelchair=no%' THEN 600 ELSE 0 END
                            + CASE
                                WHEN surface = 'unknown' THEN distance_metres / 1.1 * 0.6
                                WHEN surface IN ('cobblestone', 'sett') THEN distance_metres / 1.1 * 2.0
                                WHEN surface IN ('gravel', 'unpaved', 'sand', 'dirt', 'earth', 'grass') THEN distance_metres / 1.1 * 4.0
                                ELSE 0
                              END
                            + CASE
                                WHEN smoothness_value IN ('bad', 'very_bad', 'horrible', 'very_horrible', 'impassable') THEN 300
                                WHEN smoothness_value = '' THEN distance_metres / 1.1 * 0.25
                                ELSE 0
                              END
                            + CASE
                                WHEN width_metres IS NOT NULL AND width_metres < 0.9 THEN 300
                                WHEN width_metres IS NULL THEN distance_metres / 1.1 * 0.35
                                ELSE 0
                              END
                            + CASE WHEN is_steep THEN distance_metres / 1.1 * 1.5 ELSE 0 END
                        )) AS stroller_penalty,
                        GREATEST(0.10, 1.0
                            - CASE WHEN surface = 'unknown' THEN 0.25 ELSE 0 END
                            - CASE WHEN smoothness_value = '' THEN 0.20 ELSE 0 END
                            - CASE WHEN width_metres IS NULL THEN 0.25 ELSE 0 END
                        ) AS data_quality
                    FROM edge_profile
                )
                UPDATE public.route_edges AS edge
                SET
                    accessibility_cost_version = 1,
                    standard_accessibility_penalty_seconds = computed.standard_penalty,
                    wheelchair_accessibility_penalty_seconds = computed.wheelchair_penalty,
                    stroller_accessibility_penalty_seconds = computed.stroller_penalty,
                    accessibility_data_quality = computed.data_quality
                FROM computed
                WHERE edge."Id" = computed."Id";
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_route_edges_accessibility_cost_version"
                    ON public.route_edges (accessibility_cost_version);
                """,
                suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS public."IX_route_edges_accessibility_cost_version";""",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE public.route_edges
                    DROP COLUMN IF EXISTS accessibility_data_quality,
                    DROP COLUMN IF EXISTS stroller_accessibility_penalty_seconds,
                    DROP COLUMN IF EXISTS wheelchair_accessibility_penalty_seconds,
                    DROP COLUMN IF EXISTS standard_accessibility_penalty_seconds,
                    DROP COLUMN IF EXISTS accessibility_cost_version;
                """);
        }
    }
}
