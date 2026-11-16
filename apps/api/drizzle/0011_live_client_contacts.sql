CREATE TABLE "studio"."clients" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"name" text NOT NULL,
	"kind" text DEFAULT 'individual' NOT NULL,
	"contact_person" text DEFAULT '' NOT NULL,
	"email" text DEFAULT '' NOT NULL,
	"phone" text DEFAULT '' NOT NULL,
	"address" text DEFAULT '' NOT NULL,
	"correspondence_address" text DEFAULT '' NOT NULL,
	"preferred_communication" text DEFAULT '' NOT NULL,
	"notes" text DEFAULT '' NOT NULL,
	"version" integer DEFAULT 1 NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	"deleted_at" timestamp with time zone,
	CONSTRAINT "clients_name_check" CHECK (length(btrim("studio"."clients"."name")) between 1 and 240),
	CONSTRAINT "clients_kind_check" CHECK ("studio"."clients"."kind" in ('individual','company','architect_firm')),
	CONSTRAINT "clients_fields_check" CHECK (length("studio"."clients"."contact_person")<=240 AND length("studio"."clients"."email")<=254 AND length("studio"."clients"."phone")<=80
    AND length("studio"."clients"."address")<=2000 AND length("studio"."clients"."correspondence_address")<=2000
    AND length("studio"."clients"."preferred_communication")<=120 AND length("studio"."clients"."notes")<=4000),
	CONSTRAINT "clients_version_check" CHECK ("studio"."clients"."version">=1)
);
--> statement-breakpoint
ALTER TABLE "studio"."projects" ADD COLUMN "client_id" uuid;--> statement-breakpoint
ALTER TABLE "studio"."clients" ADD CONSTRAINT "clients_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE UNIQUE INDEX "clients_org_id_unique" ON "studio"."clients" USING btree ("organization_id","id");--> statement-breakpoint
CREATE INDEX "clients_org_idx" ON "studio"."clients" USING btree ("organization_id","id");--> statement-breakpoint
ALTER TABLE "studio"."projects" ADD CONSTRAINT "projects_organization_id_client_id_clients_organization_id_id_fk" FOREIGN KEY ("organization_id","client_id") REFERENCES "studio"."clients"("organization_id","id") ON DELETE no action ON UPDATE no action;
--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type)
VALUES('clients.read','Read client contacts within company or assigned-project scope','clients') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT r.id,'clients.read' FROM access.roles r JOIN access.role_permissions rp ON rp.role_id=r.id
WHERE r.is_system_role AND rp.permission_key='projects.read' ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE FUNCTION studio.client_allowed(p_client uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = '' AS $$
  SELECT EXISTS(
    SELECT 1 FROM studio.clients c JOIN core.memberships m ON m.organization_id=c.organization_id
    JOIN access.membership_roles mr ON mr.membership_id=m.id
    JOIN access.roles r ON r.id=mr.role_id AND r.organization_id=m.organization_id
    JOIN access.role_permissions rp ON rp.role_id=r.id AND rp.permission_key='clients.read'
    WHERE c.id=p_client AND c.deleted_at IS NULL AND m.status='active'
      AND m.id::text=current_setting('app.membership_id',true) AND m.auth_user_id=current_setting('app.user_id',true)
      AND m.organization_id::text=current_setting('app.organization_id',true)
      AND (r.scope='organization' OR EXISTS(
        SELECT 1 FROM studio.projects p JOIN studio.project_assignments a ON a.project_id=p.id
          AND a.organization_id=p.organization_id AND a.membership_id=m.id
        WHERE p.client_id=c.id AND p.organization_id=c.organization_id AND p.deleted_at IS NULL
          AND studio.project_allowed(p.id,'projects.read'))));
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.client_allowed(uuid) FROM PUBLIC;
--> statement-breakpoint
ALTER TABLE studio.clients ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY client_read ON studio.clients FOR SELECT USING (
  organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL AND studio.client_allowed(id));
--> statement-breakpoint
DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
    REVOKE ALL ON studio.clients FROM myna_runtime;
    GRANT SELECT ON studio.clients TO myna_runtime;
    GRANT EXECUTE ON FUNCTION studio.client_allowed(uuid) TO myna_runtime;
  END IF;
END $$;
