using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    public partial class AddWorkerJobAndMessageReliability : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "osm_import_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    file_path = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    city_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    queued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    feed_ingestion_run_id = table.Column<long>(type: "bigint", nullable: true),
                    error_summary = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_osm_import_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_integration_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    topic = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    consumer_group_id = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    event_type = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    processed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_integration_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_osm_import_jobs_status_queued",
                table: "osm_import_jobs",
                columns: new[] { "status", "queued_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_processed_integration_messages_processed_at",
                table: "processed_integration_messages",
                column: "processed_at_utc");

            migrationBuilder.CreateIndex(
                name: "UX_processed_integration_messages_identity",
                table: "processed_integration_messages",
                columns: new[] { "message_id", "consumer_group_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_hazard_report_reported_at_brin"
                    ON public.hazard_report USING BRIN (reported_at);

                CREATE INDEX IF NOT EXISTS "IX_feed_ingestion_runs_started_at_brin"
                    ON public.feed_ingestion_runs USING BRIN ("StartedAt");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "processed_integration_messages");
            migrationBuilder.DropTable(name: "osm_import_jobs");

            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS public."IX_hazard_report_reported_at_brin";
                DROP INDEX IF EXISTS public."IX_feed_ingestion_runs_started_at_brin";
                """);
        }
    }
}
