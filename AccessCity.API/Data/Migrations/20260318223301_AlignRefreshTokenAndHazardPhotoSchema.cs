using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AccessCity.API.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlignRefreshTokenAndHazardPhotoSchema : Migration
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
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_reports'
                    ) AND EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                    ) THEN
                        IF EXISTS (SELECT 1 FROM public.hazard_reports LIMIT 1) THEN
                            RAISE EXCEPTION 'Cannot auto-align hazard tables because both public.hazard_reports and public.hazard_report contain schema state. Resolve manually before rerunning migration.';
                        END IF;

                        DROP TABLE public.hazard_reports CASCADE;
                    ELSIF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_reports'
                    ) THEN
                        ALTER TABLE public.hazard_reports RENAME TO hazard_report;
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                    ) THEN
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
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'PK_hazard_reports'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'PK_hazard_report'
                        ) THEN
                            ALTER TABLE public.hazard_report
                                RENAME CONSTRAINT "PK_hazard_reports" TO "PK_hazard_report";
                        END IF;

                        IF EXISTS (
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
                        ELSIF EXISTS (
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
                    ) AND EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                    ) THEN
                        IF EXISTS (SELECT 1 FROM public.refresh_tokens LIMIT 1) THEN
                            RAISE EXCEPTION 'Cannot auto-align refresh token tables because both public.refresh_tokens and public.refresh_token contain data. Resolve manually before rerunning migration.';
                        END IF;

                        DROP TABLE public.refresh_tokens CASCADE;
                    ELSIF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_tokens'
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

                        CREATE INDEX "IX_refresh_token_user_id"
                            ON public.refresh_token (user_id);
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                    ) THEN
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
                              AND column_name = 'CreatedByIp'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'created_by_ip'
                        ) THEN
                            ALTER TABLE public.refresh_token RENAME COLUMN "CreatedByIp" TO created_by_ip;
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
                              AND column_name = 'Created'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'created_at'
                        ) THEN
                            ALTER TABLE public.refresh_token RENAME COLUMN "Created" TO created_at;
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

                        IF EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'RevokedByIp'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'revoked_by_ip'
                        ) THEN
                            ALTER TABLE public.refresh_token RENAME COLUMN "RevokedByIp" TO revoked_by_ip;
                        END IF;

                        IF EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'ReplacedByToken'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'replaced_by_token'
                        ) THEN
                            ALTER TABLE public.refresh_token RENAME COLUMN "ReplacedByToken" TO replaced_by_token;
                        END IF;

                        IF EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'ReasonRevoked'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'reason_revoked'
                        ) THEN
                            ALTER TABLE public.refresh_token RENAME COLUMN "ReasonRevoked" TO reason_revoked;
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

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'token'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN token character varying(400) NOT NULL DEFAULT '';

                            ALTER TABLE public.refresh_token
                                ALTER COLUMN token DROP DEFAULT;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'created_by_ip'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN created_by_ip text NOT NULL DEFAULT '';

                            ALTER TABLE public.refresh_token
                                ALTER COLUMN created_by_ip DROP DEFAULT;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'expires_at'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN expires_at timestamp with time zone NOT NULL DEFAULT now();

                            ALTER TABLE public.refresh_token
                                ALTER COLUMN expires_at DROP DEFAULT;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'created_at'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN created_at timestamp with time zone NOT NULL DEFAULT now();

                            ALTER TABLE public.refresh_token
                                ALTER COLUMN created_at DROP DEFAULT;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'revoked'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN revoked timestamp with time zone NULL;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'revoked_by_ip'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN revoked_by_ip text NULL;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'replaced_by_token'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN replaced_by_token text NULL;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'reason_revoked'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN reason_revoked text NULL;
                        END IF;

                        IF NOT EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'user_id'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD COLUMN user_id text NULL;
                        END IF;

                        IF EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'PK_refresh_tokens'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'PK_refresh_token'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                RENAME CONSTRAINT "PK_refresh_tokens" TO "PK_refresh_token";
                        END IF;

                        IF EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'FK_refresh_tokens_AspNetUsers_UserId'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'FK_refresh_token_AspNetUsers_user_id'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                RENAME CONSTRAINT "FK_refresh_tokens_AspNetUsers_UserId"
                                TO "FK_refresh_token_AspNetUsers_user_id";
                        ELSIF NOT EXISTS (
                            SELECT 1
                            FROM pg_constraint
                            WHERE connamespace = 'public'::regnamespace
                              AND conname = 'FK_refresh_token_AspNetUsers_user_id'
                        ) AND EXISTS (
                            SELECT 1
                            FROM information_schema.columns
                            WHERE table_schema = 'public'
                              AND table_name = 'refresh_token'
                              AND column_name = 'user_id'
                        ) AND EXISTS (
                            SELECT 1
                            FROM information_schema.tables
                            WHERE table_schema = 'public'
                              AND table_name = 'AspNetUsers'
                        ) THEN
                            ALTER TABLE public.refresh_token
                                ADD CONSTRAINT "FK_refresh_token_AspNetUsers_user_id"
                                FOREIGN KEY (user_id)
                                REFERENCES public."AspNetUsers" ("Id")
                                ON DELETE CASCADE;
                        END IF;

                        IF EXISTS (
                            SELECT 1
                            FROM pg_class index_class
                            JOIN pg_namespace index_namespace
                              ON index_namespace.oid = index_class.relnamespace
                            WHERE index_namespace.nspname = 'public'
                              AND index_class.relkind = 'i'
                              AND index_class.relname = 'IX_refresh_tokens_UserId'
                        ) AND NOT EXISTS (
                            SELECT 1
                            FROM pg_class index_class
                            JOIN pg_namespace index_namespace
                              ON index_namespace.oid = index_class.relnamespace
                            WHERE index_namespace.nspname = 'public'
                              AND index_class.relkind = 'i'
                              AND index_class.relname = 'IX_refresh_token_user_id'
                        ) THEN
                            ALTER INDEX public."IX_refresh_tokens_UserId"
                                RENAME TO "IX_refresh_token_user_id";
                        ELSIF NOT EXISTS (
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
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                    ) AND EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'photo_url'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                          AND column_name = 'PhotoUrl'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME COLUMN photo_url TO "PhotoUrl";
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
                          AND index_class.relname = 'IX_hazard_reports_ReportedAt'
                    ) THEN
                        ALTER INDEX public."IX_hazard_report_ReportedAt"
                            RENAME TO "IX_hazard_reports_ReportedAt";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE connamespace = 'public'::regnamespace
                          AND conname = 'PK_hazard_report'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE connamespace = 'public'::regnamespace
                          AND conname = 'PK_hazard_reports'
                    ) THEN
                        ALTER TABLE public.hazard_report
                            RENAME CONSTRAINT "PK_hazard_report" TO "PK_hazard_reports";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_report'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'hazard_reports'
                    ) THEN
                        ALTER TABLE public.hazard_report RENAME TO hazard_reports;
                    END IF;
                END $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_refresh_token_user_id'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_class index_class
                        JOIN pg_namespace index_namespace
                          ON index_namespace.oid = index_class.relnamespace
                        WHERE index_namespace.nspname = 'public'
                          AND index_class.relkind = 'i'
                          AND index_class.relname = 'IX_refresh_tokens_UserId'
                    ) THEN
                        ALTER INDEX public."IX_refresh_token_user_id"
                            RENAME TO "IX_refresh_tokens_UserId";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE connamespace = 'public'::regnamespace
                          AND conname = 'FK_refresh_token_AspNetUsers_user_id'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE connamespace = 'public'::regnamespace
                          AND conname = 'FK_refresh_tokens_AspNetUsers_UserId'
                    ) THEN
                        ALTER TABLE public.refresh_token
                            RENAME CONSTRAINT "FK_refresh_token_AspNetUsers_user_id"
                            TO "FK_refresh_tokens_AspNetUsers_UserId";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE connamespace = 'public'::regnamespace
                          AND conname = 'PK_refresh_token'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_constraint
                        WHERE connamespace = 'public'::regnamespace
                          AND conname = 'PK_refresh_tokens'
                    ) THEN
                        ALTER TABLE public.refresh_token
                            RENAME CONSTRAINT "PK_refresh_token" TO "PK_refresh_tokens";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'token'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Token'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN token TO "Token";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'created_by_ip'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'CreatedByIp'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN created_by_ip TO "CreatedByIp";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'expires_at'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Expires'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN expires_at TO "Expires";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'created_at'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Created'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN created_at TO "Created";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'revoked'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'Revoked'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN revoked TO "Revoked";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'revoked_by_ip'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'RevokedByIp'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN revoked_by_ip TO "RevokedByIp";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'replaced_by_token'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'ReplacedByToken'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN replaced_by_token TO "ReplacedByToken";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'reason_revoked'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'ReasonRevoked'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN reason_revoked TO "ReasonRevoked";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'user_id'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                          AND column_name = 'UserId'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME COLUMN user_id TO "UserId";
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_token'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.tables
                        WHERE table_schema = 'public'
                          AND table_name = 'refresh_tokens'
                    ) THEN
                        ALTER TABLE public.refresh_token RENAME TO refresh_tokens;
                    END IF;
                END $$;
                """);
        }
    }
}
