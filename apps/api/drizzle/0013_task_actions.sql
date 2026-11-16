CREATE TABLE "studio"."task_action_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"task_id" uuid NOT NULL,
	"action" text NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "task_action_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id"),
	CONSTRAINT "task_action_check" CHECK ("studio"."task_action_receipts"."action" in ('assign','delete'))
);
--> statement-breakpoint
CREATE TABLE "studio"."task_assignments" (
	"organization_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"task_id" uuid NOT NULL,
	"membership_id" uuid NOT NULL,
	CONSTRAINT "task_assignments_task_id_membership_id_pk" PRIMARY KEY("task_id","membership_id")
);
--> statement-breakpoint
ALTER TABLE "studio"."task_action_receipts" ADD CONSTRAINT "task_action_receipts_organization_id_project_id_task_id_project_tasks_organization_id_project_id_id_fk" FOREIGN KEY ("organization_id","project_id","task_id") REFERENCES "studio"."project_tasks"("organization_id","project_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."task_assignments" ADD CONSTRAINT "task_assignments_membership_id_memberships_id_fk" FOREIGN KEY ("membership_id") REFERENCES "core"."memberships"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."task_assignments" ADD CONSTRAINT "task_assignments_organization_id_project_id_task_id_project_tasks_organization_id_project_id_id_fk" FOREIGN KEY ("organization_id","project_id","task_id") REFERENCES "studio"."project_tasks"("organization_id","project_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type) VALUES
('tasks.assign','Manage task assignees within project scope','tasks'),('tasks.delete','Remove tasks from live views within project scope','tasks') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT r.id,p.key FROM access.roles r CROSS JOIN (VALUES('tasks.assign'),('tasks.delete')) p(key)
WHERE r.is_system_role AND r.name IN ('Owner','Administrator','Principal','Project Manager') ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE FUNCTION studio.task_member_eligible(p_project uuid,p_member uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT studio.project_allowed(p_project,'projects.read') AND EXISTS(
 SELECT 1 FROM studio.projects p JOIN core.memberships m ON m.organization_id=p.organization_id
 JOIN auth."user" u ON u.id::text=m.auth_user_id
 JOIN access.membership_roles mr ON mr.membership_id=m.id
 JOIN access.roles r ON r.id=mr.role_id AND r.organization_id=m.organization_id
 JOIN access.role_permissions rp ON rp.role_id=r.id AND rp.permission_key='projects.read'
 WHERE p.id=p_project AND p.deleted_at IS NULL AND p.organization_id::text=current_setting('app.organization_id',true)
 AND m.id=p_member AND m.status='active' AND (r.scope='organization' OR EXISTS(
 SELECT 1 FROM studio.project_assignments a WHERE a.organization_id=p.organization_id AND a.project_id=p.id AND a.membership_id=m.id)));
$$;
--> statement-breakpoint
CREATE FUNCTION studio.task_assignees(p_task uuid) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT coalesce(jsonb_agg(jsonb_build_object('membershipId',m.id,'name',u.name) ORDER BY m.id),'[]'::jsonb)
 FROM studio.task_assignments a JOIN studio.project_tasks t ON t.id=a.task_id AND t.project_id=a.project_id AND t.organization_id=a.organization_id
 JOIN core.memberships m ON m.id=a.membership_id AND m.organization_id=a.organization_id
 JOIN auth."user" u ON u.id::text=m.auth_user_id
 WHERE a.task_id=p_task AND t.deleted_at IS NULL AND a.organization_id::text=current_setting('app.organization_id',true)
 AND studio.task_member_eligible(a.project_id,m.id);
$$;
--> statement-breakpoint
CREATE FUNCTION studio.task_action_allowed(p_project uuid,p_task uuid,p_action text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT p_action IN ('assign','delete') AND studio.project_allowed(p_project,'projects.read')
 AND studio.project_allowed(p_project,CASE WHEN p_action='assign' THEN 'tasks.assign' ELSE 'tasks.delete' END)
 AND EXISTS(SELECT 1 FROM studio.project_tasks t WHERE t.id=p_task AND t.project_id=p_project
 AND t.organization_id::text=current_setting('app.organization_id',true) AND (p_action='delete' OR t.deleted_at IS NULL));
$$;
--> statement-breakpoint
CREATE FUNCTION studio.task_json(p_task studio.project_tasks) RETURNS jsonb
LANGUAGE sql STABLE SET search_path='' AS $$
 SELECT jsonb_build_object('id',p_task.id,'projectId',p_task.project_id,'title',p_task.title,'description',p_task.description,
 'dueAt',CASE WHEN p_task.due_at IS NULL THEN NULL ELSE to_char(p_task.due_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') END,
 'status',p_task.status,'priority',p_task.priority,'version',p_task.version,'assignees',studio.task_assignees(p_task.id));
$$;
--> statement-breakpoint
CREATE FUNCTION studio.mutate_task_action(p_project uuid,p_task uuid,p_action text,p_expected integer,p_members uuid[]) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE t studio.project_tasks%ROWTYPE;
BEGIN
 IF NOT studio.task_action_allowed(p_project,p_task,p_action) THEN RETURN NULL; END IF;
 IF p_expected IS NULL OR p_expected NOT BETWEEN 1 AND 2147483646 THEN RAISE EXCEPTION 'Invalid revision' USING ERRCODE='22023'; END IF;
 PERFORM 1 FROM studio.projects WHERE id=p_project AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 SELECT * INTO t FROM studio.project_tasks WHERE id=p_task AND project_id=p_project AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF t.version<>p_expected THEN RETURN jsonb_build_object('outcome','conflict','expectedVersion',p_expected,'current',studio.task_json(t)); END IF;
 IF p_action='delete' THEN
 UPDATE studio.project_tasks SET deleted_at=clock_timestamp(),updated_at=clock_timestamp(),version=version+1 WHERE id=p_task RETURNING * INTO t;
 RETURN jsonb_build_object('outcome','deleted','id',t.id,'projectId',t.project_id,'version',t.version,'deletedAt',to_char(t.deleted_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'));
 END IF;
 IF p_members IS NULL OR cardinality(p_members)>20 OR cardinality(p_members)<>(SELECT count(DISTINCT x) FROM unnest(p_members) x)
 OR EXISTS(SELECT 1 FROM unnest(p_members) m WHERE m IS NULL OR NOT studio.task_member_eligible(p_project,m)) THEN
 RETURN jsonb_build_object('error','INVALID_ASSIGNEES'); END IF;
 DELETE FROM studio.task_assignments WHERE task_id=p_task AND project_id=p_project AND organization_id=t.organization_id;
 INSERT INTO studio.task_assignments(organization_id,project_id,task_id,membership_id) SELECT t.organization_id,p_project,p_task,m FROM unnest(p_members) m;
 UPDATE studio.project_tasks SET updated_at=clock_timestamp(),version=version+1 WHERE id=p_task RETURNING * INTO t;
 RETURN jsonb_build_object('outcome','assigned','task',studio.task_json(t));
END;
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.task_member_eligible(uuid,uuid),studio.task_assignees(uuid),studio.task_action_allowed(uuid,uuid,text),studio.task_json(studio.project_tasks),studio.mutate_task_action(uuid,uuid,text,integer,uuid[]) FROM PUBLIC;
--> statement-breakpoint
ALTER TABLE studio.task_assignments ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY task_assignment_read ON studio.task_assignments FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND studio.project_allowed(project_id,'projects.read')
 AND EXISTS(SELECT 1 FROM studio.project_tasks t WHERE t.id=task_assignments.task_id AND t.project_id=task_assignments.project_id AND t.deleted_at IS NULL));
--> statement-breakpoint
ALTER TABLE studio.task_action_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY task_action_receipt_read ON studio.task_action_receipts FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.task_action_allowed(project_id,task_id,action));
--> statement-breakpoint
CREATE POLICY task_action_receipt_insert ON studio.task_action_receipts FOR INSERT WITH CHECK (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.task_action_allowed(project_id,task_id,action));
--> statement-breakpoint
CREATE TRIGGER task_action_receipt_immutable BEFORE UPDATE OR DELETE ON studio.task_action_receipts FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER task_action_receipt_no_truncate BEFORE TRUNCATE ON studio.task_action_receipts FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
 REVOKE ALL ON studio.task_assignments,studio.task_action_receipts FROM myna_runtime;
 GRANT SELECT ON studio.task_assignments TO myna_runtime;
 GRANT SELECT,INSERT ON studio.task_action_receipts TO myna_runtime;
 GRANT EXECUTE ON FUNCTION studio.task_member_eligible(uuid,uuid),studio.task_assignees(uuid),studio.task_action_allowed(uuid,uuid,text),studio.mutate_task_action(uuid,uuid,text,integer,uuid[]) TO myna_runtime;
END IF; END $$;
