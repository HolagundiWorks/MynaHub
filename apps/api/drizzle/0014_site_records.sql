CREATE TABLE "studio"."site_mutation_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"record_id" uuid NOT NULL,
	"action" text NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "site_mutation_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id"),
	CONSTRAINT "site_mutation_action_check" CHECK ("studio"."site_mutation_receipts"."action" in ('create','edit','delete'))
);
--> statement-breakpoint
CREATE TABLE "studio"."site_records" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"kind" text NOT NULL,
	"record_date" date NOT NULL,
	"title" text NOT NULL,
	"details" text DEFAULT '' NOT NULL,
	"location" text DEFAULT '' NOT NULL,
	"assigned_to" text DEFAULT '' NOT NULL,
	"due_date" date,
	"status" text DEFAULT 'open' NOT NULL,
	"resolved_date" date,
	"created_by" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"version" integer DEFAULT 1 NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	"deleted_at" timestamp with time zone,
	CONSTRAINT "site_record_fields_check" CHECK (length(btrim("studio"."site_records"."title")) between 1 and 240 AND length("studio"."site_records"."details")<=10000 AND length("studio"."site_records"."location")<=240 AND length("studio"."site_records"."assigned_to")<=240),
	CONSTRAINT "site_record_kind_check" CHECK ("studio"."site_records"."kind" in ('instruction','inspection','snag')),
	CONSTRAINT "site_record_status_check" CHECK ("studio"."site_records"."status" in ('open','resolved','closed')),
	CONSTRAINT "site_record_dates_check" CHECK ("studio"."site_records"."record_date" between date '0001-01-01' and date '9999-12-31' AND ("studio"."site_records"."due_date" IS NULL OR "studio"."site_records"."due_date" between date '0001-01-01' and date '9999-12-31') AND
 (("studio"."site_records"."status"='open' AND "studio"."site_records"."resolved_date" IS NULL) OR ("studio"."site_records"."status"<>'open' AND "studio"."site_records"."resolved_date" IS NOT NULL AND "studio"."site_records"."resolved_date">="studio"."site_records"."record_date" AND "studio"."site_records"."resolved_date"<=date '9999-12-31'))),
	CONSTRAINT "site_record_version_check" CHECK ("studio"."site_records"."version">=1)
);
--> statement-breakpoint
CREATE UNIQUE INDEX "site_records_org_project_id_unique" ON "studio"."site_records" USING btree ("organization_id","project_id","id");--> statement-breakpoint
ALTER TABLE "studio"."site_mutation_receipts" ADD CONSTRAINT "site_mutation_receipts_organization_id_project_id_record_id_site_records_organization_id_project_id_id_fk" FOREIGN KEY ("organization_id","project_id","record_id") REFERENCES "studio"."site_records"("organization_id","project_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."site_records" ADD CONSTRAINT "site_records_organization_id_project_id_projects_organization_id_id_fk" FOREIGN KEY ("organization_id","project_id") REFERENCES "studio"."projects"("organization_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "site_records_project_idx" ON "studio"."site_records" USING btree ("organization_id","project_id","id");--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type) VALUES
('sites.write','Create and edit project site records','sites'),('sites.delete','Remove site records from live project views','sites') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT r.id,p.key FROM access.roles r CROSS JOIN (VALUES('sites.write'),('sites.delete')) p(key)
WHERE r.is_system_role AND (r.name IN ('Owner','Administrator','Principal','Project Manager') OR (r.name='Site Supervisor' AND p.key='sites.write')) ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE FUNCTION studio.site_action_allowed(p_project uuid,p_record uuid,p_action text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT p_action IN ('create','edit','delete') AND studio.project_allowed(p_project,'projects.read')
 AND studio.project_allowed(p_project,CASE WHEN p_action='delete' THEN 'sites.delete' ELSE 'sites.write' END)
 AND (p_action='create' OR EXISTS(SELECT 1 FROM studio.site_records r WHERE r.id=p_record AND r.project_id=p_project
 AND r.organization_id::text=current_setting('app.organization_id',true) AND (p_action='delete' OR r.deleted_at IS NULL)));
$$;
--> statement-breakpoint
CREATE FUNCTION studio.site_json(p_record studio.site_records) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path='' AS $$
 SELECT jsonb_build_object('id',p_record.id,'projectId',p_record.project_id,'kind',p_record.kind,'recordDate',to_char(p_record.record_date,'YYYY-MM-DD'),
 'title',p_record.title,'details',p_record.details,'location',p_record.location,'assignedTo',p_record.assigned_to,'dueDate',CASE WHEN p_record.due_date IS NULL THEN NULL ELSE to_char(p_record.due_date,'YYYY-MM-DD') END,
 'status',p_record.status,'resolvedDate',CASE WHEN p_record.resolved_date IS NULL THEN NULL ELSE to_char(p_record.resolved_date,'YYYY-MM-DD') END,'version',p_record.version);
$$;
--> statement-breakpoint
CREATE FUNCTION studio.mutate_site_record(p_project uuid,p_id uuid,p_action text,p_expected integer,p_fields jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE r studio.site_records%ROWTYPE;
BEGIN
 IF NOT studio.site_action_allowed(p_project,p_id,p_action) THEN RETURN NULL; END IF;
 IF p_action<>'create' AND (p_expected IS NULL OR p_expected NOT BETWEEN 1 AND 2147483646) THEN RAISE EXCEPTION 'Invalid revision' USING ERRCODE='22023'; END IF;
 PERFORM 1 FROM studio.projects WHERE id=p_project AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR SHARE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF p_action='create' THEN
 INSERT INTO studio.site_records(id,organization_id,project_id,kind,record_date,title,details,location,assigned_to,due_date,status,resolved_date,created_by)
 VALUES(p_id,current_setting('app.organization_id')::uuid,p_project,p_fields->>'kind',(p_fields->>'recordDate')::date,p_fields->>'title',p_fields->>'details',p_fields->>'location',p_fields->>'assignedTo',
 (p_fields->>'dueDate')::date,p_fields->>'status',(p_fields->>'resolvedDate')::date,current_setting('app.user_id')) ON CONFLICT(id) DO NOTHING RETURNING * INTO r;
 IF NOT FOUND THEN RETURN jsonb_build_object('error','SITE_RECORD_ID_IN_USE'); END IF;
 RETURN jsonb_build_object('outcome','created','record',studio.site_json(r));
 END IF;
 SELECT * INTO r FROM studio.site_records WHERE id=p_id AND project_id=p_project AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF r.version<>p_expected THEN RETURN jsonb_build_object('outcome','conflict','expectedVersion',p_expected,'current',studio.site_json(r)); END IF;
 IF p_action='delete' THEN
 UPDATE studio.site_records SET deleted_at=clock_timestamp(),updated_at=clock_timestamp(),version=version+1 WHERE id=p_id RETURNING * INTO r;
 RETURN jsonb_build_object('outcome','deleted','id',r.id,'projectId',r.project_id,'version',r.version,'deletedAt',to_char(r.deleted_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'));
 END IF;
 UPDATE studio.site_records SET kind=p_fields->>'kind',record_date=(p_fields->>'recordDate')::date,title=p_fields->>'title',details=p_fields->>'details',location=p_fields->>'location',assigned_to=p_fields->>'assignedTo',
 due_date=(p_fields->>'dueDate')::date,status=p_fields->>'status',resolved_date=(p_fields->>'resolvedDate')::date,updated_at=clock_timestamp(),version=version+1 WHERE id=p_id RETURNING * INTO r;
 RETURN jsonb_build_object('outcome','applied','record',studio.site_json(r));
END;
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.site_action_allowed(uuid,uuid,text),studio.site_json(studio.site_records),studio.mutate_site_record(uuid,uuid,text,integer,jsonb) FROM PUBLIC;
--> statement-breakpoint
ALTER TABLE studio.site_records ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY site_record_read ON studio.site_records FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL AND studio.project_allowed(project_id,'projects.read'));
--> statement-breakpoint
ALTER TABLE studio.site_mutation_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY site_receipt_read ON studio.site_mutation_receipts FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.site_action_allowed(project_id,record_id,action));
--> statement-breakpoint
CREATE POLICY site_receipt_insert ON studio.site_mutation_receipts FOR INSERT WITH CHECK (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.site_action_allowed(project_id,record_id,action));
--> statement-breakpoint
CREATE TRIGGER site_receipt_immutable BEFORE UPDATE OR DELETE ON studio.site_mutation_receipts FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER site_receipt_no_truncate BEFORE TRUNCATE ON studio.site_mutation_receipts FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
 REVOKE ALL ON studio.site_records,studio.site_mutation_receipts FROM myna_runtime;
 GRANT SELECT ON studio.site_records TO myna_runtime;
 GRANT SELECT,INSERT ON studio.site_mutation_receipts TO myna_runtime;
 GRANT EXECUTE ON FUNCTION studio.site_action_allowed(uuid,uuid,text),studio.mutate_site_record(uuid,uuid,text,integer,jsonb) TO myna_runtime;
END IF; END $$;
