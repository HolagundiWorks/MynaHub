CREATE TABLE "studio"."client_mutation_receipts" (
	"organization_id" uuid NOT NULL,
	"user_id" text NOT NULL,
	"operation_id" uuid NOT NULL,
	"target_id" uuid NOT NULL,
	"action" text NOT NULL,
	"request_hash" text NOT NULL,
	"result" jsonb NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "client_mutation_receipts_organization_id_user_id_operation_id_pk" PRIMARY KEY("organization_id","user_id","operation_id"),
	CONSTRAINT "client_mutation_action_check" CHECK ("studio"."client_mutation_receipts"."action" in ('create','edit','delete','link'))
);
--> statement-breakpoint
ALTER TABLE "studio"."client_mutation_receipts" ADD CONSTRAINT "client_mutation_receipts_organization_id_organizations_id_fk" FOREIGN KEY ("organization_id") REFERENCES "core"."organizations"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
INSERT INTO access.permissions(key,description,resource_type) VALUES
('clients.write','Create and edit company clients and link them to writable projects','clients'),
('clients.delete','Delete unused company clients with organization scope','clients') ON CONFLICT DO NOTHING;
--> statement-breakpoint
INSERT INTO access.role_permissions(role_id,permission_key)
SELECT r.id,p.key FROM access.roles r CROSS JOIN (VALUES('clients.write'),('clients.delete')) p(key)
WHERE r.is_system_role AND (r.name IN ('Owner','Administrator') OR (r.name='Principal' AND p.key='clients.write')) ON CONFLICT DO NOTHING;
--> statement-breakpoint
CREATE FUNCTION studio.client_action_allowed(p_id uuid,p_action text) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
 SELECT studio.organization_permission('clients.write') AND studio.organization_permission('clients.read') AND
 CASE WHEN p_action='create' THEN true
 WHEN p_action='link' THEN studio.project_allowed(p_id,'projects.read') AND studio.project_allowed(p_id,'projects.write')
 WHEN p_action IN ('edit','delete') THEN EXISTS(SELECT 1 FROM studio.clients c WHERE c.id=p_id
   AND c.organization_id::text=current_setting('app.organization_id',true)
   AND (p_action='delete' OR c.deleted_at IS NULL)) AND (p_action<>'delete' OR studio.organization_permission('clients.delete'))
 ELSE false END;
$$;
--> statement-breakpoint
CREATE FUNCTION studio.client_json(p_client studio.clients) RETURNS jsonb
LANGUAGE sql IMMUTABLE SET search_path='' AS $$
 SELECT jsonb_build_object('id',p_client.id,'name',p_client.name,'kind',p_client.kind,'contactPerson',p_client.contact_person,
 'email',p_client.email,'phone',p_client.phone,'address',p_client.address,'correspondenceAddress',p_client.correspondence_address,
 'preferredCommunication',p_client.preferred_communication,'notes',p_client.notes,'version',p_client.version);
$$;
--> statement-breakpoint
CREATE FUNCTION studio.mutate_company_client(p_action text,p_id uuid,p_fields jsonb,p_expected integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE c studio.clients%ROWTYPE; p studio.projects%ROWTYPE; link_id uuid; link_name text; current_link jsonb;
BEGIN
 IF NOT studio.client_action_allowed(p_id,p_action) THEN RETURN NULL; END IF;
 IF p_action<>'create' AND (p_expected IS NULL OR p_expected NOT BETWEEN 1 AND 2147483646) THEN
   RAISE EXCEPTION 'Invalid revision' USING ERRCODE='22023'; END IF;
 IF p_action='link' THEN
   IF jsonb_typeof(p_fields)<>'object' OR NOT (p_fields ? 'clientId') THEN RAISE EXCEPTION 'Invalid link' USING ERRCODE='22023'; END IF;
   link_id=(p_fields->>'clientId')::uuid;
   IF link_id IS NOT NULL THEN
     SELECT * INTO c FROM studio.clients WHERE id=link_id AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR UPDATE;
     IF NOT FOUND THEN RETURN jsonb_build_object('error','CLIENT_NOT_FOUND'); END IF;
   END IF;
   SELECT * INTO p FROM studio.projects WHERE id=p_id AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR UPDATE;
   IF NOT FOUND THEN RETURN NULL; END IF;
   SELECT name INTO link_name FROM studio.clients WHERE id=p.client_id AND deleted_at IS NULL;
   current_link=jsonb_build_object('projectId',p.id,'clientId',p.client_id,'clientName',link_name,'version',p.version);
   IF p.version<>p_expected THEN RETURN jsonb_build_object('outcome','conflict','expectedVersion',p_expected,'current',current_link); END IF;
   UPDATE studio.projects SET client_id=link_id,version=version+1,updated_at=clock_timestamp() WHERE id=p_id RETURNING * INTO p;
   SELECT name INTO link_name FROM studio.clients WHERE id=p.client_id;
   RETURN jsonb_build_object('outcome','linked','project',jsonb_build_object('projectId',p.id,'clientId',p.client_id,'clientName',link_name,'version',p.version));
 END IF;
 IF p_action='create' THEN
   INSERT INTO studio.clients(id,organization_id,name,kind,contact_person,email,phone,address,correspondence_address,preferred_communication,notes)
   VALUES(p_id,current_setting('app.organization_id')::uuid,p_fields->>'name',p_fields->>'kind',p_fields->>'contactPerson',p_fields->>'email',p_fields->>'phone',
     p_fields->>'address',p_fields->>'correspondenceAddress',p_fields->>'preferredCommunication',p_fields->>'notes')
   ON CONFLICT(id) DO NOTHING RETURNING * INTO c;
   IF NOT FOUND THEN RETURN jsonb_build_object('error','CLIENT_ID_IN_USE'); END IF;
   RETURN jsonb_build_object('outcome','created','client',studio.client_json(c));
 END IF;
 SELECT * INTO c FROM studio.clients WHERE id=p_id AND organization_id::text=current_setting('app.organization_id',true) AND deleted_at IS NULL FOR UPDATE;
 IF NOT FOUND THEN RETURN NULL; END IF;
 IF c.version<>p_expected THEN RETURN jsonb_build_object('outcome','conflict','expectedVersion',p_expected,'current',studio.client_json(c)); END IF;
 IF p_action='delete' THEN
   IF EXISTS(SELECT 1 FROM studio.projects WHERE client_id=p_id AND organization_id=c.organization_id AND deleted_at IS NULL) THEN
     RETURN jsonb_build_object('error','CLIENT_IN_USE'); END IF;
   UPDATE studio.clients SET deleted_at=clock_timestamp(),updated_at=clock_timestamp(),version=version+1 WHERE id=p_id RETURNING * INTO c;
   RETURN jsonb_build_object('outcome','deleted','id',c.id,'version',c.version,'deletedAt',to_char(c.deleted_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'));
 END IF;
 UPDATE studio.clients SET name=p_fields->>'name',kind=p_fields->>'kind',contact_person=p_fields->>'contactPerson',email=p_fields->>'email',phone=p_fields->>'phone',
 address=p_fields->>'address',correspondence_address=p_fields->>'correspondenceAddress',preferred_communication=p_fields->>'preferredCommunication',notes=p_fields->>'notes',
 updated_at=clock_timestamp(),version=version+1 WHERE id=p_id RETURNING * INTO c;
 RETURN jsonb_build_object('outcome','applied','client',studio.client_json(c));
END;
$$;
--> statement-breakpoint
REVOKE ALL ON FUNCTION studio.client_action_allowed(uuid,text),studio.client_json(studio.clients),studio.mutate_company_client(text,uuid,jsonb,integer) FROM PUBLIC;
--> statement-breakpoint
ALTER TABLE studio.client_mutation_receipts ENABLE ROW LEVEL SECURITY;
--> statement-breakpoint
CREATE POLICY client_mutation_receipt_read ON studio.client_mutation_receipts FOR SELECT USING (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.client_action_allowed(target_id,action));
--> statement-breakpoint
CREATE POLICY client_mutation_receipt_insert ON studio.client_mutation_receipts FOR INSERT WITH CHECK (
 organization_id::text=current_setting('app.organization_id',true) AND user_id=current_setting('app.user_id',true) AND studio.client_action_allowed(target_id,action));
--> statement-breakpoint
CREATE TRIGGER client_mutation_receipt_immutable BEFORE UPDATE OR DELETE ON studio.client_mutation_receipts FOR EACH ROW EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
CREATE TRIGGER client_mutation_receipt_no_truncate BEFORE TRUNCATE ON studio.client_mutation_receipts FOR EACH STATEMENT EXECUTE FUNCTION studio.edit_receipt_immutable();
--> statement-breakpoint
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='myna_runtime') THEN
  REVOKE ALL ON studio.client_mutation_receipts FROM myna_runtime;
  GRANT SELECT,INSERT ON studio.client_mutation_receipts TO myna_runtime;
  GRANT EXECUTE ON FUNCTION studio.client_action_allowed(uuid,text),studio.mutate_company_client(text,uuid,jsonb,integer) TO myna_runtime;
 END IF;
END $$;
