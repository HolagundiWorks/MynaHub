# Company project authorization

Status: project reads, role management and online edits, 2026-11-15. This is not completion
of platform Phase 3 or the sync protocol. Finance stays in the desktop company file.

## Current API

| Route | Authority |
| --- | --- |
| GET /v1/permissions | Active company member |
| GET /v1/roles | roles.read |
| POST /v1/roles, PATCH /v1/roles/{id} | Organization-scoped roles.manage, fresh session, administrator MFA |
| PUT /v1/members/{id}/roles | Same role-management authority |
| GET /v1/projects, GET /v1/projects/{id} | projects.read plus scope/assignment check |
| GET /v1/mobile/projects/live | Active member; project rows require projects.read and current project scope |
| PATCH /v1/projects/{id} | projects.write and projects.read within project scope; expected revision and operation ID required |
| PUT /v1/projects/{id}/assignments/{membershipId} | projects.assign within that project's scope |
| DELETE /v1/projects/{id}/assignments/{membershipId} | Same assignment authority |

Reads return project id, code, name, status and version only. Collection requests
default to 50 rows, cap at 100, and use an `after` UUID page key; `nextCursor` is
null at the end. This page key is not the future opaque synchronization cursor.
There are no project create/delete endpoints, task endpoints or offline sync.
See [project edits](project-edits.md) for update/conflict/retry behavior.

## Roles and delegation

The current catalogue is `apps/api/src/authz/catalogue.ts`. Eleven immutable
system templates are the union of the seven cloud roles and five desktop roles
(Owner is shared). Future features will add catalogue entries with their endpoints.

| Scope | System templates |
| --- | --- |
| Organization | Owner, Administrator, Principal, Accounts, Auditor |
| Assigned projects | Project Manager, Architect / Engineer, Site Supervisor, Staff, Viewer, Document editor |

Owner/Administrator manage roles and members. Principal manages project teams
practice-wide; Project Manager manages teams only for assigned projects. All
system roles can read projects within their scope. Owner, Administrator, Principal
and Project Manager also edit projects within those scopes. Accounts has no cloud finance
permission; its name preserves the local template, not a new finance service.

Custom roles have a name, description, scope and a bounded list of known
permissions. A role administrator cannot grant a permission or a wider scope
than they hold for that specific permission. Organization-wide role management
does not widen a separately assigned-project read permission. Company
administration permissions require organization scope; project assignment needs
project read permission. Invitation-based assignment has the same delegation
check. Only an actual Owner may assign Owner or alter an Owner's roles. The final
active Owner cannot be demoted. Organization advisory locks serialize role and
ownership changes with member suspension/reactivation. Mutations append audit
events; permission changes are re-read for existing sessions on subsequent requests.

Every role/member/project id is checked against the authenticated company; foreign
and unavailable resource ids return 404. System roles cannot be edited; copy the
permissions into a custom role. Each company is limited to 100 role definitions.

## Project row security

Additive migrations 0005/0006 add role scope, unique role names, project assignments,
the catalogue/template upgrade and project RLS. Existing users, role ids and project
rows are preserved. Existing field users have no assignments until an authorized
manager explicitly adds them; migrating does not implicitly share every project.

`withProjectContext` sets organization id, membership id and user id with
transaction-local `set_config(..., true)`. Queries always run in that transaction;
commit/rollback clears context before a pooled connection is reused. The SQL helper
checks the active membership, actual permission-bearing role, matching company and
an assignment or organization scope. Scope is tied to the role granting that
permission: an unrelated organization-wide role does not widen project access.

RLS is enabled on `studio.projects` and `studio.project_assignments`. Runtime has
no project INSERT/DELETE policy. Migration 0007 adds scoped UPDATE for editable
columns and actor-scoped immutable receipts. Runtime cannot update project
identity, company, creator, creation time or tombstone columns. Assignment writes additionally
check project-assignment authority, and triggers reject cross-company project/member
and membership/role links. The helper is SECURITY DEFINER with an empty search path,
qualified references and public execution revoked. Its schema owner bypasses table
RLS for policy lookup and operator fixtures; runtime is a separate non-owner without
BYPASSRLS, enforced by setup checks. Application requests, including Owner requests,
use runtime credentials. See PostgreSQL's [RLS rules](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)
and [transaction-local configuration](https://www.postgresql.org/docs/current/functions-admin.html).

This is defense against missing query scoping, not arbitrary SQL from an untrusted
holder of the runtime credential: the API owns context assignment. Core/access/auth
metadata is still protected by API authorization, scoped queries and integrity
checks, not complete metadata-table RLS. That wider Phase 3 hardening is pending.
Clients never receive database credentials.

## Verification

API suite: 124 tests passed (configuration/authentication/setup plus authorization,
RLS and upgrade checks), lint/type checking/build passed. PGlite ran real PostgreSQL
policies as `myna_runtime`: missing/mismatched context denies access, assignments
scope reads, field writes and business writes are refused, cross-company links are
rejected, suspension removes access, and context clears on the next transaction.
An existing Phase 2 company upgraded twice without replacing its identities/data.

Applied the additive migrations only to the isolated Neon `demo` branch and ran
live API checks: scoped Staff reads, Project Manager boundaries, assignment removal,
immutable system roles, blocked Administrator-to-Owner escalation and final-Owner
demotion. The runtime driver also verified context cleanup on the same pooled
connection. Production stayed empty; the separate development company was not
migrated by this slice. Test credentials remain in ignored local files.
