CREATE TABLE app_user (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    email               TEXT UNIQUE,
    password_hash       TEXT,
    display_name        TEXT NOT NULL,
    role                user_role NOT NULL DEFAULT 'citizen',
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE user_accessibility_profile (
    user_id                     UUID PRIMARY KEY REFERENCES app_user(id) ON DELETE CASCADE,
    mobility_profile            mobility_profile NOT NULL DEFAULT 'standard_pedestrian',
    prefers_step_free           BOOLEAN NOT NULL DEFAULT FALSE,
    avoids_steep_gradients      BOOLEAN NOT NULL DEFAULT FALSE,
    avoids_rough_surfaces       BOOLEAN NOT NULL DEFAULT FALSE,
    prefers_well_lit_routes     BOOLEAN NOT NULL DEFAULT TRUE,
    prefers_cctv_coverage       BOOLEAN NOT NULL DEFAULT FALSE,
    needs_high_contrast         BOOLEAN NOT NULL DEFAULT FALSE,
    larger_touch_targets        BOOLEAN NOT NULL DEFAULT FALSE,
    text_scale                  NUMERIC(3,2) NOT NULL DEFAULT 1.00,
    voice_guidance_enabled      BOOLEAN NOT NULL DEFAULT FALSE,
    haptic_guidance_enabled     BOOLEAN NOT NULL DEFAULT FALSE,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT chk_text_scale CHECK (text_scale BETWEEN 0.80 AND 3.00)
);