using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertHazardTypeToText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'hazard_type'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            ALTER COLUMN hazard_type DROP DEFAULT;

                        ALTER TABLE public.hazard_report
                            ALTER COLUMN hazard_type TYPE text
                            USING hazard_type::text;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
