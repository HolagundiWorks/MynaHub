# Neon Functions deployment preparation

Updated 2026-11-17. The owner selected Neon Functions, with a custom domain to follow,
then requested **preparation only**. No Function was published, production database
migrated or DNS changed.

## Prepared implementation

`apps/api/src/function.ts` exposes the existing Fastify API through Fetch, preserving
Better Auth, bearer sessions, separate cookies, validation and authorization. Requests
are bounded to 7 MiB; decoded attachments remain limited to 5 MiB. Caller-supplied
forwarding headers are discarded. Responses retain the API's no-store policy.

One pool per isolate uses at most five connections with Neon's idle-pool handler.
`MYNA_RUNTIME_DATABASE_URL` must use the restricted `myna_runtime` role and TLS on the
same branch/database as Neon's injected `DATABASE_URL`. The actual role is checked
before serving. Migration-owner credentials are refused. The bundle embeds the
shipped migration count; Functions do not apply migrations.

`npm run build:function` creates ignored `dist-function/index.mjs` and `function.zip`
with esbuild and Neon's bundling helper. `neon.ts` declares slug `mynahub` and explicit
runtime settings. Credentials and bundles stay out of git.

## When deployment resumes

1. Run `neon login --profile mynahub-deploy` with the correct account. Verify access
   to Hub project `soft-cherry-88367368`, organization `org-floral-sun-23006249`.
   The existing default CLI account cannot access it; do not substitute another project.
2. Run API checks and `npm run build:function` in `apps/api`.
3. Target demo explicitly. An initial code-only deployment gives the invocation URL
   while missing settings fail closed:

   ```powershell
   neon functions deploy mynahub --src dist-function --no-bundle --project-id soft-cherry-88367368 --branch br-round-grass-a1lsapi6 --profile mynahub-deploy
   neon functions get mynahub --project-id soft-cherry-88367368 --branch br-round-grass-a1lsapi6 --profile mynahub-deploy
   ```

4. Use the returned invocation origin; do not guess its hostname. Create a private
   environment file, then deploy without exposing credentials in argv:

   ```powershell
   node scripts/prepare-function-env.mjs .env.demo .env.functions-demo https://RETURNED-NATIVE-HOST
   neon deploy --config neon.ts --env .env.functions-demo --project-id soft-cherry-88367368 --branch br-round-grass-a1lsapi6 --profile mynahub-deploy --no-env-pull
   ```

   Preparation creates a new file exclusively and copies only runtime connection,
   authentication and private-storage settings. It never copies the owner URL.
   Review the CLI plan before applying it. Preserve the deployed auth secret.
5. Check native HTTPS health/readiness, unauthenticated denial, sign-in/MFA, polling,
   revocation, file upload/view/deletion and Mobile compatibility with fictional data.

## Production gates and evidence

Metadata RLS, load checks, object retention/reconciliation, signed Mobile installation
and public deployment verification remain pending. Native Function URLs stay publicly
reachable. Per-isolate rate limits are not global abuse controls. Without a verified
ingress IP policy, Better Auth uses a conservative shared bucket; do not disable it.
When the domain is supplied, verify an ingress/rate-limit design. Never ship an origin
secret in Mobile. A custom-domain registration does not disable the native URL or add a WAF.

191 API tests, lint/types/build and bundle build passed; the runtime dependency audit
reported zero vulnerabilities. The packaged handler passed demo health/readiness,
unauthenticated denial, sign-in, live project reads, private-storage access and sign-out
locally. Deployed HTTPS, custom domains and scale-out were **not** verified.

Official references: [deployment](https://neon.com/docs/compute/functions/deploy),
[runtime limits](https://neon.com/docs/compute/functions/reference/runtime-limits),
[authentication](https://neon.com/docs/compute/functions/authentication).
