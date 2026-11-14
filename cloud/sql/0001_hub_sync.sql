-- MynaHub company sync, wave 1 (docs/hub-sync-design.md sections 4 to 6). Additive; safe to run again.
-- Clients reach this schema only through the Neon Data API with a Neon Auth token, never with a database owner credential.
-- Membership is held in hub.company_member, NOT in neon_auth user fields (users can edit those; see Neon's Data API advisor).

create schema if not exists hub;

create table if not exists hub.company (
  id text primary key,                       -- the company file's company_uuid
  name text not null default '',
  created_by text not null,
  created_at timestamptz not null default now()
);

create table if not exists hub.company_member (
  company_id text not null references hub.company(id),
  user_id text not null,
  email text not null default '',
  role text not null check (role in ('owner','principal','accounts','document_editor','auditor','member')),
  created_at timestamptz not null default now(),
  primary key (company_id, user_id)
);

create table if not exists hub.company_seq (
  company_id text primary key references hub.company(id),
  last_seq bigint not null default 0
);

-- One row per synced thing; wave 1 kinds only. body is the entity as JSON (money, when it arrives, in paise). Rows are never deleted: deleted = true is the tombstone.
create table if not exists hub.record (
  company_id text not null references hub.company(id),
  kind text not null check (kind in ('client','project','project_stage','task')),
  sync_id uuid not null,
  rev bigint not null default 1,
  seq bigint not null,
  deleted boolean not null default false,
  updated_ms bigint not null,
  updated_by text not null,
  body jsonb not null default '{}'::jsonb,
  primary key (company_id, kind, sync_id)
);
create index if not exists record_company_seq on hub.record (company_id, seq);

-- The losing side of a conflict is kept, never dropped.
create table if not exists hub.conflict (
  id bigserial primary key,
  company_id text not null references hub.company(id),
  kind text not null,
  sync_id uuid not null,
  loser_updated_ms bigint not null,
  loser_updated_by text not null,
  loser_deleted boolean not null,
  loser_body jsonb not null,
  at timestamptz not null default now()
);

alter table hub.company enable row level security;
alter table hub.company_member enable row level security;
alter table hub.company_seq enable row level security;
alter table hub.record enable row level security;
alter table hub.conflict enable row level security;

-- Who is the caller in company cid (null when not a member). Security definer so the policies do not recurse into company_member.
create or replace function hub.role_of(cid text) returns text
language sql stable security definer set search_path = hub, pg_temp as $$
  select role from hub.company_member where company_id = cid and user_id = auth.user_id()
$$;

drop policy if exists company_select on hub.company;
create policy company_select on hub.company for select to authenticated using (hub.role_of(id) is not null);
drop policy if exists member_select on hub.company_member;
create policy member_select on hub.company_member for select to authenticated using (hub.role_of(company_id) is not null);
drop policy if exists record_select on hub.record;
create policy record_select on hub.record for select to authenticated using (hub.role_of(company_id) is not null);
drop policy if exists conflict_select on hub.conflict;
create policy conflict_select on hub.conflict for select to authenticated using (hub.role_of(company_id) in ('owner','principal'));
-- No insert, update or delete policy on any table: writes go through the functions below, which check the caller.

create or replace function hub.create_company(p_id text, p_name text) returns void
language plpgsql security definer set search_path = hub, pg_temp as $$
declare uid text := auth.user_id(); mail text;
begin
  if uid is null then raise exception 'not signed in' using errcode = '28000'; end if;
  if coalesce(p_id, '') = '' or length(p_id) > 80 then raise exception 'bad company id'; end if;
  if exists (select 1 from hub.company where id = p_id) then
    if hub.role_of(p_id) = 'owner' then return; end if;
    raise exception 'company already exists' using errcode = '42501';
  end if;
  select email into mail from neon_auth."user" where id::text = uid;
  insert into hub.company (id, name, created_by) values (p_id, coalesce(p_name, ''), uid);
  insert into hub.company_seq (company_id) values (p_id);
  insert into hub.company_member (company_id, user_id, email, role) values (p_id, uid, coalesce(mail, ''), 'owner');
end $$;

-- Owner only. The person must already have a cloud account; role changes use the same call.
create or replace function hub.set_member(p_company text, p_email text, p_role text) returns void
language plpgsql security definer set search_path = hub, pg_temp as $$
declare target text; mail text;
begin
  if hub.role_of(p_company) is distinct from 'owner' then raise exception 'only the owner may change members' using errcode = '42501'; end if;
  select id::text, email into target, mail from neon_auth."user" where lower(email) = lower(p_email);
  if target is null then raise exception 'no cloud account for that email' using errcode = 'P0002'; end if;
  if exists (select 1 from hub.company_member where company_id = p_company and user_id = target and role = 'owner') and p_role <> 'owner'
     and (select count(*) from hub.company_member where company_id = p_company and role = 'owner') = 1 then
    raise exception 'the last owner cannot be changed' using errcode = '42501';
  end if;
  insert into hub.company_member (company_id, user_id, email, role) values (p_company, target, mail, p_role)
  on conflict (company_id, user_id) do update set role = excluded.role;
end $$;

create or replace function hub.remove_member(p_company text, p_user text) returns void
language plpgsql security definer set search_path = hub, pg_temp as $$
begin
  if hub.role_of(p_company) is distinct from 'owner' then raise exception 'only the owner may change members' using errcode = '42501'; end if;
  if (select role from hub.company_member where company_id = p_company and user_id = p_user) = 'owner'
     and (select count(*) from hub.company_member where company_id = p_company and role = 'owner') = 1 then
    raise exception 'the last owner cannot be removed' using errcode = '42501';
  end if;
  delete from hub.company_member where company_id = p_company and user_id = p_user;
end $$;

-- Push a batch (max 500) of {kind, sync_id, base_rev, updated_ms, deleted, body}. Returns [{sync_id, kind, status, rev, seq}].
-- status: created | updated | merged (conflict, incoming newer, kept) | rejected_older (conflict, incoming older, kept in hub.conflict) | forbidden
-- Conflict rule (design 4.4): base_rev must equal the cloud rev; otherwise the later updated_ms wins and the loser is kept.
create or replace function hub.push_records(p_company text, p_records jsonb) returns jsonb
language plpgsql security definer set search_path = hub, pg_temp as $$
declare
  r jsonb; me text := auth.user_id(); myrole text := hub.role_of(p_company);
  cur hub.record; k text; sid uuid; base bigint; ums bigint; del boolean; bod jsonb; nseq bigint; out jsonb := '[]'::jsonb; st text;
begin
  if myrole is null then raise exception 'not a member of this company' using errcode = '42501'; end if;
  if jsonb_typeof(p_records) <> 'array' or jsonb_array_length(p_records) > 500 then raise exception 'batch must be an array of at most 500'; end if;
  for r in select * from jsonb_array_elements(p_records) loop
    k := r->>'kind'; sid := (r->>'sync_id')::uuid; base := coalesce((r->>'base_rev')::bigint, 0);
    ums := (r->>'updated_ms')::bigint; del := coalesce((r->>'deleted')::boolean, false); bod := coalesce(r->'body', '{}'::jsonb);
    if myrole = 'auditor' or k not in ('client','project','project_stage','task') then
      out := out || jsonb_build_object('sync_id', sid, 'kind', k, 'status', 'forbidden', 'rev', 0, 'seq', 0);
      continue;
    end if;
    update hub.company_seq set last_seq = last_seq + 1 where company_id = p_company returning last_seq into nseq;
    select * into cur from hub.record where company_id = p_company and kind = k and sync_id = sid for update;
    if not found then
      insert into hub.record (company_id, kind, sync_id, rev, seq, deleted, updated_ms, updated_by, body)
      values (p_company, k, sid, 1, nseq, del, ums, me, bod);
      st := 'created';
      out := out || jsonb_build_object('sync_id', sid, 'kind', k, 'status', st, 'rev', 1, 'seq', nseq);
    elsif base = cur.rev or ums > cur.updated_ms then
      if base <> cur.rev then
        insert into hub.conflict (company_id, kind, sync_id, loser_updated_ms, loser_updated_by, loser_deleted, loser_body)
        values (p_company, k, sid, cur.updated_ms, cur.updated_by, cur.deleted, cur.body);
        st := 'merged';
      else st := 'updated'; end if;
      update hub.record set rev = cur.rev + 1, seq = nseq, deleted = del, updated_ms = ums, updated_by = me, body = bod
      where company_id = p_company and kind = k and sync_id = sid;
      out := out || jsonb_build_object('sync_id', sid, 'kind', k, 'status', st, 'rev', cur.rev + 1, 'seq', nseq);
    else
      insert into hub.conflict (company_id, kind, sync_id, loser_updated_ms, loser_updated_by, loser_deleted, loser_body)
      values (p_company, k, sid, ums, me, del, bod);
      out := out || jsonb_build_object('sync_id', sid, 'kind', k, 'status', 'rejected_older', 'rev', cur.rev, 'seq', cur.seq);
    end if;
  end loop;
  return out;
end $$;

-- Nothing is reachable by default; grant only what clients need.
revoke all on schema hub from public, anonymous;
revoke all on all tables in schema hub from public, anonymous, authenticated;
revoke all on all functions in schema hub from public, anonymous;
grant usage on schema hub to authenticated;
grant select on hub.company, hub.company_member, hub.record, hub.conflict to authenticated;
grant execute on function hub.create_company(text, text), hub.set_member(text, text, text), hub.remove_member(text, text),
  hub.push_records(text, jsonb), hub.role_of(text) to authenticated;
