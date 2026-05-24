using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20260524123000_AddAccessibilityVerificationSubmissions")]
    public partial class AddAccessibilityVerificationSubmissions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS public.accessibility_verification_submissions
                (
                    "Id" uuid NOT NULL,
                    "InfrastructureAssetId" bigint NOT NULL,
                    "SubmittedByUserId" character varying(450) NOT NULL,
                    "Source" character varying(100) NOT NULL,
                    "Status" character varying(50) NOT NULL,
                    "SubmittedAtUtc" timestamp with time zone NOT NULL,
                    "ObservedAtUtc" timestamp with time zone NULL,
                    "ReviewedAtUtc" timestamp with time zone NULL,
                    "ReviewedByUserId" character varying(450) NULL,
                    "AppliedAtUtc" timestamp with time zone NULL,
                    "Notes" text NULL,
                    "Confidence" double precision NOT NULL,
                    "AttributeUpdates" jsonb NOT NULL DEFAULT '{}'::jsonb,
                    "PhotoUrls" jsonb NOT NULL DEFAULT '[]'::jsonb,
                    CONSTRAINT "PK_accessibility_verification_submissions" PRIMARY KEY ("Id"),
                    CONSTRAINT "FK_accessibility_verification_submissions_infrastructure_assets_InfrastructureAssetId"
                        FOREIGN KEY ("InfrastructureAssetId")
                        REFERENCES public.infrastructure_assets ("Id")
                        ON DELETE CASCADE
                );
                """);

            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_accessibility_verifications_asset_status_submitted"
                    ON public.accessibility_verification_submissions ("InfrastructureAssetId", "Status", "SubmittedAtUtc" DESC);
                """,
                suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS public."IX_accessibility_verifications_asset_status_submitted";""",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS public.accessibility_verification_submissions;
                """);
        }
    }
}
