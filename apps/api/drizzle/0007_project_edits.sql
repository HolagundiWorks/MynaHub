CREATE TABLE "studio"."project_edit_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "project_edit_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id")
);
--> statement-breakpoint
ALTER TABLE "studio"."project_edit_receipts" ADD CONSTRAINT "project_edit_receipts_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."project_edit_receipts" ADD CONSTRAINT "project_edit_receipts_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "studio"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type)
VALUES ('projects.write','Edit project details within the role scope','projects') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT id,'projects.write' FROM access.roles WHERE is_system_role AND name IN ('Owner','Administrator','Principal','Project Manager')
ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE POLICY project_edit ON studio.projects FOR UPDATE
USING (studio.project_allowed(id,'projects.read') AND studio.project_allowed(id,'projects.write'))
WITH CHECK (organization_id::text=current_setting('app.organization_id',true)
  AND studio.project_allowed(id,'projects.read') AND studio.project_allowed(id,'projects.write'));
--> statement-breakpoint
ALTER TABLE studio.project_edit_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY receipt_read ON studio.project_edit_receipts FOR SELECT
USING (organization_id::text=current_setting('app.organization_id',true)
  AND user_id=current_setting('app.user_id',true) AND studio.project_allowed(project_id,'projects.write')
  AND studio.project_allowed(project_id,'projects.read'));
--> statement-breakpoint
CREATE POLICY receipt_insert ON studio.project_edit_receipts FOR INSERT
WITH CHECK (organization_id::text=current_setting('app.organization_id',true)
  AND user_id=current_setting('app.user_id',true) AND studio.project_allowed(project_id,'projects.write')
  AND studio.project_allowed(project_id,'projects.read'));
--> statement-breakpoint
CREATE FUNCTION studio.edit_receipt_immutable() RETURNS trigger LANGUAGE plpgsql SET search_path = '' AS $$
BEGIN RAISE EXCEPTION 'Project edit receipts are immutable'; END;
$$;
--> statement-breakpoint
CREATE TRIGGER project_edit_receipt_immutable BEFORE UPDATE OR DELETE ON studio.project_edit_receipts
FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER project_edit_receipt_no_truncate BEFORE TRUNCATE ON studio.project_edit_receipts
FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
    REVOKE UPDATE ON studio.projects FROM myna_runtime;
    GRANT UPDATE(name,code,status,version,updated_at) ON studio.projects TO myna_runtime;
    REVOKE ALL ON studio.project_edit_receipts FROM myna_runtime;
    GRANT SELECT,INSERT ON studio.project_edit_receipts TO myna_runtime;
  END IF;
END $$;
