-- Permission and system-role snapshot for the project authorization slice.
INSERT INTO access.permissions (key,description,resource_type) VALUES
('organization.read','Read the signed-in company identity','organization'),
('organization.members.manage','Invite and manage company members','organization'),
('audit.read','Read authorized audit events','audit'),
('roles.read','Read company role definitions','roles'),
('roles.manage','Create custom roles and assign existing roles','roles'),
('projects.read','Read projects within the role''s scope','projects'),
('projects.assign','Manage assignments for projects within the role''s scope','projects')
ON CONFLICT (key) DO NOTHING;
--> statement-breakpoint
INSERT INTO access.roles (organization_id,name,description,is_system_role,scope) SELECT o.id,t.name,t.description,true,t.scope FROM core.organizations o CROSS JOIN (VALUES
('Owner','Company ownership and complete administration','organization'),
('Administrator','Company administration without ownership delegation','organization'),
('Project Manager','Manage assigned projects and their team','assigned'),
('Architect / Engineer','Read assigned technical work','assigned'),
('Site Supervisor','Read assigned site work','assigned'),
('Staff','Read assigned company work','assigned'),
('Viewer','Read assigned projects','assigned'),
('Principal','Practice-wide project oversight','organization'),
('Accounts','Read practice projects; finance remains in the desktop company file','organization'),
('Document editor','Read assigned project context','assigned'),
('Auditor','Practice-wide project and role review','organization')
) AS t(name,description,scope) ON CONFLICT (organization_id,name) DO NOTHING;
--> statement-breakpoint
UPDATE access.roles r SET scope=t.scope FROM (VALUES
('Owner','Company ownership and complete administration','organization'),
('Administrator','Company administration without ownership delegation','organization'),
('Project Manager','Manage assigned projects and their team','assigned'),
('Architect / Engineer','Read assigned technical work','assigned'),
('Site Supervisor','Read assigned site work','assigned'),
('Staff','Read assigned company work','assigned'),
('Viewer','Read assigned projects','assigned'),
('Principal','Practice-wide project oversight','organization'),
('Accounts','Read practice projects; finance remains in the desktop company file','organization'),
('Document editor','Read assigned project context','assigned'),
('Auditor','Practice-wide project and role review','organization')
) AS t(name,description,scope) WHERE r.name=t.name AND r.is_system_role;
--> statement-breakpoint
INSERT INTO access.role_permissions (role_id,permission_key) SELECT r.id,t.permission FROM access.roles r JOIN (VALUES
('Owner','organization.read'),
('Owner','organization.members.manage'),
('Owner','audit.read'),
('Owner','roles.read'),
('Owner','roles.manage'),
('Owner','projects.read'),
('Owner','projects.assign'),
('Administrator','organization.read'),
('Administrator','organization.members.manage'),
('Administrator','audit.read'),
('Administrator','roles.read'),
('Administrator','roles.manage'),
('Administrator','projects.read'),
('Administrator','projects.assign'),
('Project Manager','organization.read'),
('Project Manager','projects.read'),
('Project Manager','projects.assign'),
('Architect / Engineer','organization.read'),
('Architect / Engineer','projects.read'),
('Site Supervisor','organization.read'),
('Site Supervisor','projects.read'),
('Staff','organization.read'),
('Staff','projects.read'),
('Viewer','organization.read'),
('Viewer','projects.read'),
('Principal','organization.read'),
('Principal','projects.read'),
('Principal','roles.read'),
('Principal','projects.assign'),
('Accounts','organization.read'),
('Accounts','projects.read'),
('Document editor','organization.read'),
('Document editor','projects.read'),
('Auditor','organization.read'),
('Auditor','projects.read'),
('Auditor','roles.read'),
('Auditor','audit.read')
) AS t(name,permission) ON r.name=t.name WHERE r.is_system_role ON CONFLICT DO NOTHING;
--> statement-breakpoint
-- PROJECT POLICIES BELOW
CREATE FUNCTION studio.project_allowed(p_project uuid, p_permission text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = '' AS $$
  SELECT EXISTS (
    SELECT 1 FROM studio.projects p
    JOIN core.memberships m ON m.organization_id=p.organization_id
    JOIN access.membership_roles mr ON mr.membership_id=m.id
    JOIN access.roles r ON r.id=mr.role_id AND r.organization_id=m.organization_id
    JOIN access.role_permissions rp ON rp.role_id=r.id AND rp.permission_key=p_permission
    WHERE p.id=p_project AND p.deleted_at IS NULL AND m.status='active'
      AND m.id::text=current_setting('app.membership_id',true)
      AND m.auth_user_id=current_setting('app.user_id',true)
      AND p.organization_id::text=current_setting('app.organization_id',true)
      AND (r.scope='organization' OR EXISTS (
        SELECT 1 FROM studio.project_assignments a WHERE a.project_id=p.id
          AND a.organization_id=p.organization_id AND a.membership_id=m.id))
  );
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.project_allowed(uuid,text) FROM PUBLIC;
--> statement-breakpoint
CREATE FUNCTION studio.assignment_company_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = '' AS $$
BEGIN
  IF NOT EXISTS(SELECT 1 FROM studio.projects p JOIN core.memberships m ON m.id=NEW.membership_id
    WHERE p.id=NEW.project_id AND p.organization_id=NEW.organization_id AND m.organization_id=NEW.organization_id) THEN
    RAISE EXCEPTION 'Project and member must belong to the same organization' USING ERRCODE='23503';
  END IF;
  RETURN NEW;
END;
$$;
--> statement-breakpoint
CREATE TRIGGER project_assignment_company BEFORE INSERT OR UPDATE ON studio.project_assignments
FOR EACH ROW EXECUTE FUNCTION studio.assignment_company_guard();
--> statement-breakpoint
CREATE FUNCTION access.membership_role_company_guard() RETURNS trigger
LANGUAGE plpgsql SET search_path = '' AS $$
BEGIN
  IF NOT EXISTS(SELECT 1 FROM core.memberships m JOIN access.roles r ON r.id=NEW.role_id
    WHERE m.id=NEW.membership_id AND r.organization_id=m.organization_id) THEN
    RAISE EXCEPTION 'Role and member must belong to the same organization' USING ERRCODE='23503';
  END IF;
  RETURN NEW;
END;
$$;
--> statement-breakpoint
CREATE TRIGGER membership_role_company BEFORE INSERT OR UPDATE ON access.membership_roles
FOR EACH ROW EXECUTE FUNCTION access.membership_role_company_guard();
--> statement-breakpoint
ALTER TABLE studio.projects ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY project_read ON studio.projects FOR SELECT USING (studio.project_allowed(id,'projects.read'));
--> statement-breakpoint
-- No business-write routes exist in this slice. No INSERT/UPDATE/DELETE policy grants those operations to runtime.
ALTER TABLE studio.project_assignments ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY assignment_read ON studio.project_assignments FOR SELECT
USING (studio.project_allowed(project_id,'projects.read'));
--> statement-breakpoint
CREATE POLICY assignment_insert ON studio.project_assignments FOR INSERT
WITH CHECK (organization_id::text=current_setting('app.organization_id',true) AND studio.project_allowed(project_id,'projects.assign'));
--> statement-breakpoint
CREATE POLICY assignment_delete ON studio.project_assignments FOR DELETE
USING (organization_id::text=current_setting('app.organization_id',true) AND studio.project_allowed(project_id,'projects.assign'));
--> statement-breakpoint
DO $$ BEGIN
  IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
    GRANT SELECT,INSERT,UPDATE,DELETE ON studio.project_assignments TO myna_runtime;
    GRANT EXECUTE ON FUNCTION studio.project_allowed(uuid,text) TO myna_runtime;
  END IF;
END $$;
