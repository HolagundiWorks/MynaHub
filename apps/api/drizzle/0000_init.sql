CREATE SCHEMA "core";
--> statement-breakpoint
CREATE SCHEMA "access";
--> statement-breakpoint
CREATE SCHEMA "studio";
--> statement-breakpoint
CREATE SCHEMA "audit";
--> statement-breakpoint
CREATE TABLE "core"."memberships" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"auth_user_id" text NOT NULL,
	"status" text DEFAULT 'invited' NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "memberships_org_user_unique" UNIQUE("organization_id","auth_user_id"),
	CONSTRAINT "memberships_status_check" CHECK ("core"."memberships"."status" in ('invited', 'active', 'suspended'))
);
--> statement-breakpoint
CREATE TABLE "core"."organizations" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"name" text NOT NULL,
	"slug" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "organizations_slug_unique" UNIQUE("slug")
);
--> statement-breakpoint
CREATE TABLE "access"."membership_roles" (
	"membership_id" uuid NOT NULL,
	"role_id" uuid NOT NULL,
	CONSTRAINT "membership_roles_membership_id_role_id_pk" PRIMARY KEY("membership_id","role_id")
);
--> statement-breakpoint
CREATE TABLE "access"."permissions" (
	"key" text PRIMARY KEY NOT NULL,
	"description" text DEFAULT '' NOT NULL,
	"resource_type" text NOT NULL
);
--> statement-breakpoint
CREATE TABLE "access"."role_permissions" (
	"role_id" uuid NOT NULL,
	"permission_key" text NOT NULL,
	CONSTRAINT "role_permissions_role_id_permission_key_pk" PRIMARY KEY("role_id","permission_key")
);
--> statement-breakpoint
CREATE TABLE "access"."roles" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"name" text NOT NULL,
	"description" text DEFAULT '' NOT NULL,
	"is_system_role" boolean DEFAULT false NOT NULL
);
--> statement-breakpoint
CREATE TABLE "studio"."projects" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"name" text NOT NULL,
	"code" text DEFAULT '' NOT NULL,
	"status" text DEFAULT 'active' NOT NULL,
	"created_by" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	"version" integer DEFAULT 1 NOT NULL,
	"deleted_at" timestamp with time zone,
	CONSTRAINT "projects_status_check" CHECK ("studio"."projects"."status" in ('active', 'on_hold', 'completed', 'archived')),
	CONSTRAINT "projects_version_check" CHECK ("studio"."projects"."version" >= 1)
);
--> statement-breakpoint
CREATE TABLE "audit"."events" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid,
	"occurred_at" timestamp with time zone DEFAULT now() NOT NULL,
	"actor_auth_user_id" text,
	"action" text NOT NULL,
	"resource_type" text DEFAULT '' NOT NULL,
	"resource_id" text DEFAULT '' NOT NULL,
	"request_id" text DEFAULT '' NOT NULL,
	"detail" jsonb DEFAULT '{}'::jsonb NOT NULL
);
--> statement-breakpoint
ALTER TABLE "core"."memberships" ADD CONSTRAINT "memberships_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "access"."membership_roles" ADD CONSTRAINT "membership_roles_membership_id_memberships_id_fk" FOREIGN KEY ("membership_id") REFERENCES "core"."memberships"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "access"."membership_roles" ADD CONSTRAINT "membership_roles_role_id_roles_id_fk" FOREIGN KEY ("role_id") REFERENCES "access"."roles"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "access"."role_permissions" ADD CONSTRAINT "role_permissions_role_id_roles_id_fk" FOREIGN KEY ("role_id") REFERENCES "access"."roles"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "access"."role_permissions" ADD CONSTRAINT "role_permissions_permission_key_permissions_key_fk" FOREIGN KEY ("permission_key") REFERENCES "access"."permissions"("key") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "access"."roles" ADD CONSTRAINT "roles_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."projects" ADD CONSTRAINT "projects_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "memberships_user_idx" ON "core"."memberships" USING btree ("auth_user_id");--> statement-breakpoint
CREATE INDEX "roles_org_name_idx" ON "access"."roles" USING btree ("organization_id","name");--> statement-breakpoint
CREATE INDEX "projects_org_idx" ON "studio"."projects" USING btree ("organization_id");--> statement-breakpoint
CREATE UNIQUE INDEX "projects_org_code_unique" ON "studio"."projects" USING btree ("organization_id","code") WHERE "studio"."projects"."code" <> '';--> statement-breakpoint
CREATE INDEX "audit_events_org_time_idx" ON "audit"."events" USING btree ("organization_id","occurred_at");