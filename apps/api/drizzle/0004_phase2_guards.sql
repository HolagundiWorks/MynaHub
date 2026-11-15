-- Phase 2 guards and reference data that the schema builder cannot express. Hand-written, additive.

-- 1. An invitation's role must exist (the schema file cannot reference access.roles without a circular import).
ALTER TABLE "core"."invitations" ADD CONSTRAINT "invitations_role_fk" FOREIGN KEY ("role_id") REFERENCES "access"."roles"("id");
--> statement-breakpoint
ALTER TABLE "core"."invitations" ADD CONSTRAINT "invitations_expiry_check" CHECK ("expires_at" > "created_at");
--> statement-breakpoint
ALTER TABLE "core"."bootstrap_tokens" ADD CONSTRAINT "bootstrap_tokens_expiry_check" CHECK ("expires_at" > "created_at");
--> statement-breakpoint

-- 2. Least privilege for the runtime role (applies only if the company created it first; see the README).
-- Existing tables get explicit grants; ALTER DEFAULT PRIVILEGES covers every later table the same owner creates, so a new migration does not
-- need to remember. audit.events stays insert and read only (migration 0001), so it is not part of the defaults.
DO $$
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'myna_runtime') THEN
    GRANT USAGE ON SCHEMA "auth" TO "myna_runtime";
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "core", "access", "studio", "auth" TO "myna_runtime";
    ALTER DEFAULT PRIVILEGES IN SCHEMA "core", "access", "studio", "auth" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "myna_runtime";
  END IF;
END
$$;
--> statement-breakpoint

-- 3. The first permission keys (the full catalogue is Phase 3). Reference data, global to the deployment.
INSERT INTO "access"."permissions" ("key", "description", "resource_type") VALUES
  ('organization.read', 'See the organisation and who is in it', 'organization'),
  ('organization.members.manage', 'Invite, suspend and reactivate members', 'organization'),
  ('audit.read', 'Read the audit log', 'audit')
ON CONFLICT ("key") DO NOTHING;
