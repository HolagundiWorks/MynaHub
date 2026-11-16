CREATE TABLE "studio"."attachment_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"file_id" uuid NOT NULL,
	"action" text NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "attachment_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id"),
	CONSTRAINT "attachment_action_check" CHECK ("studio"."attachment_receipts"."action" in ('upload','delete'))
);
--> statement-breakpoint
CREATE TABLE "studio"."attachments" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"organization_id" uuid NOT NULL,
	"project_id" uuid NOT NULL,
	"name" text NOT NULL,
	"content_type" text NOT NULL,
	"size" integer NOT NULL,
	"sha256" text NOT NULL,
	"object_key" text NOT NULL,
	"created_by" text NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"version" integer DEFAULT 1 NOT NULL,
	"deleted_at" timestamp with time zone,
	CONSTRAINT "attachments_object_key_unique" UNIQUE("object_key"),
	CONSTRAINT "attachment_fields_check" CHECK (length(btrim("studio"."attachments"."name")) between 1 and 180 AND "studio"."attachments"."size" between 1 and 5242880 AND "studio"."attachments"."sha256" ~ '^[a-f0-9]{64}$' AND "studio"."attachments"."version">=1),
	CONSTRAINT "attachment_type_check" CHECK ("studio"."attachments"."content_type" in ('application/pdf','image/png','image/jpeg','image/webp','text/plain','text/csv'))
);
--> statement-breakpoint
CREATE UNIQUE INDEX "attachments_org_project_id_unique" ON "studio"."attachments" USING btree ("organization_id","project_id","id");--> statement-breakpoint
ALTER TABLE "studio"."attachment_receipts" ADD CONSTRAINT "attachment_receipts_organization_id_project_id_file_id_attachments_organization_id_project_id_id_fk" FOREIGN KEY ("organization_id","project_id","file_id") REFERENCES "studio"."attachments"("organization_id","project_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "studio"."attachments" ADD CONSTRAINT "attachments_organization_id_project_id_projects_organization_id_id_fk" FOREIGN KEY ("organization_id","project_id") REFERENCES "studio"."projects"("organization_id","id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "attachments_project_idx" ON "studio"."attachments" USING btree ("organization_id","project_id","id");--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type) VALUES
('files.write','Upload files within project scope','files'),('files.delete','Remove files from live project views','files') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT r.id,p.key FROM access.roles r CROSS JOIN (VALUES('files.write'),('files.delete')) p(key)
WHERE r.is_system_role AND (r.name IN ('Owner','Administrator','Principal','Project Manager') OR (r.name IN ('Document editor','Site Supervisor') AND p.key='files.write')) ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE FUNCTION studio.file_action_allowed(p_project uuid,p_file uuid,p_action text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT p_action IN ('upload','delete') AND studio.project_allowed(p_project,'projects.read')
 AND studio.project_allowed(p_project,CASE WHEN p_action='delete' THEN 'files.delete' ELSE 'files.write' END)
 AND (p_action='upload' OR EXISTS(SELECT 1 FROM studio.attachments f WHERE f.id=p_file AND f.project_id=p_project AND f.organization_id::text=current_setting('app.organization_id',true)));
$$;
--> statement-breakpoint
CREATE FUNCTION studio.file_id_reserved(p_project uuid,p_file uuid) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT studio.file_action_allowed(p_project,p_file,'upload') AND EXISTS(SELECT 1 FROM studio.attachments WHERE id=p_file);
$$;
--> statement-breakpoint
CREATE FUNCTION studio.file_json(p_file studio.attachments) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path='' AS $$
 SELECT jsonb_build_object('id',p_file.id,'projectId',p_file.project_id,'name',p_file.name,'contentType',p_file.content_type,'size',p_file.size,'sha256',p_file.sha256,'version',p_file.version);
$$;
--> statement-breakpoint
CREATE FUNCTION studio.register_attachment(p_project uuid,p_file uuid,p_name text,p_type text,p_size integer,p_sha text) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE f studio.attachments%ROWTYPE;
BEGIN
 IF NOT studio.file_action_allowed(p_project,p_file,'upload') THEN RETURN NULL; END IF;
 INSERT INTO studio.attachments(id,organization_id,project_id,name,content_type,size,sha256,object_key,created_by)
 VALUES(p_file,current_setting('app.organization_id')::uuid,p_project,p_name,p_type,p_size,p_sha,
 current_setting('app.organization_id')||'/'||p_project::text||'/'||p_file::text||'/'||p_sha,current_setting('app.user_id'))
 ON CONFLICT(id) DO NOTHING RETURNING * INTO f;
 IF NOT FOUND THEN RETURN NULL; END IF;
 RETURN studio.file_json(f);
END;
$$;
--> statement-breakpoint
CREATE FUNCTION studio.delete_attachment(p_project uuid,p_file uuid,p_expected integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE f studio.attachments%ROWTYPE;
BEGIN
 IF NOT studio.file_action_allowed(p_project,p_file,'delete') THEN RETURN NULL; END IF;
 IF p_expected IS NULL OR p_expected NOT BETWEEN 1 AND 2147483646 THEN RAISE EXCEPTION 'Invalid revision' USING ERRCODE='22023'; END IF;
 SELECT * INTO f FROM studio.attachments WHERE id=p_file AND project_id=p_project AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF f.version<>p_expected THEN RETURN jsonb_build_object('outcome','conflict','expectedVersion',p_expected,'current',studio.file_json(f)); END IF;
 UPDATE studio.attachments SET deleted_at=clock_timestamp(),version=version+1 WHERE id=p_file RETURNING * INTO f;
 RETURN jsonb_build_object('outcome','deleted','id',f.id,'projectId',f.project_id,'version',f.version,'deletedAt',to_char(f.deleted_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'));
END;
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.file_action_allowed(uuid,uuid,text),studio.file_id_reserved(uuid,uuid),studio.file_json(studio.attachments),studio.register_attachment(uuid,uuid,text,text,integer,text),studio.delete_attachment(uuid,uuid,integer) FROM PUBLIC;
--> statement-breakpoint
ALTER TABLE studio.attachments ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY attachment_read ON studio.attachments FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL AND studio.project_allowed(project_id,'projects.read'));
--> statement-breakpoint
ALTER TABLE studio.attachment_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY attachment_receipt_read ON studio.attachment_receipts FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.file_action_allowed(project_id,file_id,action));
--> statement-breakpoint
CREATE POLICY attachment_receipt_insert ON studio.attachment_receipts FOR INSERT WITH CHECK (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.file_action_allowed(project_id,file_id,action));
--> statement-breakpoint
CREATE TRIGGER attachment_receipt_immutable BEFORE UPDATE OR DELETE ON studio.attachment_receipts FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER attachment_receipt_no_truncate BEFORE TRUNCATE ON studio.attachment_receipts FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
 REVOKE ALL ON studio.attachments,studio.attachment_receipts FROM myna_runtime;
 GRANT SELECT ON studio.attachments TO myna_runtime;
 GRANT SELECT,INSERT ON studio.attachment_receipts TO myna_runtime;
 GRANT EXECUTE ON FUNCTION studio.file_action_allowed(uuid,uuid,text),studio.file_id_reserved(uuid,uuid),studio.register_attachment(uuid,uuid,text,text,integer,text),studio.delete_attachment(uuid,uuid,integer) TO myna_runtime;
END IF; END $$;
