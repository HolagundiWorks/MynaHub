import { createAuthClient } from "@neondatabase/auth";
import { BetterAuthVanillaAdapter } from "@neondatabase/auth/vanilla/adapters";

export type AuthSmokeResult = {
  userId: string;
  email: string;
  sessionUserId: string | null;
};

const defaultOrigin = () => process.env.NEON_AUTH_ORIGIN ?? "http://localhost:3000";

/** Runs fn while `@neondatabase/auth` requests share one in-memory cookie jar. */
export async function withNodeAuthCookies<T>(fn: () => Promise<T>): Promise<T> {
  let cookieJar = "";
  const baseFetch = globalThis.fetch.bind(globalThis);
  const fetchWithCookies: typeof fetch = async (input, init) => {
    const headers = new Headers(init?.headers as HeadersInit | undefined);
    if (cookieJar) headers.set("cookie", cookieJar);
    const response = await baseFetch(input, { ...init, headers });
    const setCookies =
      typeof response.headers.getSetCookie === "function" ? response.headers.getSetCookie() : [];
    if (setCookies.length) {
      cookieJar = setCookies.map((c) => c.split(";")[0]).join("; ");
    }
    return response;
  };
  const priorFetch = globalThis.fetch;
  globalThis.fetch = fetchWithCookies;
  try {
    return await fn();
  } finally {
    globalThis.fetch = priorFetch;
  }
}

/** Email/password sign-up and session read against Managed Better Auth (Node smoke test). */
export async function runAuthEmailSmoke(
  baseUrl: string,
  email: string,
  password: string,
  name: string,
): Promise<AuthSmokeResult> {
  return withNodeAuthCookies(async () => {
    const auth = createAuthClient(baseUrl, {
      adapter: BetterAuthVanillaAdapter({
        fetchOptions: { headers: { Origin: defaultOrigin() } },
      }),
    });

    const signUp = await auth.signUp.email({ email, password, name });
    if (signUp.error) {
      const msg = signUp.error.message ?? String(signUp.error);
      if (!/already|exist/i.test(msg)) {
        throw new Error(`signUp.email failed: ${msg}`);
      }
    }

    const signIn = await auth.signIn.email({ email, password });
    if (signIn.error) {
      throw new Error(`signIn.email failed: ${signIn.error.message ?? String(signIn.error)}`);
    }

    const session = await auth.getSession();
    if (session.error) {
      throw new Error(`getSession failed: ${session.error.message ?? String(session.error)}`);
    }

    const user = session.data?.user ?? signIn.data?.user ?? signUp.data?.user;
    if (!user?.id || !user.email) {
      throw new Error("No user in session after sign-up/sign-in");
    }

    return {
      userId: user.id,
      email: user.email,
      sessionUserId: session.data?.user?.id ?? null,
    };
  });
}
