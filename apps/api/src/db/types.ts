import type { PgDatabase, PgQueryResultHKT } from "drizzle-orm/pg-core";
import type * as schema from "./schema/index.js";

/**
 * The database handle the services use. A transaction is also a Db, so a service works the same inside or outside one. Both the node-postgres
 * driver (production) and PGlite (tests) fit this type.
 */
export type Db = PgDatabase<PgQueryResultHKT, typeof schema>;
