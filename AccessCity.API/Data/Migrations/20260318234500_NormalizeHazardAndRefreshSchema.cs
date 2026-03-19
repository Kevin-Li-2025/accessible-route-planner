using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeHazardAndRefreshSchema : Migration
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
                          AND column_name = 'Id'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'id'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "Id" TO id;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'Location'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'geom'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "Location" TO geom;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'Type'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'hazard_type'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "Type" TO hazard_type;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'Description'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'description'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "Description" TO description;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'PhotoUrl'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'photo_url'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "PhotoUrl" TO photo_url;
                    ELSIF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'photo_url'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            ADD COLUMN photo_url text NOT NULL DEFAULT '';

                        ALTER TABLE public.hazard_report
                            ALTER COLUMN photo_url DROP DEFAULT;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'ReportedAt'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'reported_at'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "ReportedAt" TO reported_at;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'Status'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'status'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "Status" TO status;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'Source'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'source'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "Source" TO source;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'ReporterUserId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'reporter_user_id'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN "ReporterUserId" TO reporter_user_id;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'hazard_type'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            ALTER COLUMN hazard_type TYPE text
                            USING hazard_type::text;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_type
                        WHERE typname = 'hazard_status'
                    ) THEN
                        CREATE TYPE hazard_status AS ENUM (
                            'reported',
                            'under_review',
                            'verified',
                            'action_planned',
                            'in_progress',
                            'resolved',
                            'rejected',
                            'duplicate'
                        );
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'status'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            ALTER COLUMN status TYPE hazard_status
                            USING CASE lower(status::text)
                                WHEN 'reported' THEN 'reported'::hazard_status
                                WHEN 'under_review' THEN 'under_review'::hazard_status
                                WHEN 'underreview' THEN 'under_review'::hazard_status
                                WHEN 'verified' THEN 'verified'::hazard_status
                                WHEN 'action_planned' THEN 'action_planned'::hazard_status
                                WHEN 'actionplanned' THEN 'action_planned'::hazard_status
                                WHEN 'in_progress' THEN 'in_progress'::hazard_status
                                WHEN 'inprogress' THEN 'in_progress'::hazard_status
                                WHEN 'resolved' THEN 'resolved'::hazard_status
                                WHEN 'dismissed' THEN 'rejected'::hazard_status
                                WHEN 'rejected' THEN 'rejected'::hazard_status
                                WHEN 'duplicate' THEN 'duplicate'::hazard_status
                                ELSE 'reported'::hazard_status
                            END;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'reporter_user_id'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            ALTER COLUMN reporter_user_id TYPE uuid
                            USING NULLIF(reporter_user_id::text, '')::uuid;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'photo_url'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            ALTER COLUMN photo_url TYPE text;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_hazard_report_ReportedAt'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_hazard_report_reported_at'
                    ) THEN
                        ALTER INDEX public."IX_hazard_report_ReportedAt"
                            RENAME TO "IX_hazard_report_reported_at";
                    ELSIF EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_hazard_reports_ReportedAt'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_hazard_report_reported_at'
                    ) THEN
                        ALTER INDEX public."IX_hazard_reports_ReportedAt"
                            RENAME TO "IX_hazard_report_reported_at";
                    ELSIF NOT EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_hazard_report_reported_at'
                    ) AND EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'reported_at'
                    ) THEN
                        CREATE INDEX "IX_hazard_report_reported_at"
                            ON public.hazard_report (reported_at);
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_tokens'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                    ) THEN
                        ALTER TABLE public.refresh_tokens RENAME TO refresh_token;
                    ELSIF NOT EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                    ) THEN
                        CREATE TABLE public.refresh_token
                        (
                            "Id" integer GENERATED BY DEFAULT AS IDENTITY,
                            token character varying(400) NOT NULL,
                            created_by_ip text NOT NULL DEFAULT '',
                            expires_at timestamp with time zone NOT NULL,
                            created_at timestamp with time zone NOT NULL DEFAULT now(),
                            revoked timestamp with time zone NULL,
                            revoked_by_ip text NULL,
                            replaced_by_token text NULL,
                            reason_revoked text NULL,
                            user_id text NOT NULL,
                            CONSTRAINT "PK_refresh_token" PRIMARY KEY ("Id"),
                            CONSTRAINT "FK_refresh_token_AspNetUsers_user_id"
                                FOREIGN KEY (user_id)
                                REFERENCES public."AspNetUsers" ("Id")
                                ON DELETE CASCADE
                        );
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Token'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'token'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN "Token" TO token;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'UserId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'user_id'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN "UserId" TO user_id;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Expires'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'expires_at'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN "Expires" TO expires_at;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Revoked'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'revoked'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN "Revoked" TO revoked;
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_refresh_token_user_id'
                    ) AND EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'user_id'
                    ) THEN
                        CREATE INDEX "IX_refresh_token_user_id"
                            ON public.refresh_token (user_id);
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
