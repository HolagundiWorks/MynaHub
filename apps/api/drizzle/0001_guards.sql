-- Guards that the schema builder cannot express. Hand-written, additive.

-- 1. The audit log is append-only (guidelines section 15.3): no UPDATE, DELETE or TRUNCATE, for any role.
CREATE FUNCTION "audit"."events_refuse_change"() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'audit.events is append-only' USING ERRCODE = '42501';
END;
$$;
--> statement-breakpoint
CREATE TRIGGER "events_no_update_delete" BEFORE UPDATE OR DELETE ON "audit"."events"
  FOR EACH ROW EXECUTE FUNCTION "audit"."events_refuse_change"();
--> statement-breakpoint
CREATE TRIGGER "events_no_truncate" BEFORE TRUNCATE ON "audit"."events"
  FOR EACH STATEMENT EXECUTE FUNCTION "audit"."events_refuse_change"();
--> statement-breakpoint

-- 2. Least privilege for the runtime role (guidelines section 7.5: separate credentials for runtime and for migrations).
-- The migration role is the owner and runs this file; the runtime role `myna_runtime` is created by the company (see docs) and is only
-- granted data access here, if it exists. It never owns objects, never gets DDL, and audit.events is insert and read only.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'myna_runtime') THEN
    GRANT USAGE ON SCHEMA "core", "access", "studio", "audit" TO "myna_runtime";
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "core", "access", "studio" TO "myna_runtime";
    GRANT SELECT, INSERT ON "audit"."events" TO "myna_runtime";
  END IF;
END
$$;
