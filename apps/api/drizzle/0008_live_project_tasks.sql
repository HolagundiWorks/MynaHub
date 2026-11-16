CREATE TABLE "studio"."project_tasks" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"title" text NOT NULL,
	"description" text DEFAULT '' NOT NULL,
	"due_at" timestamp with time zone,
	"status" text DEFAULT 'open' NOT NULL,
	"priority" text DEFAULT 'normal' NOT NULL,
	"version" integer DEFAULT 1 NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	"deleted_at" timestamp with time zone,
	CONSTRAINT "project_tasks_title_check" CHECK (length(btrim("studio"."project_tasks"."title")) between 1 and 240),
	CONSTRAINT "project_tasks_description_check" CHECK (length("studio"."project_tasks"."description") <= 10000),
	CONSTRAINT "project_tasks_status_check" CHECK ("studio"."project_tasks"."status" in ('open','in_progress','completed','cancelled')),
	CONSTRAINT "project_tasks_priority_check" CHECK ("studio"."project_tasks"."priority" in ('low','normal','high','urgent')),
	CONSTRAINT "project_tasks_version_check" CHECK ("studio"."project_tasks"."version" >= 1)
);
--> statement-breakpoint
CREATE UNIQUE INDEX "projects_org_id_unique" ON "studio"."projects" USING btree ("organization_id","id");
--> statement-breakpoint
ALTER TABLE "studio"."project_tasks" ADD CONSTRAINT "project_tasks_organization_id_project_id_projects_organization_id_id_fk" FOREIGN KEY ("organization_id","project_id") REFERENCES "studio"."projects"("organization_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "project_tasks_project_idx" ON "studio"."project_tasks" USING btree ("organization_id","project_id","id");--> statement-breakpoint
ALTER TABLE studio.project_tasks ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY task_read ON studio.project_tasks FOR SELECT USING (
  organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL
  AND studio.project_allowed(project_id,'projects.read'));
--> statement-breakpoint
DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
    REVOKE ALL ON studio.project_tasks FROM myna_runtime;
    GRANT SELECT ON studio.project_tasks TO myna_runtime;
  END IF;
END $$;
