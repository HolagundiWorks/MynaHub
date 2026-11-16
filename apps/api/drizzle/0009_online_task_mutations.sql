CREATE TABLE "studio"."task_mutation_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"task_id" uuid NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "task_mutation_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id")
);
--> statement-breakpoint
CREATE UNIQUE INDEX "project_tasks_org_project_id_unique" ON "studio"."project_tasks" USING btree ("organization_id","project_id","id");
--> statement-breakpoint
ALTER TABLE "studio"."task_mutation_receipts" ADD CONSTRAINT "task_mutation_receipts_organization_id_project_id_task_id_project_tasks_organization_id_project_id_id_fk" FOREIGN KEY ("organization_id","project_id","task_id") REFERENCES "studio"."project_tasks"("organization_id","project_id","id") ON DELETE no action ON UPDATE no action;
--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type)
VALUES('tasks.write','Create and edit tasks within the project scope','tasks') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT id,'tasks.write' FROM access.roles WHERE is_system_role AND name IN ('Owner','Administrator','Principal','Project Manager') ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE POLICY task_insert ON studio.project_tasks FOR INSERT WITH CHECK (
  organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL AND version=1
  AND studio.project_allowed(project_id,'projects.read') AND studio.project_allowed(project_id,'tasks.write'));
--> statement-breakpoint
CREATE POLICY task_update ON studio.project_tasks FOR UPDATE
USING (deleted_at IS NULL AND studio.project_allowed(project_id,'projects.read') AND studio.project_allowed(project_id,'tasks.write'))
WITH CHECK (organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL
  AND studio.project_allowed(project_id,'projects.read') AND studio.project_allowed(project_id,'tasks.write'));
--> statement-breakpoint
ALTER TABLE studio.task_mutation_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY task_receipt_read ON studio.task_mutation_receipts FOR SELECT USING (
  organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true)
  AND studio.project_allowed(project_id,'projects.read') AND studio.project_allowed(project_id,'tasks.write'));
--> statement-breakpoint
CREATE POLICY task_receipt_insert ON studio.task_mutation_receipts FOR INSERT WITH CHECK (
  organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true)
  AND studio.project_allowed(project_id,'projects.read') AND studio.project_allowed(project_id,'tasks.write'));
--> statement-breakpoint
CREATE TRIGGER task_receipt_immutable BEFORE UPDATE OR DELETE ON studio.task_mutation_receipts
FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER task_receipt_no_truncate BEFORE TRUNCATE ON studio.task_mutation_receipts
FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
    GRANT INSERT ON studio.project_tasks TO myna_runtime;
    GRANT UPDATE(title,description,due_at,status,priority,version,updated_at) ON studio.project_tasks TO myna_runtime;
    REVOKE ALL ON studio.task_mutation_receipts FROM myna_runtime;
    GRANT SELECT,INSERT ON studio.task_mutation_receipts TO myna_runtime;
  END IF;
END $$;
