namespace Adms.Cloud;

internal static class OspSyncSchema
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS osp_project (
          id TEXT PRIMARY KEY,
          owner_id TEXT NOT NULL,
          name TEXT NOT NULL,
          latest_revision BIGINT NOT NULL DEFAULT 0,
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS osp_project_revision (
          project_id TEXT NOT NULL REFERENCES osp_project(id) ON DELETE CASCADE,
          revision BIGINT NOT NULL,
          pushed_by TEXT,
          sha256 TEXT NOT NULL,
          size_bytes BIGINT NOT NULL,
          storage_key TEXT NOT NULL,
          created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
          PRIMARY KEY (project_id, revision)
        );

        CREATE TABLE IF NOT EXISTS osp_markup (
          id TEXT PRIMARY KEY,
          project_id TEXT NOT NULL REFERENCES osp_project(id) ON DELETE CASCADE,
          drawing TEXT NOT NULL,
          author TEXT NOT NULL,
          payload JSONB NOT NULL DEFAULT '{}'::jsonb,
          updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE OR REPLACE FUNCTION osp_notify_revision() RETURNS trigger AS $$
        BEGIN
          PERFORM pg_notify('osp_revision', NEW.project_id || ':' || NEW.revision::text);
          RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS osp_project_revision_notify ON osp_project_revision;
        CREATE TRIGGER osp_project_revision_notify
          AFTER INSERT ON osp_project_revision
          FOR EACH ROW EXECUTE FUNCTION osp_notify_revision();
        """;
}
