using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    public partial class AddScalabilityGuardIndexes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_hazard_report_active_geom_gist"
                    ON public.hazard_report USING GIST (geom)
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status);

                CREATE INDEX IF NOT EXISTS "IX_hazard_report_active_geom_geog_gist"
                    ON public.hazard_report USING GIST ((geom::geography))
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status);

                CREATE INDEX IF NOT EXISTS "IX_hazard_report_active_reported_at"
                    ON public.hazard_report (reported_at DESC)
                    WHERE status IN ('reported'::hazard_status, 'under_review'::hazard_status);
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS public."IX_hazard_report_active_reported_at";
                DROP INDEX IF EXISTS public."IX_hazard_report_active_geom_geog_gist";
                DROP INDEX IF EXISTS public."IX_hazard_report_active_geom_gist";
                """);
        }
    }
}
