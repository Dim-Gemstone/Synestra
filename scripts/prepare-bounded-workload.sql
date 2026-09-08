-- Explicit local development/test preparation, after MigrationWorker completes.
-- This fixed UUID v7 identifies only the built-in development definition.
INSERT INTO job_definitions
    (id, type, display_name, description, is_enabled, created_at_utc, updated_at_utc)
VALUES
    ('019926cf-0800-7000-8000-000000000001', 'test.bounded-sum.v1',
     'Bounded sum test workload', NULL, TRUE, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON CONFLICT (type) DO NOTHING;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM job_definitions WHERE type = 'test.bounded-sum.v1' AND is_enabled
    ) THEN
        RAISE EXCEPTION 'Existing test.bounded-sum.v1 definition is disabled; preparation will not change it.';
    END IF;
END $$;
