using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260524101500_AddInfrastructureAccessibilityProfile")]
    public partial class AddInfrastructureAccessibilityProfile : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE public.infrastructure_assets
                    ADD COLUMN IF NOT EXISTS "AccessibilityProfile" jsonb NOT NULL DEFAULT '{}'::jsonb;
                """);

            migrationBuilder.Sql(
                """
                UPDATE public.infrastructure_assets
                SET "AccessibilityProfile" = jsonb_build_object(
                    'schemaVersion', 'accessibility-profile.v1',
                    'sourceSystem', "SourceSystem",
                    'sourceRecordId', "SourceRecordId",
                    'profileGeneratedAtUtc', now(),
                    'lastVerifiedAtUtc', "LastObservedAt",
                    'verificationStatus', CASE
                        WHEN "LastObservedAt" IS NULL THEN 'unverified'
                        ELSE 'partial'
                    END,
                    'confidence', CASE
                        WHEN jsonb_typeof("AccessibilityInfo") = 'object' AND "AccessibilityInfo" <> '{}'::jsonb THEN 0.45
                        ELSE 0.10
                    END,
                    'path', jsonb_build_object(
                        'surface', "AccessibilityInfo" ->> 'surface',
                        'smoothness', "AccessibilityInfo" ->> 'smoothness',
                        'wheelchairAccess', "AccessibilityInfo" ->> 'wheelchair',
                        'lighting', "AccessibilityInfo" ->> 'lit',
                        'crossingType', COALESCE("AccessibilityInfo" ->> 'crossing', "AccessibilityInfo" ->> 'footway'),
                        'access', COALESCE("AccessibilityInfo" ->> 'access', "AccessibilityInfo" ->> 'foot')
                    ),
                    'entrances', '[]'::jsonb,
                    'restrooms', '[]'::jsonb,
                    'photos', CASE
                        WHEN "AccessibilityInfo" ? 'image'
                        THEN jsonb_build_array(jsonb_build_object(
                            'source', 'osm-image',
                            'url', "AccessibilityInfo" ->> 'image',
                            'verificationStatus', CASE WHEN "LastObservedAt" IS NULL THEN 'unverified' ELSE 'osm-tagged' END
                        ))
                        ELSE '[]'::jsonb
                    END,
                    'missingFields', '[]'::jsonb,
                    'evidenceTags', '{}'::jsonb,
                    'rawTagCount', 0
                )
                WHERE "AccessibilityProfile" = '{}'::jsonb;
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_infrastructure_assets_accessibility_profile_gin"
                    ON public.infrastructure_assets USING GIN ("AccessibilityProfile");
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_infrastructure_assets_last_observed_at"
                    ON public.infrastructure_assets ("LastObservedAt" DESC);
                """,
                suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS public."IX_infrastructure_assets_last_observed_at";""",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS public."IX_infrastructure_assets_accessibility_profile_gin";""",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE public.infrastructure_assets
                    DROP COLUMN IF EXISTS "AccessibilityProfile";
                """);
        }
    }
}
