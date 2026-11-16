CREATE TABLE "studio"."project_assignments" (
	"organization_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"membership_id" uuid NOT NULL,
	CONSTRAINT "project_assignments_project_id_membership_id_pk" PRIMARY KEY("project_id","membership_id")
);
--> statement-breakpoint
ALTER TABLE "access"."roles" ADD COLUMN "scope" text DEFAULT 'assigned' NOT NULL;--> statement-breakpoint
ALTER TABLE "studio"."project_assignments" ADD CONSTRAINT "project_assignments_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."project_assignments" ADD CONSTRAINT "project_assignments_project_id_projects_id_fk" FOREIGN KEY ("project_id") REFERENCES "studio"."projects"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."project_assignments" ADD CONSTRAINT "project_assignments_membership_id_memberships_id_fk" FOREIGN KEY ("membership_id") REFERENCES "core"."memberships"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "project_assignments_member_idx" ON "studio"."project_assignments" USING btree ("membership_id");--> statement-breakpoint
CREATE UNIQUE INDEX "roles_org_name_unique" ON "access"."roles" USING btree ("organization_id","name");--> statement-breakpoint
ALTER TABLE "access"."roles" ADD CONSTRAINT "roles_scope_check" CHECK ("access"."roles"."scope" in ('organization', 'assigned'));