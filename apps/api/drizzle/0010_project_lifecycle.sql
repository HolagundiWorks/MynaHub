CREATE TABLE "studio"."project_lifecycle_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"action" text NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "project_lifecycle_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id"),
	CONSTRAINT "project_lifecycle_action_check" CHECK ("studio"."project_lifecycle_receipts"."action" in ('create','delete'))
);
--> statement-breakpoint
ALTER TABLE "studio"."project_lifecycle_receipts" ADD CONSTRAINT "project_lifecycle_receipts_organization_id_project_id_projects_organization_id_id_fk" FOREIGN KEY ("organization_id","project_id") REFERENCES "studio"."projects"("organization_id","id") ON DELETE no action ON UPDATE no action;
--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type) VALUES
('projects.create','Create company projects with organization scope','projects'),
('projects.delete','Remove projects from live company views within the role scope','projects')
ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT r.id,p.key FROM access.roles r CROSS JOIN (VALUES('projects.create'),('projects.delete')) p(key)
WHERE r.is_system_role AND (r.name IN ('Owner','Administrator') OR (r.name='Principal' AND p.key='projects.create'))
ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE FUNCTION studio.organization_permission(p_permission text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = '' AS $$
  SELECT EXISTS(
    SELECT 1 FROM core.memberships m
    JOIN access.membership_roles mr ON mr.membership_id=m.id
    JOIN access.roles r ON r.id=mr.role_id AND r.organization_id=m.organization_id AND r.scope='organization'
    JOIN access.role_permissions rp ON rp.role_id=r.id AND rp.permission_key=p_permission
    WHERE m.id::text=current_setting('app.membership_id',true) AND m.status='active'
      AND m.auth_user_id=current_setting('app.user_id',true)
      AND m.organization_id::text=current_setting('app.organization_id',true));
$$;
--> statement-breakpoint
-- Deliberately includes tombstones only to authorize deletion receipts. Normal project reads remain unchanged.
CREATE FUNCTION studio.project_delete_allowed(p_project uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = '' AS $$
  SELECT EXISTS(
    SELECT 1 FROM studio.projects p JOIN core.memberships m ON m.organization_id=p.organization_id
    WHERE p.id=p_project AND m.id::text=current_setting('app.membership_id',true) AND m.status='active'
      AND m.auth_user_id=current_setting('app.user_id',true)
      AND m.organization_id::text=current_setting('app.organization_id',true)
      AND NOT EXISTS(
        SELECT 1 FROM (VALUES('projects.read'),('projects.delete')) required(key)
        WHERE NOT EXISTS(
          SELECT 1 FROM access.membership_roles mr
          JOIN access.roles r ON r.id=mr.role_id AND r.organization_id=m.organization_id
          JOIN access.role_permissions rp ON rp.role_id=r.id AND rp.permission_key=required.key
          WHERE mr.membership_id=m.id AND (r.scope='organization' OR EXISTS(
            SELECT 1 FROM studio.project_assignments a WHERE a.organization_id=p.organization_id
              AND a.project_id=p.id AND a.membership_id=m.id)))));
$$;
--> statement-breakpoint
CREATE FUNCTION studio.create_company_project(p_id uuid,p_name text,p_code text,p_status text)
RETURNS TABLE(id uuid,name text,code text,status text,version integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
BEGIN
  IF NOT studio.organization_permission('projects.create') OR NOT studio.organization_permission('projects.read') THEN RETURN; END IF;
  IF p_name IS NULL OR length(btrim(p_name)) NOT BETWEEN 1 AND 240
    OR p_code IS NULL OR length(p_code)>80 OR p_status IS NULL OR p_status NOT IN ('active','on_hold','completed','archived') THEN
    RAISE EXCEPTION 'Invalid project fields' USING ERRCODE='22023';
  END IF;
  RETURN QUERY INSERT INTO studio.projects AS p(id,organization_id,name,code,status,created_by)
    VALUES(p_id,current_setting('app.organization_id')::uuid,btrim(p_name),btrim(p_code),p_status,current_setting('app.user_id'))
    ON CONFLICT ON CONSTRAINT projects_pkey DO NOTHING RETURNING p.id,p.name,p.code,p.status,p.version;
END;
$$;
--> statement-breakpoint
CREATE FUNCTION studio.delete_company_project(p_id uuid,p_expected integer)
RETURNS TABLE(outcome text,id uuid,name text,code text,status text,version integer,deleted_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = '' AS $$
DECLARE current_project studio.projects%ROWTYPE;
BEGIN
  IF NOT studio.project_delete_allowed(p_id) THEN RETURN; END IF;
  IF p_expected IS NULL OR p_expected<1 OR p_expected>2147483646 THEN RAISE EXCEPTION 'Invalid revision' USING ERRCODE='22023'; END IF;
  SELECT p.* INTO current_project FROM studio.projects p WHERE p.id=p_id
    AND p.organization_id::text=current_setting('app.organization_id',true) FOR UPDATE;
  IF NOT FOUND OR current_project.deleted_at IS NOT NULL THEN RETURN; END IF;
  IF current_project.version<>p_expected THEN
    RETURN QUERY SELECT 'conflict'::text,current_project.id,current_project.name,current_project.code,
      current_project.status,current_project.version,NULL::timestamptz;
    RETURN;
  END IF;
  UPDATE studio.projects p SET deleted_at=clock_timestamp(),updated_at=clock_timestamp(),version=p.version+1
    WHERE p.id=p_id RETURNING p.* INTO current_project;
  RETURN QUERY SELECT 'deleted'::text,current_project.id,current_project.name,current_project.code,
    current_project.status,current_project.version,current_project.deleted_at;
END;
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.organization_permission(text),studio.project_delete_allowed(uuid),
  studio.create_company_project(uuid,text,text,text),studio.delete_company_project(uuid,integer) FROM PUBLIC;
--> statement-breakpoint
ALTER TABLE studio.project_lifecycle_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY project_lifecycle_receipt_read ON studio.project_lifecycle_receipts FOR SELECT USING (
  organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true)
  AND ((action='create' AND studio.organization_permission('projects.create') AND studio.organization_permission('projects.read')
      AND studio.project_allowed(project_id,'projects.read')) OR (action='delete' AND studio.project_delete_allowed(project_id))));
--> statement-breakpoint
CREATE POLICY project_lifecycle_receipt_insert ON studio.project_lifecycle_receipts FOR INSERT WITH CHECK (
  organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true)
  AND ((action='create' AND studio.organization_permission('projects.create') AND studio.organization_permission('projects.read')
      AND studio.project_allowed(project_id,'projects.read')) OR (action='delete' AND studio.project_delete_allowed(project_id))));
--> statement-breakpoint
CREATE TRIGGER project_lifecycle_receipt_immutable BEFORE UPDATE OR DELETE ON studio.project_lifecycle_receipts
FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER project_lifecycle_receipt_no_truncate BEFORE TRUNCATE ON studio.project_lifecycle_receipts
FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
    REVOKE INSERT,DELETE ON studio.projects FROM myna_runtime;
    REVOKE ALL ON studio.project_lifecycle_receipts FROM myna_runtime;
    GRANT SELECT,INSERT ON studio.project_lifecycle_receipts TO myna_runtime;
    GRANT EXECUTE ON FUNCTION studio.organization_permission(text),studio.project_delete_allowed(uuid),
      studio.create_company_project(uuid,text,text,text),studio.delete_company_project(uuid,integer) TO myna_runtime;
  END IF;
END $$;
