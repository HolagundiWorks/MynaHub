/** Shared with the mobile connection handshake and Better Auth mount; never a client-supplied URL. */
export const AUTH_BASE_PATH = "/api/auth";
export const MOBILE_CONNECTION = Object.freeze({
  service: "myna-api" as const,
  apiVersion: 1 as const,
  minimumVersionCode: 8,
  authBasePath: AUTH_BASE_PATH,
  capabilities: [
    "identity",
    "invitations",
    "sessions",
    "two-factor",
    "projects.read",
    "projects.edit",
    "projects.live",
    "projects.create",
    "projects.delete",
    "tasks.live",
    "tasks.edit",
    "clients.live",
    "clients.edit",
    "tasks.actions",
    "sites.live",
    "sites.edit",
    "files.live",
    "files.upload",
  ],
});
