import { z } from "zod";

/** The one error body every endpoint returns (guidelines section 9.4). */
export const errorBody = z.object({
  error: z.object({ code: z.string(), message: z.string(), requestId: z.string() }),
});
export type ErrorBody = z.infer<typeof errorBody>;

/** An error that is safe to show a client: a stable code, a plain message and the HTTP status. */
export class AppError extends Error {
  constructor(
    public readonly statusCode: number,
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "AppError";
  }
}

export function body(code: string, message: string, requestId: string): ErrorBody {
  return { error: { code, message, requestId } };
}
