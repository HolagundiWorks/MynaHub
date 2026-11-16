/** The current cloud permission contract. Finance remains desktop-only; no permission here exposes a finance API. */
export const PERMISSIONS = {
  organizationRead: "organization.read",
  membersManage: "organization.members.manage",
  auditRead: "audit.read",
  rolesRead: "roles.read",
  rolesManage: "roles.manage",
  projectsRead: "projects.read",
  projectsAssign: "projects.assign",
  projectsWrite: "projects.write",
} as const;
export const PERMISSION_CATALOGUE = Object.values(PERMISSIONS).map((key) => ({
  key,
  description: {
    "organization.read": "Read the signed-in company identity",
    "organization.members.manage": "Invite and manage company members",
    "audit.read": "Read authorized audit events",
    "roles.read": "Read company role definitions",
    "roles.manage": "Create custom roles and assign existing roles",
    "projects.read": "Read projects within the role's scope",
    "projects.assign": "Manage assignments for projects within the role's scope",
    "projects.write": "Edit project details within the role's scope",
  }[key],
  resourceType: key.startsWith("organization.") ? "organization" : key.split(".")[0]!,
}));

export type RoleScope = "organization" | "assigned";
const read = [PERMISSIONS.organizationRead, PERMISSIONS.projectsRead];
export const ROLE_TEMPLATES: {
  name: string;
  description: string;
  permissions: string[];
  scope: RoleScope;
}[] = [
  {
    name: "Owner",
    description: "Company ownership and complete administration",
    permissions: Object.values(PERMISSIONS),
    scope: "organization",
  },
  {
    name: "Administrator",
    description: "Company administration without ownership delegation",
    permissions: Object.values(PERMISSIONS),
    scope: "organization",
  },
  {
    name: "Project Manager",
    description: "Manage assigned projects and their team",
    permissions: [...read, PERMISSIONS.projectsAssign, PERMISSIONS.projectsWrite],
    scope: "assigned",
  },
  {
    name: "Architect / Engineer",
    description: "Read assigned technical work",
    permissions: read,
    scope: "assigned",
  },
  { name: "Site Supervisor", description: "Read assigned site work", permissions: read, scope: "assigned" },
  { name: "Staff", description: "Read assigned company work", permissions: read, scope: "assigned" },
  { name: "Viewer", description: "Read assigned projects", permissions: read, scope: "assigned" },
  {
    name: "Principal",
    description: "Practice-wide project oversight",
    permissions: [...read, PERMISSIONS.rolesRead, PERMISSIONS.projectsAssign, PERMISSIONS.projectsWrite],
    scope: "organization",
  },
  {
    name: "Accounts",
    description: "Read practice projects; finance remains in the desktop company file",
    permissions: read,
    scope: "organization",
  },
  {
    name: "Document editor",
    description: "Read assigned project context",
    permissions: read,
    scope: "assigned",
  },
  {
    name: "Auditor",
    description: "Practice-wide project and role review",
    permissions: [...read, PERMISSIONS.rolesRead, PERMISSIONS.auditRead],
    scope: "organization",
  },
];
